using ConfigFerret.Desktop.Services;

namespace ConfigFerret.Desktop.Tests;

public class RepositoryScannerTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("configferret-scan-");

    public void Dispose()
    {
        _temp.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Finds_the_app_and_its_environments()
    {
        var scan = RepositoryScanner.Scan(OrdersRepo.Root);

        var app = Assert.Single(scan.Apps);
        Assert.Equal("src/Orders.Api", app.RelativePath);
        Assert.Equal(["Development", "Production"], app.Environments);
        Assert.Equal(Path.GetFullPath(Path.Join(OrdersRepo.Root, "src", "Orders.Api")), app.Folder);
    }

    [Fact]
    public void Finds_the_helm_chart_with_the_release_values_files_but_not_its_own_values()
    {
        var scan = RepositoryScanner.Scan(OrdersRepo.Root);

        var chart = Assert.Single(scan.HelmCharts);
        Assert.Equal("deploy/chart", chart.RelativePath);
        Assert.Equal(["deploy/values-eu.yaml", "deploy/values-prod.yaml"], chart.ValuesFiles);
    }

    [Fact]
    public void Finds_the_rendered_manifest_but_not_the_chart_templates()
    {
        var scan = RepositoryScanner.Scan(OrdersRepo.Root);

        Assert.Equal(["deploy/rendered.yaml"], scan.Manifests);
    }

    [Fact]
    public void Finds_the_workflow()
    {
        var scan = RepositoryScanner.Scan(OrdersRepo.Root);

        Assert.Equal([".github/workflows/deploy.yml"], scan.Workflows);
    }

    [Fact]
    public void Finds_nothing_else_in_the_orders_repository()
    {
        var scan = RepositoryScanner.Scan(OrdersRepo.Root);

        Assert.Empty(scan.Kustomizations);
        Assert.False(scan.HasOctopusConfigAsCode);
    }

    [Fact]
    public void Full_resolves_a_path_from_the_root()
    {
        var scan = RepositoryScanner.Scan(OrdersRepo.Root);

        Assert.True(File.Exists(scan.Full("deploy/rendered.yaml")));
    }

    [Fact]
    public void Skips_build_output_dependency_and_git_folders()
    {
        foreach (var folder in new[] { "bin", "obj", "node_modules", ".git", "packages" })
        {
            Write($"{folder}/Hidden/appsettings.json", "{}");
            Write($"{folder}/Hidden/pod.yaml", Pod);
            Write($"{folder}/Hidden/Chart.yaml", "name: hidden");
        }

        Write("src/Api/appsettings.json", "{}");

        var scan = RepositoryScanner.Scan(_temp.FullName);

        Assert.Equal(["src/Api"], scan.Apps.Select(a => a.RelativePath));
        Assert.Empty(scan.HelmCharts);
        Assert.Empty(scan.Manifests);
    }

    [Fact]
    public void Does_not_take_a_template_with_braces_for_a_manifest()
    {
        Write("k8s/templated.yaml", Pod + "  replicas: {{ .Values.replicas }}\n");
        Write("k8s/plain.yaml", Pod);

        var scan = RepositoryScanner.Scan(_temp.FullName);

        Assert.Equal(["k8s/plain.yaml"], scan.Manifests);
    }

    [Fact]
    public void Does_not_take_other_yaml_for_a_manifest()
    {
        Write("ci/config.yaml", "name: build\nsteps: []\n");
        Write("k8s/service.yaml", "apiVersion: v1\nkind: Service\nmetadata:\n  name: x\n");

        var scan = RepositoryScanner.Scan(_temp.FullName);

        Assert.Empty(scan.Manifests);
    }

    [Fact]
    public void Treats_values_files_as_values_and_not_as_manifests()
    {
        Write("chart/Chart.yaml", "name: demo\n");
        Write("env/values-test.yaml", Pod);

        var scan = RepositoryScanner.Scan(_temp.FullName);

        Assert.Empty(scan.Manifests);
        Assert.Equal(["env/values-test.yaml"], Assert.Single(scan.HelmCharts).ValuesFiles);
    }

    [Fact]
    public void Lists_overlays_before_bases_and_finds_octopus_config_as_code()
    {
        Write("k8s/base/kustomization.yaml", "resources: []\n");
        Write("k8s/overlays/prod/kustomization.yaml", "resources: []\n");
        Write(".octopus/deployment_process.ocl", "step");

        var scan = RepositoryScanner.Scan(_temp.FullName);

        Assert.Equal(["k8s/overlays/prod", "k8s/base"], scan.Kustomizations);
        Assert.True(scan.HasOctopusConfigAsCode);
    }

    [Fact]
    public void Lists_the_shallowest_app_first_and_the_root_as_a_dot()
    {
        Write("appsettings.json", "{}");
        Write("src/deep/er/appsettings.json", "{}");

        var scan = RepositoryScanner.Scan(_temp.FullName);

        Assert.Equal([".", "src/deep/er"], scan.Apps.Select(a => a.RelativePath));
    }

    [Fact]
    public void Throws_when_the_folder_does_not_exist()
    {
        Assert.Throws<DirectoryNotFoundException>(() => RepositoryScanner.Scan(Path.Join(_temp.FullName, "missing")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Needs_a_folder(string root)
    {
        Assert.ThrowsAny<ArgumentException>(() => RepositoryScanner.Scan(root));
    }

    [Fact]
    public void Names_the_folder_that_does_not_exist()
    {
        var missing = Path.Join(_temp.FullName, "missing");

        var ex = Assert.Throws<DirectoryNotFoundException>(() => RepositoryScanner.Scan(missing));

        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Lists_values_files_from_everywhere_except_the_charts_own_values_templates_and_subcharts()
    {
        Write("deploy/chart/Chart.yaml", "name: demo\n");
        Write("deploy/chart/values.yaml", "a: 1\n");
        Write("deploy/chart/templates/values-test.yaml", "a: 1\n");
        Write("deploy/chart/charts/sub/values-sub.yaml", "a: 1\n");
        Write("deploy/values-b.yml", "a: 1\n");
        Write("deploy/Values-a.yaml", "a: 1\n");
        Write("deploy/values.txt", "a: 1\n");
        Write("deploy/myvalues.yaml", "a: 1\n");

        var chart = Assert.Single(RepositoryScanner.Scan(_temp.FullName).HelmCharts);

        Assert.Equal("deploy/chart", chart.RelativePath);
        Assert.Equal(Path.GetFullPath(Path.Join(_temp.FullName, "deploy", "chart")), chart.Folder);
        Assert.Equal(["deploy/Values-a.yaml", "deploy/values-b.yml"], chart.ValuesFiles);
        Assert.Equal("deploy/chart", chart.ToString());
    }

    [Fact]
    public void Values_files_of_other_charts_count_as_release_values()
    {
        Write("a/Chart.yaml", "name: a\n");
        Write("a/values.yaml", "x: 1\n");
        Write("b/Chart.yaml", "name: b\n");
        Write("b/values.yaml", "x: 1\n");

        var charts = RepositoryScanner.Scan(_temp.FullName).HelmCharts;

        Assert.Equal(["a", "b"], charts.Select(c => c.RelativePath));
        Assert.Equal(["b/values.yaml"], charts[0].ValuesFiles);
        Assert.Equal(["a/values.yaml"], charts[1].ValuesFiles);
    }

    [Fact]
    public void A_chart_is_offered_every_values_file_but_its_own_values_templates_and_subcharts()
    {
        Write("a/Chart.yaml", "name: a\n");
        Write("a/values.yaml", "x: 1\n");
        Write("a/values-prod.yaml", "x: 1\n");
        Write("a/templates/values-t.yaml", "x: 1\n");
        Write("a/charts/b/Chart.yaml", "name: b\n");
        Write("a/charts/b/values.yaml", "x: 1\n");
        Write("a/charts/b/values-b.yaml", "x: 1\n");
        Write("env/values-qa.yaml", "x: 1\n");

        var charts = RepositoryScanner.Scan(_temp.FullName).HelmCharts;

        Assert.Equal(["a", "a/charts/b"], charts.Select(c => c.RelativePath));
        Assert.Equal(["a/values-prod.yaml", "env/values-qa.yaml"], charts[0].ValuesFiles);
        Assert.Equal(["a/charts/b/values-b.yaml", "a/templates/values-t.yaml", "a/values-prod.yaml", "a/values.yaml", "env/values-qa.yaml"], charts[1].ValuesFiles);
        foreach (var chart in charts)
        {
            Assert.Equal(chart.ValuesFiles, Enumerable.Range(0, chart.ValuesFiles.Count).Select(i => chart.ValuesFiles[i]));
            Assert.Throws<ArgumentOutOfRangeException>(() => chart.ValuesFiles[chart.ValuesFiles.Count]);
        }
    }

    [Fact]
    public void Scanning_many_charts_and_values_files_costs_memory_in_step_with_the_files()
    {
        // Each chart folder holds a chart and a values file, and as many values files lie outside every chart. Every
        // chart is offered all of them, so a list per chart would cost charts times files.
        const int count = 400;
        for (var i = 0; i < count; i++)
        {
            Write($"charts/c{i}/Chart.yaml", string.Empty);
            Write($"charts/c{i}/values-prod.yaml", string.Empty);
            Write($"env/e{i}/values-qa.yaml", string.Empty);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var scan = RepositoryScanner.Scan(_temp.FullName);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(count, scan.HelmCharts.Count);
        Assert.Equal(2 * count, scan.HelmCharts[0].ValuesFiles.Count);
        const long budget = 3 * count * 16 * 1024;
        Assert.True(allocated < budget, $"The scan allocated {allocated / 1024 / 1024} MB, more than {budget / 1024 / 1024} MB for {3 * count} files.");
    }

    [Fact]
    public void Lists_charts_manifests_and_workflows_in_name_order()
    {
        Write("z/Chart.yaml", "name: z\n");
        Write("m/Chart.yaml", "name: m\n");
        Write("k8s/b.yml", Pod);
        Write("k8s/A.YAML", Pod);
        Write("k8s/c.yaml", Pod);
        Write(".github/workflows/release.yml", "name: r\n");
        Write(".github/workflows/Build.yaml", "name: b\n");
        Write(".github/workflows/README.md", "docs\n");
        Write(".github/workflows/nested/inner.yml", "name: n\n");

        var scan = RepositoryScanner.Scan(_temp.FullName);

        Assert.Equal(["m", "z"], scan.HelmCharts.Select(c => c.RelativePath));
        Assert.Equal(["k8s/A.YAML", "k8s/b.yml", "k8s/c.yaml"], scan.Manifests);
        Assert.Equal([".github/workflows/Build.yaml", ".github/workflows/release.yml"], scan.Workflows);
        Assert.Equal(_temp.FullName, scan.Root);
    }

    [Fact]
    public void Has_no_workflows_without_a_workflows_folder()
    {
        Write("src/Api/appsettings.json", "{}");

        var scan = RepositoryScanner.Scan(_temp.FullName);

        Assert.Empty(scan.Workflows);
        Assert.False(scan.HasOctopusConfigAsCode);
    }

    [Fact]
    public void Orders_apps_by_depth_then_name()
    {
        Write("b/appsettings.json", "{}");
        Write("a/appsettings.json", "{}");
        Write("a/x/appsettings.json", "{}");
        Write("C/appsettings.json", "{}");

        var scan = RepositoryScanner.Scan(_temp.FullName);

        Assert.Equal(["a", "b", "C", "a/x"], scan.Apps.Select(a => a.RelativePath));
        Assert.Equal("a", scan.Apps[0].ToString());
    }

    [Fact]
    public void Lists_each_environment_once_in_name_order()
    {
        Write("app/appsettings.json", "{}");
        Write("app/appsettings.Staging.json", "{}");
        Write("app/appsettings.development.json", "{}");
        Write("app/appsettings.Development.json.bak", "{}");
        Write("app/appsettings.local.extra.json", "{}");
        Write("app/appsettings.Production.json", "{}");

        var app = Assert.Single(RepositoryScanner.Scan(_temp.FullName).Apps);

        Assert.Equal(["development", "Production", "Staging"], app.Environments);
    }

    [Fact]
    public void Lists_kustomizations_by_every_name_with_overlays_first_then_by_name()
    {
        Write("z/base/kustomization.yaml", "resources: []\n");
        Write("a/base/kustomization.yml", "resources: []\n");
        Write("m/Kustomization", "resources: []\n");
        Write("z/Overlay/kustomization.yaml", "resources: []\n");
        Write("a/overlays/dev/kustomization.yaml", "resources: []\n");

        var scan = RepositoryScanner.Scan(_temp.FullName);

        Assert.Equal(["a/overlays/dev", "z/Overlay", "a/base", "m", "z/base"], scan.Kustomizations);
    }

    [Theory]
    [InlineData(".github")]
    [InlineData(".octopus")]
    [InlineData(".deploy")]
    public void Scans_the_hidden_folders_that_can_hold_configuration(string folder)
    {
        Write($"{folder}/app/appsettings.json", "{}");

        Assert.Equal([$"{folder}/app"], RepositoryScanner.Scan(_temp.FullName).Apps.Select(a => a.RelativePath));
    }

    [Theory]
    [InlineData(".hidden")]
    [InlineData(".vs")]
    [InlineData("Bin")]
    [InlineData("TestResults")]
    [InlineData("artifacts")]
    [InlineData("dist")]
    [InlineData("vendor")]
    [InlineData(".idea")]
    public void Skips_other_hidden_and_build_folders(string folder)
    {
        Write($"{folder}/app/appsettings.json", "{}");
        Write("src/appsettings.json", "{}");

        Assert.Equal(["src"], RepositoryScanner.Scan(_temp.FullName).Apps.Select(a => a.RelativePath));
    }

    [Fact]
    public void Looks_twelve_folders_deep_and_no_further()
    {
        var twelve = string.Join('/', Enumerable.Range(1, 12).Select(i => "d" + i));
        Write($"{twelve}/appsettings.json", "{}");
        Write($"{twelve}/d13/appsettings.json", "{}");

        Assert.Equal([twelve], RepositoryScanner.Scan(_temp.FullName).Apps.Select(a => a.RelativePath));
    }

    [Fact]
    public void Does_not_follow_folder_links()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var outside = new TempFolder();
        File.WriteAllText(Path.Join(outside.Path, "appsettings.json"), "{}");
        Write("src/appsettings.json", "{}");
        Directory.CreateSymbolicLink(Path.Join(_temp.FullName, "link"), outside.Path);

        Assert.Equal(["src"], RepositoryScanner.Scan(_temp.FullName).Apps.Select(a => a.RelativePath));
    }

    [Fact]
    public void Does_not_take_a_linked_file_for_a_manifest()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var outside = new TempFolder();
        var target = Path.Join(outside.Path, "pod.yaml");
        File.WriteAllText(target, Pod);
        Write("k8s/real.yaml", Pod);
        File.CreateSymbolicLink(Path.Join(_temp.FullName, "k8s", "link.yaml"), target);

        Assert.Equal(["k8s/real.yaml"], RepositoryScanner.Scan(_temp.FullName).Manifests);
    }

    [Fact]
    public void Keeps_a_backslash_in_a_name_as_part_of_the_name()
    {
        // On Windows a backslash is the separator, so no name can hold one.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Write(@"a\..\..\other-repo\overlay/kustomization.yaml", "resources: []\n");
        Write(@"k8s/..\..\..\other-repo\pod.yaml", Pod);
        Write(@".github/workflows/..\..\..\other-repo\deploy.yml", "on: push\n");
        Write("deploy/chart/Chart.yaml", "apiVersion: v2\nname: x\nversion: 1.0.0\n");
        Write(@"deploy/values\..\..\..\other-repo\values.yaml", "a: 1\n");

        var scan = RepositoryScanner.Scan(_temp.FullName);

        var found = scan.Kustomizations.Concat(scan.Manifests).Concat(scan.Workflows).Concat(scan.HelmCharts.SelectMany(c => c.ValuesFiles)).ToList();
        Assert.Equal(4, found.Count);
        Assert.All(found, f => Assert.StartsWith(_temp.FullName + Path.DirectorySeparatorChar, scan.Full(f), StringComparison.Ordinal));
        Assert.Equal([@"a\..\..\other-repo\overlay"], scan.Kustomizations);
    }

    [Fact]
    public void Reads_manifests_up_to_two_megabytes()
    {
        const int limit = 2 * 1024 * 1024;
        Write("k8s/exact.yaml", Pod + new string('#', limit - Pod.Length));
        Write("k8s/over.yaml", Pod + new string('#', limit - Pod.Length + 1));

        Assert.Equal(["k8s/exact.yaml"], RepositoryScanner.Scan(_temp.FullName).Manifests);
    }

    [Theory]
    [InlineData("kind: Pod\nmetadata:\n  name: x\n")]
    [InlineData("apiVersion: v1\nmetadata:\n  name: x\n")]
    [InlineData("apiVersion: v1\nkind: Service\n")]
    public void A_manifest_needs_an_api_version_and_a_workload_or_config_kind(string text)
    {
        Write("k8s/odd.yaml", text);

        Assert.Empty(RepositoryScanner.Scan(_temp.FullName).Manifests);
    }

    [Theory]
    [InlineData("Deployment")]
    [InlineData("StatefulSet")]
    [InlineData("DaemonSet")]
    [InlineData("Job")]
    [InlineData("CronJob")]
    [InlineData("ReplicaSet")]
    [InlineData("ConfigMap")]
    [InlineData("Secret")]
    [InlineData("List")]
    public void Takes_these_kinds_for_manifests(string kind)
    {
        Write("k8s/x.yaml", $"apiVersion: v1\nkind: {kind}\nmetadata:\n  name: x\n");

        Assert.Equal(["k8s/x.yaml"], RepositoryScanner.Scan(_temp.FullName).Manifests);
    }

    [Fact]
    public void Keeps_manifests_inside_a_chart_out_of_the_manifest_list_but_not_its_neighbours()
    {
        Write("chart/Chart.yaml", "name: demo\n");
        Write("chart/rendered.yaml", Pod);
        Write("chart-extra/pod.yaml", Pod);

        Assert.Equal(["chart-extra/pod.yaml"], RepositoryScanner.Scan(_temp.FullName).Manifests);
    }

    private const string Pod = "apiVersion: v1\nkind: Pod\nmetadata:\n  name: x\nspec:\n";

    private void Write(string relativePath, string text)
    {
        var path = Path.Join(_temp.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
