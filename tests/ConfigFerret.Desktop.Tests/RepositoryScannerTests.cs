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

    private const string Pod = "apiVersion: v1\nkind: Pod\nmetadata:\n  name: x\nspec:\n";

    private void Write(string relativePath, string text)
    {
        var path = Path.Join(_temp.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
