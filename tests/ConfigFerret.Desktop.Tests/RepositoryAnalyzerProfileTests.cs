using ConfigFerret.Desktop.Model;
using ConfigFerret.Desktop.Services;

namespace ConfigFerret.Desktop.Tests;

public class RepositoryAnalyzerProfileTests : IDisposable
{
    private static readonly AnalysisConnections NoConnections = new(null, string.Empty, "github", string.Empty, string.Empty, "octopus");
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("configferret-profile-");

    public void Dispose()
    {
        _temp.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Write(string relativePath, string text)
    {
        var path = Path.Join(_temp.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static string Deployment(string name, string containers, string? initContainers = null) =>
        $"apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: {name}\nspec:\n  template:\n    spec:\n"
        + (initContainers is null ? string.Empty : $"      initContainers:\n{initContainers}")
        + $"      containers:\n{containers}";

    private static string Container(string name, int env = 0, string? prefixes = null)
    {
        var text = $"        - name: {name}\n          image: {name}:1\n";
        if (env > 0)
        {
            text += "          env:\n" + string.Concat(Enumerable.Range(0, env).Select(i => $"            - name: V{i}\n              value: x\n"));
        }

        return prefixes is null ? text : text + "          envFrom:\n" + prefixes;
    }

    private static string EnvFrom(string? prefix) =>
        "            - configMapRef:\n                name: cm\n" + (prefix is null ? string.Empty : $"              prefix: {prefix}\n");

    private (Profile Profile, List<string> Notes) Build(RepositoryAnalyzer analyzer, AnalysisChoices? choices = null, AnalysisConnections? connections = null)
    {
        var scan = RepositoryScanner.Scan(_temp.FullName);
        var notes = new List<string>();
        return (analyzer.BuildProfile(scan, choices ?? new AnalysisChoices(), connections ?? NoConnections, notes), notes);
    }

    private static RepositoryAnalyzer NoTools() => new((_, _) => null);

    [Fact]
    public void Building_needs_a_scan_choices_connections_and_notes()
    {
        var scan = RepositoryScanner.Scan(_temp.FullName);
        var analyzer = NoTools();

        Assert.Throws<ArgumentNullException>(() => analyzer.BuildProfile(null!, new AnalysisChoices(), NoConnections, []));
        Assert.Throws<ArgumentNullException>(() => analyzer.BuildProfile(scan, null!, NoConnections, []));
        Assert.Throws<ArgumentNullException>(() => analyzer.BuildProfile(scan, new AnalysisChoices(), null!, []));
        Assert.Throws<ArgumentNullException>(() => analyzer.BuildProfile(scan, new AnalysisChoices(), NoConnections, null!));
    }

    [Fact]
    public void An_empty_repository_gives_an_empty_profile_named_after_the_folder()
    {
        var (profile, notes) = Build(NoTools());

        Assert.Equal(Path.GetFileName(_temp.FullName), profile.Name);
        Assert.Empty(profile.Sources);
        Assert.Equal(TraceMode.App, profile.Mode);
        Assert.Empty(notes);
    }

    [Fact]
    public void An_app_gets_an_app_settings_source_with_the_chosen_environment()
    {
        Write("src/Api/appsettings.json", "{}");
        var scan = RepositoryScanner.Scan(_temp.FullName);

        var profile = NoTools().BuildProfile(scan, new AnalysisChoices { App = "src/Api", Environment = "Staging" }, NoConnections, []);

        var app = Assert.IsType<AppSettingsSource>(Assert.Single(profile.Sources));
        Assert.Equal("App settings src/Api", app.Name);
        Assert.Equal("Staging", app.EnvironmentName);
        Assert.Equal(scan.Apps[0].Folder, app.Folder);
        Assert.False(app.UseUserSecrets);
        Assert.Equal(TraceMode.App, profile.Mode);
    }

    [Fact]
    public void An_app_that_was_not_chosen_is_left_out()
    {
        Write("src/Api/appsettings.json", "{}");

        var (profile, notes) = Build(NoTools(), new AnalysisChoices { App = "src/Other" });

        Assert.Empty(profile.Sources);
        Assert.Empty(notes);
    }

    [Fact]
    public void Plain_manifests_are_traced_as_they_are_and_an_app_goes_first()
    {
        Write("src/Api/appsettings.json", "{}");
        var manifest = Write("k8s/pod.yaml", Deployment("api", Container("api")));

        var (profile, _) = Build(NoTools(), new AnalysisChoices { App = "src/Api" });

        Assert.Equal([SourceKind.AppSettings, SourceKind.Kubernetes], profile.Sources.Select(s => s.Kind));
        var kubernetes = Assert.IsType<KubernetesSource>(profile.Sources[1]);
        Assert.Equal("Kubernetes manifests", kubernetes.Name);
        Assert.Equal([Path.GetFullPath(manifest)], kubernetes.Paths);
    }

    [Fact]
    public void Manifests_without_an_app_are_traced_as_a_container_and_say_so()
    {
        Write("k8s/pod.yaml", Deployment("api", Container("api")));

        var (profile, notes) = Build(NoTools());

        Assert.Equal(TraceMode.Container, profile.Mode);
        Assert.Equal("No appsettings.json was found, so the container's environment variables and files are traced from the manifests.", Assert.Single(notes));
    }

    [Fact]
    public void A_chart_is_rendered_with_the_values_file_and_named_after_its_folder()
    {
        Write("deploy/chart/Chart.yaml", "name: demo\n");
        Write("deploy/values-prod.yaml", "a: 1\n");
        var calls = new List<(string Tool, string[] Arguments)>();
        var analyzer = new RepositoryAnalyzer((tool, arguments) =>
        {
            calls.Add((tool, [.. arguments]));
            return Deployment("api", Container("api"));
        });
        var scan = RepositoryScanner.Scan(_temp.FullName);

        var notes = new List<string>();
        var profile = analyzer.BuildProfile(scan, new AnalysisChoices { HelmChart = "deploy/chart", HelmValues = "deploy/values-prod.yaml" }, NoConnections, notes);

        var call = Assert.Single(calls);
        Assert.Equal("helm", call.Tool);
        Assert.Equal(["template", "release", scan.HelmCharts[0].Folder, "-f", scan.Full("deploy/values-prod.yaml")], call.Arguments);
        var helm = Assert.IsType<HelmSource>(profile.Sources.Single(s => s is HelmSource));
        Assert.Equal("Helm chart deploy/chart", helm.Name);
        Assert.Equal([scan.Full("deploy/values-prod.yaml")], helm.ValuesFiles);
        var kubernetes = Assert.IsType<KubernetesSource>(profile.Sources.Single(s => s is KubernetesSource));
        Assert.Equal("Rendered manifests", kubernetes.Name);
        Assert.Equal("helm-template.yaml", Path.GetFileName(Assert.Single(kubernetes.Paths)));
        Assert.DoesNotContain(notes, n => n.StartsWith("Install", StringComparison.Ordinal));
    }

    [Fact]
    public void A_chart_without_a_values_file_is_rendered_with_its_own_values()
    {
        Write("chart/Chart.yaml", "name: demo\n");
        var calls = new List<string[]>();
        var analyzer = new RepositoryAnalyzer((_, arguments) =>
        {
            calls.Add([.. arguments]);
            return null;
        });

        var (profile, notes) = Build(analyzer, new AnalysisChoices { HelmChart = "chart" });

        Assert.Equal(["template", "release", Path.Join(_temp.FullName, "chart")], Assert.Single(calls));
        Assert.Empty(Assert.IsType<HelmSource>(profile.Sources.Single()).ValuesFiles);
        Assert.StartsWith("Install helm", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Fact]
    public void A_chart_that_was_not_chosen_is_not_rendered()
    {
        Write("chart/Chart.yaml", "name: demo\n");
        var called = false;

        var (profile, _) = Build(new RepositoryAnalyzer((_, _) => { called = true; return null; }), new AnalysisChoices { HelmChart = "other" });

        Assert.False(called);
        Assert.Empty(profile.Sources);
    }

    [Fact]
    public void A_kustomize_overlay_is_built_with_kubectl_first()
    {
        Write("k8s/overlay/kustomization.yaml", "resources: []\n");
        var calls = new List<(string Tool, string[] Arguments)>();
        var analyzer = new RepositoryAnalyzer((tool, arguments) =>
        {
            calls.Add((tool, [.. arguments]));
            return Deployment("api", Container("api"));
        });
        var scan = RepositoryScanner.Scan(_temp.FullName);

        var profile = analyzer.BuildProfile(scan, new AnalysisChoices { Kustomization = "k8s/overlay" }, NoConnections, []);

        var call = Assert.Single(calls);
        Assert.Equal("kubectl", call.Tool);
        Assert.Equal(["kustomize", scan.Full("k8s/overlay")], call.Arguments);
        var kustomize = Assert.IsType<KustomizeSource>(profile.Sources.Single(s => s is KustomizeSource));
        Assert.Equal("Kustomize k8s/overlay", kustomize.Name);
        Assert.Equal(scan.Full("k8s/overlay"), kustomize.Folder);
        var kubernetes = Assert.IsType<KubernetesSource>(profile.Sources.Single(s => s is KubernetesSource));
        Assert.Equal("Rendered manifests", kubernetes.Name);
        Assert.Equal("kustomize-build.yaml", Path.GetFileName(Assert.Single(kubernetes.Paths)));
    }

    [Fact]
    public void Kustomize_is_tried_when_kubectl_is_missing()
    {
        Write("k8s/overlay/kustomization.yaml", "resources: []\n");
        var calls = new List<(string Tool, string[] Arguments)>();
        var analyzer = new RepositoryAnalyzer((tool, arguments) =>
        {
            calls.Add((tool, [.. arguments]));
            return tool == "kustomize" ? Deployment("api", Container("api")) : null;
        });
        var scan = RepositoryScanner.Scan(_temp.FullName);

        var profile = analyzer.BuildProfile(scan, new AnalysisChoices { Kustomization = "k8s/overlay" }, NoConnections, []);

        Assert.Equal(["kubectl", "kustomize"], calls.Select(c => c.Tool));
        Assert.Equal(["build", scan.Full("k8s/overlay")], calls[1].Arguments);
        Assert.Equal("Rendered manifests", profile.Sources.OfType<KubernetesSource>().Single().Name);
    }

    [Fact]
    public void Without_kubectl_or_kustomize_the_overlay_is_not_built_and_the_user_is_told()
    {
        Write("k8s/overlay/kustomization.yaml", "resources: []\n");

        var (profile, notes) = Build(NoTools(), new AnalysisChoices { Kustomization = "k8s/overlay" });

        Assert.Equal("Install kubectl or kustomize to build the overlay, so its generators link to the app's settings.", Assert.Single(notes));
        Assert.Equal(SourceKind.Kustomize, Assert.Single(profile.Sources).Kind);
    }

    [Fact]
    public void A_rendered_chart_means_the_overlay_is_not_built()
    {
        Write("chart/Chart.yaml", "name: demo\n");
        Write("k8s/overlay/kustomization.yaml", "resources: []\n");
        var tools = new List<string>();
        var analyzer = new RepositoryAnalyzer((tool, _) =>
        {
            tools.Add(tool);
            return Deployment("api", Container("api"));
        });

        var (profile, _) = Build(analyzer, new AnalysisChoices { HelmChart = "chart", Kustomization = "k8s/overlay" });

        Assert.Equal(["helm"], tools);
        Assert.Equal([SourceKind.Kubernetes, SourceKind.Helm, SourceKind.Kustomize], profile.Sources.Select(s => s.Kind));
    }

    [Fact]
    public void An_overlay_is_built_when_the_chart_could_not_be_rendered()
    {
        Write("chart/Chart.yaml", "name: demo\n");
        Write("k8s/overlay/kustomization.yaml", "resources: []\n");
        var tools = new List<string>();
        var analyzer = new RepositoryAnalyzer((tool, _) =>
        {
            tools.Add(tool);
            return tool == "kubectl" ? Deployment("api", Container("api")) : null;
        });

        var (_, notes) = Build(analyzer, new AnalysisChoices { HelmChart = "chart", Kustomization = "k8s/overlay" });

        Assert.Equal(["helm", "kubectl"], tools);
        Assert.Single(notes, n => n.StartsWith("Install helm", StringComparison.Ordinal));
        Assert.DoesNotContain(notes, n => n.StartsWith("Install kubectl", StringComparison.Ordinal));
    }

    [Fact]
    public void Connections_become_sources_named_for_what_they_read()
    {
        var connections = new AnalysisConnections("contoso/orders", "https://ghe.example.test/api/v3/", "gh-account", "https://octopus.example.test/", "Spaces-2", "octo-account");
        Write("a/.github/workflows/ci.yml", "name: ci\n");
        Write(".github/workflows/ci.yml", "name: ci\n");

        var (profile, _) = Build(NoTools(), new AnalysisChoices { GitHubEnvironment = "production", OctopusProject = "Orders", OctopusEnvironment = "Prod" }, connections);

        var github = Assert.IsType<GitHubSource>(profile.Sources[0]);
        Assert.Equal("GitHub Actions variables", github.Name);
        Assert.Equal(("contoso/orders", "production", "https://ghe.example.test/api/v3/", "gh-account"), (github.Repository, github.Environment, github.ApiUrl, github.CredentialAccountName));
        Assert.Equal([Path.Join(_temp.FullName, ".github", "workflows", "ci.yml")], github.Workflows);
        var octopus = Assert.IsType<OctopusSource>(profile.Sources[1]);
        Assert.Equal("Octopus project Orders", octopus.Name);
        Assert.Equal(("Orders", "Prod", "Spaces-2", "octo-account"), (octopus.Project, octopus.Environment, octopus.Space, octopus.CredentialAccountName));
    }

    [Fact]
    public void Unset_environments_are_empty_text()
    {
        var connections = NoConnections with { GitHubRepository = "contoso/orders", OctopusServer = "https://octopus.example.test/" };

        var (profile, _) = Build(NoTools(), new AnalysisChoices { OctopusProject = "Orders" }, connections);

        Assert.Equal(string.Empty, profile.Sources.OfType<GitHubSource>().Single().Environment);
        Assert.Equal(string.Empty, profile.Sources.OfType<OctopusSource>().Single().Environment);
    }

    [Theory]
    [InlineData("", "Orders")]
    [InlineData("  ", "Orders")]
    [InlineData("https://octopus.example.test/", "")]
    [InlineData("https://octopus.example.test/", " ")]
    [InlineData("https://octopus.example.test/", null)]
    public void Octopus_needs_a_server_and_a_project(string server, string? project)
    {
        var (profile, _) = Build(NoTools(), new AnalysisChoices { OctopusProject = project }, NoConnections with { OctopusServer = server });

        Assert.Empty(profile.Sources);
    }

    // Choosing the workload and container

    [Fact]
    public void The_workload_named_like_the_app_is_traced_and_the_others_are_noted()
    {
        Write("src/Orders/appsettings.json", "{}");
        Write("k8s/all.yaml", Deployment("worker", Container("worker", env: 5)) + "---\n" + Deployment("orders-api", Container("orders-api")) + "---\n" + Deployment("cron", Container("cron")));

        var (profile, notes) = Build(NoTools(), new AnalysisChoices { App = "src/Orders" });

        var kubernetes = profile.Sources.OfType<KubernetesSource>().Single();
        Assert.Equal("orders-api", kubernetes.Workload);
        Assert.Equal("The manifests hold 3 workloads; traced Deployment/orders-api.", Assert.Single(notes));
    }

    [Fact]
    public void Workloads_that_match_equally_are_ordered_by_how_much_environment_they_set()
    {
        Write("k8s/all.yaml", Deployment("wkra", Container("wkra", env: 1)) + "---\n" + Deployment("wkrb", Container("wkrb", env: 3)) + "---\n" + Deployment("wkrc", Container("wkrc", env: 2)));

        var (profile, _) = Build(NoTools());

        Assert.Equal("wkrb", profile.Sources.OfType<KubernetesSource>().Single().Workload);
    }

    [Fact]
    public void Environment_counts_add_up_over_all_of_a_workloads_containers()
    {
        Write("k8s/all.yaml", Deployment("wkra", Container("cnta", env: 2) + Container("cntb", env: 2)) + "---\n" + Deployment("wkrb", Container("cntc", env: 3)));

        var (profile, _) = Build(NoTools());

        Assert.Equal("wkra", profile.Sources.OfType<KubernetesSource>().Single().Workload);
    }

    [Fact]
    public void Init_containers_do_not_count_as_the_app()
    {
        Write("k8s/all.yaml", Deployment("only-init", Container("main"), initContainers: Container("init", env: 9)) + "---\n" + Deployment("other", Container("other", env: 1)));

        var (profile, _) = Build(NoTools());

        var kubernetes = profile.Sources.OfType<KubernetesSource>().Single();
        Assert.Equal("only-init", kubernetes.Workload);
        Assert.Equal(string.Empty, kubernetes.Container);
    }

    [Fact]
    public void A_container_is_named_only_when_the_workload_has_several()
    {
        Write("k8s/all.yaml", Deployment("orders", Container("proxy", env: 5) + Container("orders", env: 1) + Container("logger")));

        var (profile, notes) = Build(NoTools());

        var kubernetes = profile.Sources.OfType<KubernetesSource>().Single();
        Assert.Equal("orders", kubernetes.Container);
        Assert.Equal(string.Empty, kubernetes.Workload);
        Assert.DoesNotContain(notes, n => n.Contains("workloads", StringComparison.Ordinal));
    }

    [Fact]
    public void A_container_named_like_its_workload_beats_one_with_more_environment()
    {
        Write("k8s/all.yaml", Deployment("orders", Container("sidecar", env: 5) + Container("orders")));

        var (profile, _) = Build(NoTools());

        Assert.Equal("orders", profile.Sources.OfType<KubernetesSource>().Single().Container);
    }

    [Fact]
    public void Containers_that_match_equally_are_ordered_by_environment()
    {
        Write("k8s/all.yaml", Deployment("web", Container("cxa", env: 1) + Container("cxb", env: 4) + Container("cxc", env: 2)));

        var (profile, _) = Build(NoTools());

        Assert.Equal("cxb", profile.Sources.OfType<KubernetesSource>().Single().Container);
    }

    [Fact]
    public void The_container_is_picked_by_the_app_name_when_it_matches()
    {
        Write("src/Billing/appsettings.json", "{}");
        Write("k8s/all.yaml", Deployment("platform", Container("proxy", env: 5) + Container("billing", env: 1)));

        var (profile, _) = Build(NoTools(), new AnalysisChoices { App = "src/Billing" });

        Assert.Equal("billing", profile.Sources.OfType<KubernetesSource>().Single().Container);
    }

    [Fact]
    public void Prefixes_come_from_the_env_from_entries_of_the_traced_container_without_duplicates_or_blanks()
    {
        Write("src/Orders/appsettings.json", "{}");
        Write("k8s/all.yaml", Deployment("orders", Container("orders", prefixes: EnvFrom("ORDERS_") + EnvFrom("ORDERS_") + EnvFrom(null) + EnvFrom("CACHE_"))));
        Write("src/Orders/Program.cs", "builder.Configuration.AddEnvironmentVariables(\"CODE_\");");

        var (profile, _) = Build(NoTools(), new AnalysisChoices { App = "src/Orders" });

        Assert.Equal(["ORDERS_", "CACHE_"], profile.Sources.OfType<AppSettingsSource>().Single().EnvironmentPrefixes);
    }

    [Fact]
    public void Manifests_that_cannot_be_read_are_still_listed_without_a_pick()
    {
        Write("k8s/pod.yaml", "apiVersion: v1\nkind: Pod\nmetadata:\n  name: x\nspec:\n  containers: [unclosed\n");

        var (profile, notes) = Build(NoTools());

        var kubernetes = Assert.IsType<KubernetesSource>(Assert.Single(profile.Sources));
        Assert.Equal(string.Empty, kubernetes.Workload);
        Assert.Equal(string.Empty, kubernetes.Container);
        Assert.DoesNotContain(notes, n => n.Contains("workloads", StringComparison.Ordinal));
    }

    [Fact]
    public void Manifests_without_workloads_leave_the_pick_empty()
    {
        Write("k8s/cm.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: cm\ndata:\n  a: b\n");

        var (profile, notes) = Build(NoTools());

        var kubernetes = Assert.IsType<KubernetesSource>(Assert.Single(profile.Sources));
        Assert.Equal(string.Empty, kubernetes.Workload);
        Assert.DoesNotContain(notes, n => n.Contains("workloads", StringComparison.Ordinal));
    }

    // Prefixes in the app's code

    private AppSettingsSource AppFor(string codePath, string code, AnalysisChoices? choices = null)
    {
        Write("src/Orders/appsettings.json", "{}");
        Write(codePath, code);
        var (profile, _) = Build(NoTools(), choices ?? new AnalysisChoices { App = "src/Orders" });
        return profile.Sources.OfType<AppSettingsSource>().Single();
    }

    [Theory]
    [InlineData("config.AddEnvironmentVariables(\"ORDERS_\")", "ORDERS_")]
    [InlineData("config.AddEnvironmentVariables( \"ORDERS_\" )", "ORDERS_")]
    [InlineData("config.AddEnvironmentVariables(prefix: \"ORDERS_\")", "ORDERS_")]
    [InlineData("config.AddEnvironmentVariables(prefix:\"Orders9\")", "Orders9")]
    public void Finds_the_prefix_the_app_strips(string code, string expected)
    {
        Assert.Equal([expected], AppFor("src/Orders/Program.cs", code).EnvironmentPrefixes);
    }

    [Theory]
    [InlineData("config.AddEnvironmentVariables()")]
    [InlineData("config.AddEnvironmentVariables(\"\")")]
    [InlineData("config.AddEnvironmentVariables(prefix)")]
    [InlineData("config.AddEnvironmentVariables(\"A-B\")")]
    public void Finds_no_prefix_in_other_calls(string code)
    {
        Assert.Empty(AppFor("src/Orders/Program.cs", code).EnvironmentPrefixes);
    }

    [Fact]
    public void Lists_each_prefix_once_across_files()
    {
        Write("src/Orders/B.cs", "x.AddEnvironmentVariables(\"B_\");");
        var app = AppFor("src/Orders/A.cs", "x.AddEnvironmentVariables(\"A_\"); x.AddEnvironmentVariables(\"B_\");");

        Assert.Equal(["A_", "B_"], app.EnvironmentPrefixes.Order());
    }

    [Fact]
    public void Looks_two_folders_down_and_no_further()
    {
        Write("src/Orders/a/b/Deep.cs", "x.AddEnvironmentVariables(\"TWO_\");");
        Write("src/Orders/a/b/c/Deeper.cs", "x.AddEnvironmentVariables(\"THREE_\");");

        var app = AppFor("src/Orders/a/Mid.cs", "x.AddEnvironmentVariables(\"ONE_\");");

        Assert.Equal(["ONE_", "TWO_"], app.EnvironmentPrefixes.Order());
    }

    [Fact]
    public void Skips_code_files_over_half_a_megabyte()
    {
        const int limit = 512 * 1024;
        const string call = "x.AddEnvironmentVariables(\"EXACT_\");";
        Write("src/Orders/Exact.cs", call + new string(' ', limit - call.Length));
        var app = AppFor("src/Orders/Big.cs", "x.AddEnvironmentVariables(\"BIG_\");" + new string(' ', limit));

        Assert.Equal(["EXACT_"], app.EnvironmentPrefixes);
    }

    [Fact]
    public void Only_reads_cs_files()
    {
        Write("src/Orders/Notes.txt", "x.AddEnvironmentVariables(\"TEXT_\");");

        Assert.Empty(AppFor("src/Orders/Program.vb", "x.AddEnvironmentVariables(\"VB_\")").EnvironmentPrefixes);
    }

    [Fact]
    public void Manifest_prefixes_win_over_prefixes_in_code()
    {
        Write("src/Orders/Program.cs", "x.AddEnvironmentVariables(\"CODE_\");");
        Write("k8s/all.yaml", Deployment("orders", Container("orders", prefixes: EnvFrom("K8S_"))));
        Write("src/Orders/appsettings.json", "{}");

        var (profile, _) = Build(NoTools(), new AnalysisChoices { App = "src/Orders" });

        Assert.Equal(["K8S_"], profile.Sources.OfType<AppSettingsSource>().Single().EnvironmentPrefixes);
    }

    // Running tools

    [Fact]
    public void A_tool_that_succeeds_gives_its_output()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal("a b|c", RepositoryAnalyzer.RunTool("sh", ["-c", "printf '%s|%s' \"$0\" \"$1\"", "a b", "c"]));
    }

    [Fact]
    public void A_tool_that_fails_or_is_missing_gives_nothing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Null(RepositoryAnalyzer.RunTool("sh", ["-c", "echo output; exit 3"]));
        Assert.Null(RepositoryAnalyzer.RunTool("configferret-no-such-tool", []));
    }

    [Fact]
    public void A_tools_error_output_does_not_get_in_the_way()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal("out\n", RepositoryAnalyzer.RunTool("sh", ["-c", "echo err >&2; echo out"]));
    }
}
