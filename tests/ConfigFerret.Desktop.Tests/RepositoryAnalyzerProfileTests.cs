using ConfigFerret.Desktop.Model;
using ConfigFerret.Desktop.Services;

namespace ConfigFerret.Desktop.Tests;

public class RepositoryAnalyzerProfileTests : IDisposable
{
    private static readonly AnalysisConnections NoConnections = new(null, string.Empty, "github", string.Empty, string.Empty, "octopus");
    private readonly DirectoryInfo _temp;

    // The analyzer matches container names against the repository folder's name, so the scanned folder has a fixed
    // name. The random temp folder name could otherwise contain a short container name, such as cxa, and change the pick.
    private readonly DirectoryInfo _root;

    public RepositoryAnalyzerProfileTests()
    {
        _temp = Directory.CreateTempSubdirectory("configferret-profile-");
        _root = _temp.CreateSubdirectory("checkout");
    }

    public void Dispose()
    {
        _temp.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Write(string relativePath, string text)
    {
        var path = Path.Join(_root.FullName, relativePath);
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
        var scan = RepositoryScanner.Scan(_root.FullName);
        var notes = new List<string>();
        return (analyzer.BuildProfile(scan, choices ?? new AnalysisChoices(), connections ?? NoConnections, notes), notes);
    }

    private static RepositoryAnalyzer NoTools() => new((_, _) => null);

    [Fact]
    public void Building_needs_a_scan_choices_connections_and_notes()
    {
        var scan = RepositoryScanner.Scan(_root.FullName);
        var analyzer = NoTools();

        Assert.Throws<ArgumentNullException>(() => analyzer.BuildProfile(null!, new AnalysisChoices(), NoConnections, [], TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() => analyzer.BuildProfile(scan, null!, NoConnections, [], TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() => analyzer.BuildProfile(scan, new AnalysisChoices(), null!, [], TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() => analyzer.BuildProfile(scan, new AnalysisChoices(), NoConnections, null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void An_empty_repository_gives_an_empty_profile_named_after_the_folder()
    {
        var (profile, notes) = Build(NoTools());

        Assert.Equal(Path.GetFileName(_root.FullName), profile.Name);
        Assert.Empty(profile.Sources);
        Assert.Equal(TraceMode.App, profile.Mode);
        Assert.Empty(notes);
    }

    [Fact]
    public void An_app_gets_an_app_settings_source_with_the_chosen_environment()
    {
        Write("src/Api/appsettings.json", "{}");
        var scan = RepositoryScanner.Scan(_root.FullName);

        var profile = NoTools().BuildProfile(scan, new AnalysisChoices { App = "src/Api", Environment = "Staging" }, NoConnections, [], TestContext.Current.CancellationToken);

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
        var scan = RepositoryScanner.Scan(_root.FullName);

        var notes = new List<string>();
        var profile = analyzer.BuildProfile(scan, new AnalysisChoices { HelmChart = "deploy/chart", HelmValues = "deploy/values-prod.yaml" }, NoConnections, notes, TestContext.Current.CancellationToken);

        var call = Assert.Single(calls);
        Assert.Equal("helm", call.Tool);
        Assert.Equal(["template", "release", scan.HelmCharts[0].Folder, "-f", scan.Full("deploy/values-prod.yaml")], call.Arguments);
        var helm = Assert.IsType<HelmSource>(profile.Sources.Single(s => s is HelmSource));
        Assert.Equal("Helm chart deploy/chart", helm.Name);
        Assert.Equal([scan.Full("deploy/values-prod.yaml")], helm.ValuesFiles);
        var kubernetes = Assert.IsType<KubernetesSource>(profile.Sources.Single(s => s is KubernetesSource));
        Assert.Equal("Rendered manifests", kubernetes.Name);
        Assert.Equal("helm-template.yaml", kubernetes.Rendered?.Name);
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

        Assert.Equal(["template", "release", Path.Join(_root.FullName, "chart")], Assert.Single(calls));
        Assert.Empty(Assert.IsType<HelmSource>(profile.Sources.Single()).ValuesFiles);
        Assert.StartsWith("Install helm", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Fact]
    public void Says_which_chart_was_traced_when_the_repository_holds_several()
    {
        Write("charts/api/Chart.yaml", "name: api\n");
        Write("charts/worker/Chart.yaml", "name: worker\n");

        var (profile, notes) = Build(NoTools(), new AnalysisChoices { HelmChart = "charts/worker" });

        Assert.Equal("Helm chart charts/worker", profile.Sources.OfType<HelmSource>().Single().Name);
        Assert.Contains("The repository holds 2 Helm charts; traced charts/worker.", notes);
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
    public void A_chart_with_a_link_out_of_the_repository_is_not_rendered()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs extra rights on Windows.");
        Write("chart/Chart.yaml", "name: demo\n");
        var outside = Path.Join(_temp.FullName, "outside", "config");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "token: fake-kube-token\n");
        Directory.CreateDirectory(Path.Join(_root.FullName, "chart", "files"));
        File.CreateSymbolicLink(Path.Join(_root.FullName, "chart", "files", "leak"), outside);
        var called = false;

        var (profile, notes) = Build(new RepositoryAnalyzer((_, _) => { called = true; return Deployment("api", Container("api")); }), new AnalysisChoices { HelmChart = "chart" });

        Assert.False(called);
        var note = Assert.Single(notes);
        Assert.Equal(
            "helm was not run for the chart chart: chart/files/leak is a symbolic link that leads out of the repository, and helm would read what it leads to. Without the rendered chart its values are listed only where a ConfigMap or env entry in the repository uses them.",
            note);
        Assert.DoesNotContain("outside", note, StringComparison.Ordinal);
        Assert.Null(profile.Sources.OfType<KubernetesSource>().SingleOrDefault()?.Rendered);
    }

    [Fact]
    public void A_chart_with_a_link_to_a_folder_out_of_the_repository_is_not_rendered()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs extra rights on Windows.");
        Write("chart/Chart.yaml", "name: demo\n");
        var outside = Directory.CreateDirectory(Path.Join(_temp.FullName, "outside")).FullName;
        Directory.CreateSymbolicLink(Path.Join(_root.FullName, "chart", "files"), outside);
        var called = false;

        var (_, notes) = Build(new RepositoryAnalyzer((_, _) => { called = true; return null; }), new AnalysisChoices { HelmChart = "chart" });

        Assert.False(called);
        Assert.StartsWith("helm was not run for the chart chart: chart/files is a symbolic link that leads out of the repository", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_out_of_the_repository_behind_a_link_inside_it_is_found()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs extra rights on Windows.");
        Write("chart/Chart.yaml", "name: demo\n");
        Write("shared/readme.txt", "shared files\n");
        var outside = Path.Join(_temp.FullName, "outside-config");
        File.WriteAllText(outside, "token: fake-kube-token\n");
        File.CreateSymbolicLink(Path.Join(_root.FullName, "shared", "leak"), outside);
        Directory.CreateSymbolicLink(Path.Join(_root.FullName, "chart", "files"), Path.Join("..", "shared"));
        var called = false;

        var (_, notes) = Build(new RepositoryAnalyzer((_, _) => { called = true; return null; }), new AnalysisChoices { HelmChart = "chart" });

        Assert.False(called);
        Assert.StartsWith("helm was not run for the chart chart: chart/files/leak is a symbolic link that leads out of the repository", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_whose_name_is_not_utf8_is_not_hidden_by_a_folder_that_reads_the_same()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Only Linux keeps file names that are not valid UTF-8.");
        Write("chart/Chart.yaml", "name: demo\n");
        var outside = Directory.CreateDirectory(Path.Join(_temp.FullName, "outside")).FullName;
        var files = Directory.CreateDirectory(Path.Join(_root.FullName, "chart", "files")).FullName;

        // .NET reads the byte 0xFF as U+FFFD, so the link and the real folder named U+FFFD k both read as files/U+FFFD k.
        Shell("ln -s \"$1\" \"$2/$(printf '\\377k')\"", outside, files);
        Directory.CreateDirectory(Path.Join(files, "\uFFFDk"));
        try
        {
            var called = false;

            var (_, notes) = Build(new RepositoryAnalyzer((_, _) => { called = true; return null; }), new AnalysisChoices { HelmChart = "chart" });

            Assert.False(called);
            Assert.StartsWith("helm was not run for the chart chart: its files could not be checked for symbolic links that lead out of the repository.", Assert.Single(notes), StringComparison.Ordinal);
        }
        finally
        {
            // .NET cannot delete what it cannot name.
            Shell("rm -rf \"$1\"", files);
        }
    }

    [Fact]
    public void A_link_whose_name_is_not_utf8_is_not_rendered()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Only Linux keeps file names that are not valid UTF-8.");
        Write("chart/Chart.yaml", "name: demo\n");
        var outside = Path.Join(_temp.FullName, "outside-config");
        File.WriteAllText(outside, "token: fake-kube-token\n");
        var files = Directory.CreateDirectory(Path.Join(_root.FullName, "chart", "files")).FullName;
        Shell("ln -s \"$1\" \"$2/$(printf '\\377k')\"", outside, files);
        try
        {
            var called = false;

            var (_, notes) = Build(new RepositoryAnalyzer((_, _) => { called = true; return null; }), new AnalysisChoices { HelmChart = "chart" });

            Assert.False(called);
            Assert.StartsWith("helm was not run for the chart chart: its files could not be checked for symbolic links that lead out of the repository.", Assert.Single(notes), StringComparison.Ordinal);
        }
        finally
        {
            // .NET cannot delete what it cannot name.
            Shell("rm -rf \"$1\"", files);
        }
    }

    [Fact]
    public void A_link_through_a_link_target_that_is_not_utf8_is_not_rendered()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Only Linux keeps file names that are not valid UTF-8.");
        Write("chart/Chart.yaml", "name: demo\n");
        var outside = Directory.CreateDirectory(Path.Join(_temp.FullName, "outside")).FullName;
        File.WriteAllText(Path.Join(outside, "secret"), "token: fake-kube-token\n");
        var other = Directory.CreateDirectory(Path.Join(_root.FullName, "other")).FullName;

        // other/lnk leads to other/0xFF d, which leads out. .NET reads lnk's target as U+FFFD d, a folder that does not exist.
        Shell("ln -s \"$(printf '\\377d')\" \"$2/lnk\" && ln -s \"$1\" \"$2/$(printf '\\377d')\"", outside, other);
        Directory.CreateDirectory(Path.Join(_root.FullName, "chart", "files"));
        File.CreateSymbolicLink(Path.Join(_root.FullName, "chart", "files", "x"), Path.Join("..", "..", "other", "lnk", "secret"));
        try
        {
            var called = false;

            var (_, notes) = Build(new RepositoryAnalyzer((_, _) => { called = true; return null; }), new AnalysisChoices { HelmChart = "chart" });

            Assert.False(called);
            Assert.StartsWith("helm was not run for the chart chart: chart/files/x is a symbolic link that leads out of the repository", Assert.Single(notes), StringComparison.Ordinal);
        }
        finally
        {
            // .NET cannot delete what it cannot name.
            Shell("rm -rf \"$1\"", other);
        }
    }

    // Runs script with /bin/sh, with arguments as $1, $2 and so on, to make names .NET cannot write.
    private static void Shell(string script, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in (string[])["-c", script, "sh", .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public void A_chart_with_links_that_stay_in_the_repository_is_rendered()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs extra rights on Windows.");
        Write("chart/Chart.yaml", "name: demo\n");
        Write("shared/config.yaml", "level: info\n");
        Directory.CreateDirectory(Path.Join(_root.FullName, "chart", "files"));
        File.CreateSymbolicLink(Path.Join(_root.FullName, "chart", "files", "config.yaml"), Path.Join("..", "..", "shared", "config.yaml"));
        Directory.CreateSymbolicLink(Path.Join(_root.FullName, "chart", "shared"), Path.Join("..", "shared"));
        var called = false;

        var (profile, notes) = Build(new RepositoryAnalyzer((_, _) => { called = true; return Deployment("api", Container("api")); }), new AnalysisChoices { HelmChart = "chart" });

        Assert.True(called);
        Assert.DoesNotContain(notes, n => n.StartsWith("helm", StringComparison.Ordinal));
        Assert.Equal("helm-template.yaml", profile.Sources.OfType<KubernetesSource>().Single().Rendered?.Name);
    }

    [Fact]
    public void A_link_loop_inside_the_chart_ends_and_the_chart_is_rendered()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs extra rights on Windows.");
        Write("chart/Chart.yaml", "name: demo\n");
        Write("chart/templates/a/readme.txt", "a\n");
        Directory.CreateSymbolicLink(Path.Join(_root.FullName, "chart", "templates", "a", "up"), Path.Join("..", ".."));
        Directory.CreateSymbolicLink(Path.Join(_root.FullName, "chart", "templates", "a", "self"), ".");
        var called = false;

        var (_, notes) = Build(new RepositoryAnalyzer((_, _) => { called = true; return Deployment("api", Container("api")); }), new AnalysisChoices { HelmChart = "chart" });

        Assert.True(called);
        Assert.DoesNotContain(notes, n => n.StartsWith("helm", StringComparison.Ordinal));
    }

    [Fact]
    public void A_chart_with_more_files_than_can_be_checked_is_not_rendered()
    {
        Write("chart/Chart.yaml", "name: demo\n");
        Write("chart/files/a.txt", "a\n");
        Write("chart/files/b.txt", "b\n");

        var refusal = RepositoryAnalyzer.ChartLinkProblem(_root.FullName, Path.Join(_root.FullName, "chart"), maxEntries: 3);

        Assert.Equal("it has more than 3 files and folders to check for symbolic links that lead out of the repository", refusal);
        Assert.Null(RepositoryAnalyzer.ChartLinkProblem(_root.FullName, Path.Join(_root.FullName, "chart"), maxEntries: 4));
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
        var scan = RepositoryScanner.Scan(_root.FullName);

        var profile = analyzer.BuildProfile(scan, new AnalysisChoices { Kustomization = "k8s/overlay" }, NoConnections, [], TestContext.Current.CancellationToken);

        var call = Assert.Single(calls);
        Assert.Equal("kubectl", call.Tool);
        Assert.Equal(["kustomize", scan.Full("k8s/overlay")], call.Arguments);
        var kustomize = Assert.IsType<KustomizeSource>(profile.Sources.Single(s => s is KustomizeSource));
        Assert.Equal("Kustomize k8s/overlay", kustomize.Name);
        Assert.Equal(scan.Full("k8s/overlay"), kustomize.Folder);
        var kubernetes = Assert.IsType<KubernetesSource>(profile.Sources.Single(s => s is KubernetesSource));
        Assert.Equal("Rendered manifests", kubernetes.Name);
        Assert.Equal("kustomize-build.yaml", kubernetes.Rendered?.Name);
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
        var scan = RepositoryScanner.Scan(_root.FullName);

        var profile = analyzer.BuildProfile(scan, new AnalysisChoices { Kustomization = "k8s/overlay" }, NoConnections, [], TestContext.Current.CancellationToken);

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
    public void A_chart_helm_cannot_render_says_so_instead_of_asking_to_install_helm()
    {
        Write("chart/Chart.yaml", "name: demo\n");

        var (_, notes) = Build(RepositoryAnalyzer.WithTools((_, _) => new ToolRun(null, "it exited with code 1", Failed: true)), new AnalysisChoices { HelmChart = "chart" });

        Assert.Equal(
            "helm could not render the chart chart: it exited with code 1. Run helm template on the chart to see why. Without the rendered chart its values are listed only where a ConfigMap or env entry in the repository uses them.",
            Assert.Single(notes));
    }

    [Fact]
    public void A_tools_error_output_never_reaches_the_notes()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses sh as the tool.");
        Write("chart/Chart.yaml", "name: demo\n");

        // helm quotes what a template's fail message is built from, such as a values file's password.
        var analyzer = RepositoryAnalyzer.WithTools((_, _) => RepositoryAnalyzer.RunTool("sh", ["-c", "echo 'Error: execution error at (demo/templates/secret.yaml:3:4): db.password \"fake-hunter2\" is too short' >&2; exit 1"]));
        var (_, notes) = Build(analyzer, new AnalysisChoices { HelmChart = "chart" });

        var note = Assert.Single(notes);
        Assert.DoesNotContain("fake-hunter2", note, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.yaml", note, StringComparison.Ordinal);
        Assert.StartsWith("helm could not render the chart chart: it exited with code 1.", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Kustomize_is_tried_when_kubectl_fails()
    {
        // kubectl bundles its own kustomize, often an older one, so the overlay may build with kustomize itself.
        Write("k8s/overlay/kustomization.yaml", "resources: []\n");
        var tools = new List<string>();
        var analyzer = RepositoryAnalyzer.WithTools((tool, _) =>
        {
            tools.Add(tool);
            return tool == "kubectl" ? new ToolRun(null, "it exited with code 1", Failed: true) : new ToolRun(Deployment("api", Container("api")));
        });

        var (profile, notes) = Build(analyzer, new AnalysisChoices { Kustomization = "k8s/overlay" });

        Assert.Equal(["kubectl", "kustomize"], tools);
        Assert.Equal("kustomize-build.yaml", profile.Sources.OfType<KubernetesSource>().Single().Rendered?.Name);
        Assert.DoesNotContain(notes, n => n.Contains("could not", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("it took over a minute, so it was stopped")]
    [InlineData("it wrote more than 32 MB")]
    public void Kustomize_is_not_tried_when_kubectl_was_stopped(string problem)
    {
        Write("k8s/overlay/kustomization.yaml", "resources: []\n");
        var tools = new List<string>();
        var analyzer = RepositoryAnalyzer.WithTools((tool, _) =>
        {
            tools.Add(tool);
            return new ToolRun(null, problem);
        });

        var (_, notes) = Build(analyzer, new AnalysisChoices { Kustomization = "k8s/overlay" });

        Assert.Equal(["kubectl"], tools);
        Assert.Equal(
            $"kubectl could not build the overlay k8s/overlay: {problem}. Run kubectl kustomize on the folder to see why. Remote bases (git or http URLs) are not fetched, so an overlay that needs one cannot be built.",
            Assert.Single(notes));
    }

    [Fact]
    public void Kubectls_failure_is_noted_when_kustomize_is_missing()
    {
        Write("k8s/overlay/kustomization.yaml", "resources: []\n");

        var (_, notes) = Build(RepositoryAnalyzer.WithTools((tool, _) => tool == "kubectl" ? new ToolRun(null, "it exited with code 1", Failed: true) : ToolRun.Missing), new AnalysisChoices { Kustomization = "k8s/overlay" });

        Assert.StartsWith("kubectl could not build the overlay k8s/overlay: it exited with code 1. Run kubectl kustomize on the folder", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Fact]
    public void Both_failures_are_noted_when_kubectl_and_kustomize_fail()
    {
        Write("k8s/overlay/kustomization.yaml", "resources: []\n");

        var (_, notes) = Build(RepositoryAnalyzer.WithTools((tool, _) => new ToolRun(null, tool == "kubectl" ? "it exited with code 1" : "it exited with code 2", Failed: true)), new AnalysisChoices { Kustomization = "k8s/overlay" });

        Assert.StartsWith("kubectl could not build the overlay k8s/overlay: it exited with code 1. kustomize could not build it either: it exited with code 2. Run kubectl kustomize", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Fact]
    public void An_overlay_kustomize_cannot_build_names_kustomize()
    {
        Write("k8s/overlay/kustomization.yaml", "resources: []\n");

        var (_, notes) = Build(RepositoryAnalyzer.WithTools((tool, _) => tool == "kubectl" ? ToolRun.Missing : new ToolRun(null, "it exited with code 1", Failed: true)), new AnalysisChoices { Kustomization = "k8s/overlay" });

        Assert.StartsWith("kustomize could not build the overlay k8s/overlay: it exited with code 1. Run kustomize build on the folder", Assert.Single(notes), StringComparison.Ordinal);
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
        Assert.Equal([Path.Join(_root.FullName, ".github", "workflows", "ci.yml")], github.Workflows);
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

        Assert.Equal(new ToolRun("a b|c"), RepositoryAnalyzer.RunTool("sh", ["-c", "printf '%s|%s' \"$0\" \"$1\"", "a b", "c"], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_tool_that_fails_or_is_missing_gives_no_output_and_says_which()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(new ToolRun(null, "it exited with code 3", Failed: true), RepositoryAnalyzer.RunTool("sh", ["-c", "echo output; exit 3"], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(ToolRun.Missing, RepositoryAnalyzer.RunTool("configferret-no-such-tool", [], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_tools_error_output_is_never_quoted()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // The repository decides what the tool says: an env file line, a template's fail message or a values file path.
        var run = RepositoryAnalyzer.RunTool("sh", ["-c", "printf 'Error: \\033[2J \"DB_PASSWORD: fake-hunter2\" is not a valid key name\\n' >&2; exit 1"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ToolRun(null, "it exited with code 1", Failed: true), run);
    }

    [Theory]
    [InlineData("Error: An error occurred while checking for chart dependencies. You may need to run `helm dependency build` to fetch missing dependencies: found in Chart.yaml, but missing in charts/ directory: db", ", because the chart's dependencies are not in its charts folder (helm dependency build fetches them)")]
    [InlineData("Error: accumulating resources: Get \"https://example.test/base\": proxyconnect tcp: dial tcp 127.0.0.1:9: connect: connection refused", ", because it needs a remote base, which is not fetched")]
    [InlineData("fatal: transport 'https' not allowed", ", because it needs a remote base, which is not fetched")]
    public void A_tool_that_fails_for_a_known_reason_says_it_in_fixed_words(string error, string reason)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var run = RepositoryAnalyzer.RunTool("sh", ["-c", "printf '%s\\n' \"$0\" >&2; exit 1", error], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ToolRun(null, "it exited with code 1" + reason, Failed: true), run);
    }

    [Fact]
    public void A_tool_that_writes_too_much_gives_no_output()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var limit = 1024 * 1024;

        Assert.Equal(new ToolRun(null, "it wrote more than 1 MB"), RepositoryAnalyzer.RunTool("sh", ["-c", "head -c 1100000 /dev/zero | tr '\\0' x"], maxOutput: limit, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(limit, RepositoryAnalyzer.RunTool("sh", ["-c", $"head -c {limit} /dev/zero | tr '\\0' x"], maxOutput: limit, cancellationToken: TestContext.Current.CancellationToken).Output?.Length);
    }

    [Fact]
    public async Task Cancelling_stops_the_tool_and_every_process_it_started()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses sh as the tool.");
        var pidFile = Path.Join(_temp.FullName, "child.pid");
        using var cancel = new CancellationTokenSource();

        // The tool starts a child that would run for ten minutes, writes its id, and waits for it.
        var run = Task.Run(() => RepositoryAnalyzer.RunTool("sh", ["-c", "sleep 600 & echo $! > \"$0.tmp\"; mv \"$0.tmp\" \"$0\"; wait", pidFile], cancellationToken: cancel.Token), TestContext.Current.CancellationToken);
        var child = await WaitForAsync(() => File.Exists(pidFile) ? int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture) : (int?)null);
        Assert.True(IsRunning(child));

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
        await WaitForAsync(() => IsRunning(child) ? null : (int?)child);
    }

    [Fact]
    public void A_cancelled_trace_starts_no_tool()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses sh as the tool.");
        var marker = Path.Join(_temp.FullName, "started");

        Assert.ThrowsAny<OperationCanceledException>(() => RepositoryAnalyzer.RunTool("sh", ["-c", "touch \"$0\"", marker], cancellationToken: new CancellationToken(canceled: true)));

        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void The_traces_cancellation_reaches_every_tool()
    {
        Write("chart/Chart.yaml", "name: demo\n");
        Write("k8s/overlay/kustomization.yaml", "resources: []\n");
        using var cancel = new CancellationTokenSource();
        var seen = new List<CancellationToken>();
        var analyzer = RepositoryAnalyzer.WithTools((_, _, cancellationToken) =>
        {
            seen.Add(cancellationToken);
            return ToolRun.Missing;
        });

        analyzer.BuildProfile(RepositoryScanner.Scan(_root.FullName), new AnalysisChoices { HelmChart = "chart", Kustomization = "k8s/overlay" }, NoConnections, [], cancel.Token);

        Assert.Equal([cancel.Token, cancel.Token, cancel.Token], seen);
    }

    // Waits for an event of another process. The minute only decides failure; the event normally comes at once.
    private static async Task<int> WaitForAsync(Func<int?> poll)
    {
        var giveUp = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        while (true)
        {
            try
            {
                if (poll() is { } value)
                {
                    return value;
                }
            }
            catch (Exception ex) when (ex is IOException or FormatException)
            {
                // Not written yet.
            }

            Assert.True(DateTime.UtcNow < giveUp, "The other process did not get there within a minute.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    // A killed process whose parent is gone can stay a zombie until something reaps it; it no longer runs.
    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }

        var stat = $"/proc/{pid}/stat";
        return !File.Exists(stat) || !File.ReadAllText(stat).Split(')')[^1].TrimStart().StartsWith('Z');
    }

    [Fact]
    public void A_tools_error_output_does_not_get_in_the_way()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(new ToolRun("out\n"), RepositoryAnalyzer.RunTool("sh", ["-c", "echo err >&2; echo out"], cancellationToken: TestContext.Current.CancellationToken));
    }
}
