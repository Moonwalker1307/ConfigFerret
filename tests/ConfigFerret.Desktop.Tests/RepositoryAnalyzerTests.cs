using ConfigFerret.Desktop.Credentials;
using ConfigFerret.Desktop.Model;
using ConfigFerret.Desktop.Services;

namespace ConfigFerret.Desktop.Tests;

public class RepositoryAnalyzerTests : IDisposable
{
    private static readonly AnalysisConnections NoConnections = new(null, string.Empty, "github", string.Empty, string.Empty, "octopus");
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("configferret-analyze-");

    public void Dispose()
    {
        _temp.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Suggests_production_the_prod_values_file_and_the_production_github_environment()
    {
        var scan = RepositoryScanner.Scan(OrdersRepo.Root);
        var choices = new AnalysisChoices { Environment = "Staging" };

        RepositoryAnalyzer.SuggestChoices(scan, choices, ["staging", "production"]);

        Assert.Equal("src/Orders.Api", choices.App);
        Assert.Equal("Production", choices.Environment);
        Assert.Equal("deploy/chart", choices.HelmChart);
        Assert.Equal("deploy/values-prod.yaml", choices.HelmValues);
        Assert.Equal("production", choices.GitHubEnvironment);
        Assert.Null(choices.Kustomization);
    }

    [Fact]
    public void Keeps_a_choice_that_is_still_valid()
    {
        var scan = RepositoryScanner.Scan(OrdersRepo.Root);
        var choices = new AnalysisChoices { Environment = "Development", HelmValues = "deploy/values-eu.yaml", GitHubEnvironment = "staging" };

        RepositoryAnalyzer.SuggestChoices(scan, choices, ["staging", "production"]);

        Assert.Equal("Development", choices.Environment);
        Assert.Equal("deploy/values-eu.yaml", choices.HelmValues);
        Assert.Equal("staging", choices.GitHubEnvironment);
    }

    [Fact]
    public void Leaves_the_github_environment_empty_when_none_matches()
    {
        var scan = RepositoryScanner.Scan(OrdersRepo.Root);
        var choices = new AnalysisChoices();

        RepositoryAnalyzer.SuggestChoices(scan, choices, ["qa"]);

        Assert.Null(choices.GitHubEnvironment);
    }

    [Fact]
    public void Builds_the_profile_without_helm_installed()
    {
        var (profile, notes) = Build(new RepositoryAnalyzer((_, _) => null), Connections(gitHub: "contoso/orders"));

        Assert.Equal("orders-repo", profile.Name);
        Assert.Equal(TraceMode.App, profile.Mode);
        Assert.Equal([SourceKind.AppSettings, SourceKind.Kubernetes, SourceKind.Helm, SourceKind.GitHub], profile.Sources.Select(s => s.Kind));

        var app = Assert.IsType<AppSettingsSource>(profile.Sources[0]);
        Assert.Equal(OrdersRepo.Full("src/Orders.Api"), app.Folder);
        Assert.Equal("Production", app.EnvironmentName);
        Assert.Equal(["ORDERS_"], app.EnvironmentPrefixes);
        Assert.False(app.UseUserSecrets);

        var kubernetes = Assert.IsType<KubernetesSource>(profile.Sources[1]);
        Assert.Equal([OrdersRepo.Full("deploy/rendered.yaml")], kubernetes.Paths);
        Assert.Equal("orders", kubernetes.Container);
        Assert.Equal(string.Empty, kubernetes.Workload);

        var helm = Assert.IsType<HelmSource>(profile.Sources[2]);
        Assert.Equal(OrdersRepo.Full("deploy/chart"), helm.ChartFolder);
        Assert.Equal([OrdersRepo.Full("deploy/values-prod.yaml")], helm.ValuesFiles);

        var github = Assert.IsType<GitHubSource>(profile.Sources[3]);
        Assert.Equal("contoso/orders", github.Repository);
        Assert.Equal("production", github.Environment);
        Assert.Equal("github", github.CredentialAccountName);
        Assert.Equal([OrdersRepo.Full(".github/workflows/deploy.yml")], github.Workflows);

        Assert.Contains(notes, n => n.Contains("Install helm", StringComparison.Ordinal));
    }

    [Fact]
    public void Adds_no_github_source_for_a_local_folder_and_no_octopus_source_without_a_server()
    {
        var (profile, _) = Build(new RepositoryAnalyzer((_, _) => null), NoConnections);

        Assert.DoesNotContain(profile.Sources, s => s is GitHubSource);
        Assert.DoesNotContain(profile.Sources, s => s is OctopusSource);
    }

    [Fact]
    public void Adds_an_octopus_source_only_when_the_server_and_a_project_are_set()
    {
        var connections = NoConnections with { OctopusServer = "https://octopus.example.test/", OctopusSpace = "Spaces-2" };

        var (withoutProject, _) = Build(new RepositoryAnalyzer((_, _) => null), connections);
        var (withProject, _) = Build(new RepositoryAnalyzer((_, _) => null), connections, c => { c.OctopusProject = "Orders"; c.OctopusEnvironment = "Production"; });

        Assert.DoesNotContain(withoutProject.Sources, s => s is OctopusSource);
        var octopus = Assert.IsType<OctopusSource>(withProject.Sources[^1]);
        Assert.Equal("https://octopus.example.test/", octopus.Server);
        Assert.Equal("Spaces-2", octopus.Space);
        Assert.Equal("Orders", octopus.Project);
        Assert.Equal("Production", octopus.Environment);
        Assert.Equal("octopus", octopus.CredentialAccountName);
    }

    [Fact]
    public void Uses_the_rendered_chart_when_helm_is_installed()
    {
        var calls = new List<(string Tool, string[] Arguments)>();
        var yaml = File.ReadAllText(OrdersRepo.Full("deploy/rendered.yaml"));
        var analyzer = new RepositoryAnalyzer((tool, arguments) =>
        {
            calls.Add((tool, [.. arguments]));
            return tool == "helm" ? yaml : null;
        });

        var (profile, notes) = Build(analyzer, NoConnections);

        var call = Assert.Single(calls);
        Assert.Equal("helm", call.Tool);
        Assert.Equal(["template", "release", OrdersRepo.Full("deploy/chart"), "-f", OrdersRepo.Full("deploy/values-prod.yaml")], call.Arguments);

        var kubernetes = Assert.IsType<KubernetesSource>(profile.Sources.Single(s => s is KubernetesSource));
        Assert.Equal("Rendered manifests", kubernetes.Name);
        Assert.Empty(kubernetes.Paths);
        Assert.Equal(("helm-template.yaml", yaml), kubernetes.Rendered);
        Assert.Equal("orders", kubernetes.Container);
        Assert.DoesNotContain(notes, n => n.Contains("Install helm", StringComparison.Ordinal));
        Assert.Equal(["ORDERS_"], Assert.IsType<AppSettingsSource>(profile.Sources[0]).EnvironmentPrefixes);
    }

    [Fact]
    public void Finds_the_environment_prefix_in_the_app_code_when_the_manifests_give_none()
    {
        var scan = RepositoryScanner.Scan(OrdersRepo.Root);
        var choices = new AnalysisChoices { App = "src/Orders.Api", HelmChart = null };
        var emptyScan = scan with { Manifests = [], HelmCharts = [], Kustomizations = [] };

        var profile = new RepositoryAnalyzer((_, _) => null).BuildProfile(emptyScan, choices, NoConnections, []);

        Assert.Equal(["ORDERS_"], Assert.IsType<AppSettingsSource>(Assert.Single(profile.Sources)).EnvironmentPrefixes);
    }

    [Fact]
    public void Traces_the_container_when_there_is_no_app_settings_file()
    {
        var folder = Directory.CreateDirectory(Path.Join(_temp.FullName, "deploy")).FullName;
        File.WriteAllText(Path.Join(folder, "pod.yaml"), "apiVersion: v1\nkind: Pod\nmetadata:\n  name: web\nspec:\n  containers:\n    - name: web\n      image: web:1\n");
        var scan = RepositoryScanner.Scan(_temp.FullName);
        var notes = new List<string>();

        var profile = new RepositoryAnalyzer((_, _) => null).BuildProfile(scan, new AnalysisChoices(), NoConnections, notes);

        Assert.Equal(TraceMode.Container, profile.Mode);
        Assert.Equal(SourceKind.Kubernetes, Assert.Single(profile.Sources).Kind);
        Assert.Contains(notes, n => n.Contains("No appsettings.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Notes_which_workload_was_traced_when_the_manifests_hold_several()
    {
        File.WriteAllText(
            Path.Join(_temp.FullName, "apps.yaml"),
            "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: worker\nspec:\n  template:\n    spec:\n      containers:\n        - name: worker\n          image: w:1\n---\n"
            + "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: orders\nspec:\n  template:\n    spec:\n      containers:\n        - name: orders\n          image: o:1\n          env:\n            - name: A\n              value: b\n");
        var folder = Directory.CreateDirectory(Path.Join(_temp.FullName, "src", "Orders")).FullName;
        File.WriteAllText(Path.Join(folder, "appsettings.json"), "{}");
        var scan = RepositoryScanner.Scan(_temp.FullName);
        var notes = new List<string>();

        var profile = new RepositoryAnalyzer((_, _) => null).BuildProfile(scan, new AnalysisChoices { App = "src/Orders" }, NoConnections, notes);

        var kubernetes = Assert.IsType<KubernetesSource>(profile.Sources.Single(s => s is KubernetesSource));
        Assert.Equal("orders", kubernetes.Workload);
        Assert.Contains(notes, n => n.Contains("2 workloads", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Traces_the_rendered_manifests_without_writing_them_to_disk()
    {
        // Rendered manifests can hold secrets from the chart's values, so no copy of them may be left behind.
        var yaml = File.ReadAllText(OrdersRepo.Full("deploy/rendered.yaml")).Replace("orders-db.prod.svc", "orders-db.rendered.svc", StringComparison.Ordinal);
        var (profile, _) = Build(new RepositoryAnalyzer((tool, _) => tool == "helm" ? yaml : null), NoConnections);

        var result = await new TraceRunner(new InMemoryCredentialStore(), _ => null).RunAsync(profile, revealSecrets: false, TestContext.Current.CancellationToken);

        Assert.Equal("orders-db.rendered.svc", result.Report.Find("Db:Host")?.Value);
        var repository = OrdersRepo.Full(".") + Path.DirectorySeparatorChar;
        Assert.All(profile.Sources.OfType<KubernetesSource>().SelectMany(k => k.Paths), p => Assert.StartsWith(repository, p, StringComparison.Ordinal));
    }

    [Fact]
    public void Runs_tools_without_a_shell_and_without_network_access()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses /bin/sh as the tool.");

        // The argument reaches the tool as one argument, so $(...) in it is not run by a shell.
        var output = RepositoryAnalyzer.RunTool("/bin/sh", ["-c", "printf '%s|%s|%s|%s' \"$HTTPS_PROXY\" \"$http_proxy\" \"$GIT_ALLOW_PROTOCOL\" \"$1\"", "sh", "$(echo injected)"]);

        Assert.Equal("http://127.0.0.1:9|http://127.0.0.1:9|file|$(echo injected)", output);
    }

    [Fact]
    public void Gives_tools_only_the_environment_variables_they_need_to_run()
    {
        // A kustomize env file line without '=' reads the variable from the tool's environment, so the server's tokens must not be in it.
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = "/opt/tools/bin",
            ["HOME"] = "/home/fake",
            ["TMPDIR"] = "/tmp/fake",
            ["HELM_CACHE_HOME"] = "/tmp/fake/helm",
            ["GITHUB_TOKEN"] = "ghp_fake_server_token",
            ["OCTOPUS_API_KEY"] = "API-FAKESERVERKEY",
            ["AWS_SECRET_ACCESS_KEY"] = "fake-aws-secret",
            ["HELM_KUBETOKEN"] = "fake-kube-token",
            ["KUBECONFIG"] = "/home/fake/.kube/config",
            ["HTTPS_PROXY"] = "http://user:fake-password@proxy.example.test:3128",
        };

        var start = RepositoryAnalyzer.StartInfo("helm", ["template"], name => environment.GetValueOrDefault(name));

        var expected = new[]
        {
            "PATH", "HOME", "TMPDIR", "HELM_CACHE_HOME",
            "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy", "NO_PROXY", "no_proxy", "GIT_ALLOW_PROTOCOL", "GIT_TERMINAL_PROMPT",
        };
        // Windows names are case-insensitive, so http_proxy and HTTP_PROXY are one variable there.
        static IEnumerable<string> Names(IEnumerable<string> names) => names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Names(expected), Names(start.Environment.Keys), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("/opt/tools/bin", start.Environment["PATH"]);
        Assert.Equal("/home/fake", start.Environment["HOME"]);
        Assert.Equal("http://127.0.0.1:9", start.Environment["HTTPS_PROXY"]);
    }

    [Fact]
    public void Returns_null_for_a_tool_that_is_not_installed() =>
        Assert.Null(RepositoryAnalyzer.RunTool("configferret-no-such-tool", ["template"]));

    private static AnalysisConnections Connections(string? gitHub) => NoConnections with { GitHubRepository = gitHub };

    private static (Profile Profile, List<string> Notes) Build(RepositoryAnalyzer analyzer, AnalysisConnections connections, Action<AnalysisChoices>? adjust = null)
    {
        var scan = RepositoryScanner.Scan(OrdersRepo.Root);
        var choices = new AnalysisChoices();
        RepositoryAnalyzer.SuggestChoices(scan, choices, ["staging", "production"]);
        adjust?.Invoke(choices);
        var notes = new List<string>();
        return (analyzer.BuildProfile(scan, choices, connections, notes), notes);
    }
}
