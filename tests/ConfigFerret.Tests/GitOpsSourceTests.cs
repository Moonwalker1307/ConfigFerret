using ConfigFerret.GitOps;
using ConfigFerret.Helm;
using ConfigFerret.Kubernetes;
using ConfigFerret.Sources;

namespace ConfigFerret.Tests;

public class GitOpsSourceTests
{
    private static readonly string ArgoRepo = Path.Join(SourceGoldenTests.Fixture("argocd"), "repo");
    private static readonly string ArgoApp = Path.Join(ArgoRepo, "apps", "orders-prod.yaml");
    private static readonly string FluxRepo = Path.Join(SourceGoldenTests.Fixture("flux"), "repo");
    private static readonly string FluxRelease = Path.Join(FluxRepo, "clusters", "prod", "orders-helmrelease.yaml");
    private static readonly string FluxKustomization = Path.Join(FluxRepo, "clusters", "prod", "billing.yaml");

    [Fact]
    public void Providers_describe_themselves()
    {
        IConfigSourceProvider[] providers = [new ArgoCdApplicationSourceProvider(), new FluxHelmReleaseSourceProvider(), new FluxKustomizationSourceProvider()];

        Assert.Equal(["argocd-application", "flux-helmrelease", "flux-kustomization"], providers.Select(p => p.Id));
        Assert.Equal(["Argo CD Application", "Flux HelmRelease", "Flux Kustomization"], providers.Select(p => p.DisplayName));
        Assert.All(providers, p => Assert.Equal(SourceCategory.GitOps, p.Category));
        Assert.Equal(["ManifestFile", "Name", "ChartFolder", "RepositoryRoot"], providers[0].Settings.Select(s => s.Name));
        Assert.Equal(["ManifestFile", "Name", "ChartFolder", "RepositoryRoot", "DataFile"], providers[1].Settings.Select(s => s.Name));
        Assert.Equal(["ManifestFile", "Name", "RepositoryRoot", "DataFile"], providers[2].Settings.Select(s => s.Name));
        Assert.All(providers.SelectMany(p => p.Settings).Where(s => s.Name != "Name"), s => Assert.Equal(SourceSettingKind.Path, s.Kind));
        Assert.Equal(SourceSettingKind.Text, providers[0].Settings[1].Kind);
        Assert.Equal([true, false, false, false], providers[0].Settings.Select(s => s.Required));
        Assert.Equal("The YAML file in Git that holds the Application.", providers[0].Settings[0].Help);
        Assert.Equal("The HelmRelease's name, needed when the file holds several.", providers[1].Settings[1].Help);
        Assert.Equal("Manifest file", providers[2].Settings[0].Label);
        Assert.All(providers.SelectMany(p => p.Settings), s => Assert.False(string.IsNullOrWhiteSpace(s.Help)));
    }

    [Fact]
    public void Create_checks_the_settings()
    {
        var provider = new ArgoCdApplicationSourceProvider();

        Assert.Throws<ArgumentNullException>(() => provider.Create(null!));
        Assert.Equal("ManifestFile is required. (Parameter 'values')", Assert.Throws<ArgumentException>(() => provider.Create(new Dictionary<string, string?>())).Message);
        Assert.Throws<ArgumentException>(() => provider.Create(new Dictionary<string, string?> { ["ManifestFile"] = " " }));
        Assert.StartsWith("The manifest file /nonexistent/app.yaml does not exist.", Assert.Throws<ArgumentException>(() => provider.Create(new Dictionary<string, string?> { ["ManifestFile"] = "/nonexistent/app.yaml" })).Message, StringComparison.Ordinal);
        Assert.StartsWith("ChartFolder /nonexistent does not exist.", Assert.Throws<ArgumentException>(() => provider.Create(new Dictionary<string, string?> { ["ManifestFile"] = ArgoApp, ["ChartFolder"] = "/nonexistent" })).Message, StringComparison.Ordinal);
        Assert.StartsWith("RepositoryRoot /nonexistent does not exist.", Assert.Throws<ArgumentException>(() => provider.Create(new Dictionary<string, string?> { ["ManifestFile"] = ArgoApp, ["RepositoryRoot"] = "/nonexistent" })).Message, StringComparison.Ordinal);
        Assert.StartsWith("DataFile /nonexistent does not exist.", Assert.Throws<ArgumentException>(() => new FluxHelmReleaseSourceProvider().Create(new Dictionary<string, string?> { ["ManifestFile"] = FluxRelease, ["DataFile"] = "/nonexistent" })).Message, StringComparison.Ordinal);

        var source = Assert.IsType<GitOpsSource>(provider.Create(new Dictionary<string, string?>
        {
            ["ManifestFile"] = $" {ArgoApp} ",
            ["Name"] = "orders-prod",
            ["RepositoryRoot"] = ArgoRepo,
            ["ChartFolder"] = "",
            ["DataFile"] = null,
        }));
        Assert.Equal(ArgoApp, source.ManifestFile);
        Assert.Equal("orders-prod", source.Name);
        Assert.Equal(ArgoRepo, source.Options.RepositoryRoot);
        Assert.Null(source.Options.ChartFolder);
        Assert.Null(source.DataFile);
        Assert.Equal("GitOps orders-prod", source.DisplayName);
    }

    [Fact]
    public async Task Loads_an_argo_cd_application_and_finds_unused_values()
    {
        var source = new ArgoCdApplicationSourceProvider().Create(new Dictionary<string, string?> { ["ManifestFile"] = ArgoApp, ["RepositoryRoot"] = ArgoRepo });
        Assert.Equal("GitOps orders-prod.yaml", source.DisplayName);

        var tracer = ConfigTracer.ForContainer(KubernetesManifests.LoadFile(Path.Join(SourceGoldenTests.Fixture("argocd"), "rendered.yaml")), "orders", "orders");
        var loaded = await tracer.WithSourceAsync(source, TestContext.Current.CancellationToken);
        var report = tracer.Trace();

        Assert.Equal("Argo CD Application orders-prod", loaded.DisplayName);
        Assert.Empty(loaded.Warnings);
        Assert.Equal(6, loaded.Entries.Count);
        Assert.Empty(loaded.FindUnused(report));
        Assert.Equal("45", report.Find("Db__Timeout")!.Value);
    }

    [Fact]
    public async Task Loads_a_flux_helm_release_with_values_from_a_data_file()
    {
        var dir = Directory.CreateTempSubdirectory("cf-gitops-data-").FullName;
        try
        {
            var release = Path.Join(dir, "release.yaml");
            File.WriteAllText(release, """
                apiVersion: helm.toolkit.fluxcd.io/v2
                kind: HelmRelease
                metadata: {name: rel}
                spec:
                  chart: {spec: {chart: orders, sourceRef: {kind: HelmRepository, name: r}}}
                  valuesFrom:
                    - {kind: ConfigMap, name: from-file}
                    - {kind: ConfigMap, name: from-code}
                """);
            File.WriteAllText(Path.Join(dir, "data.yaml"), "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: from-file}\ndata:\n  values.yaml: 'a: 1'\n");
            var source = new GitOpsSource(
                release,
                options: new GitOpsOptions { Data = KubernetesManifests.Parse("apiVersion: v1\nkind: ConfigMap\nmetadata: {name: from-code}\ndata:\n  values.yaml: 'b: 2'\n") },
                kinds: [GitOpsKind.FluxHelmRelease],
                dataFile: Path.Join(dir, "data.yaml"));

            var loaded = await source.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Equal(["a", "b"], loaded.Entries.Select(e => e.Name));
            Assert.Equal(Path.Join(dir, "data.yaml"), source.DataFile);
            Assert.Single(loaded.Warnings);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_provider_only_finds_its_own_kind_of_object()
    {
        var argo = new ArgoCdApplicationSourceProvider().Create(new Dictionary<string, string?> { ["ManifestFile"] = FluxKustomization });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => argo.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("billing.yaml has no Argo CD Application or Flux HelmRelease or Kustomization.", ex.Message);

        var flux = new FluxKustomizationSourceProvider().Create(new Dictionary<string, string?> { ["ManifestFile"] = FluxKustomization, ["RepositoryRoot"] = FluxRepo });
        var loaded = await flux.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Flux Kustomization billing", loaded.DisplayName);
        Assert.Contains(loaded.Entries, e => e.Name == "UNUSED_VAR");

        var helm = new FluxHelmReleaseSourceProvider().Create(new Dictionary<string, string?> { ["ManifestFile"] = FluxRelease });
        Assert.Equal("Flux HelmRelease orders", (await helm.LoadAsync(TestContext.Current.CancellationToken)).DisplayName);
    }

    [Fact]
    public async Task Finds_unused_substitution_variables()
    {
        var source = new GitOpsSource(FluxKustomization, "billing", new GitOpsOptions { RepositoryRoot = FluxRepo });
        var tracer = ConfigTracer.ForContainer(KubernetesManifests.LoadFile(Path.Join(SourceGoldenTests.Fixture("flux"), "billing-rendered.yaml")), "billing", "billing");
        var loaded = await tracer.WithSourceAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(["SMTP_HOST", "UNUSED_VAR"], loaded.FindUnused(tracer.Trace()).Select(e => e.Name));
    }

    [Fact]
    public async Task The_source_checks_its_arguments_and_the_token()
    {
        Assert.Throws<ArgumentException>(() => new GitOpsSource(" "));
        var all = new GitOpsSource(ArgoApp, " ", kinds: []);
        Assert.Null(all.Name);
        Assert.NotNull(all.Options);
        Assert.Equal("Argo CD Application orders-prod", (await all.LoadAsync(TestContext.Current.CancellationToken)).DisplayName);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => all.LoadAsync(cancelled.Token));
    }

    [Fact]
    public void With_gitops_traces_the_helm_values_and_warns_through_the_trace()
    {
        var app = GitOpsApplication.Parse("""
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            metadata: {name: remote}
            spec:
              source:
                chart: orders
                repoURL: oci://registry.example.test/charts
                helm:
                  values: |
                    db:
                      timeout: 45
            """);
        var manifests = KubernetesManifests.Parse("""
            apiVersion: v1
            kind: Pod
            metadata: {name: p}
            spec:
              containers:
                - name: c
                  env:
                    - {name: TIMEOUT, value: "45"}
            """);

        var report = ConfigTracer.ForContainer(manifests).WithGitOps(app).Trace();

        Assert.Equal(
            ["Argo CD Application remote deploys chart orders from oci://registry.example.test/charts, which ConfigFerret cannot fetch. Pull it (helm pull --untar) and pass its folder as the chart folder to trace the chart's templates and defaults."],
            report.Warnings);
        Assert.Empty(report.Find("TIMEOUT")!.Steps[0].Origins);
        Assert.Throws<ArgumentNullException>(() => ConfigTracer.ForContainer(manifests).WithGitOps(null!));
    }

    [Fact]
    public void With_helm_adds_the_warnings_of_a_gitops_chart()
    {
        var app = GitOpsApplication.Parse("""
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            metadata: {name: remote}
            spec:
              source: {chart: orders}
            """);
        var manifests = KubernetesManifests.Parse("apiVersion: v1\nkind: Pod\nmetadata: {name: p}\nspec:\n  containers:\n    - name: c\n      env: [{name: A, value: b}]\n");

        var report = ConfigTracer.ForContainer(manifests).WithHelm(HelmChart.ForGitOps(app)).Trace();

        Assert.Single(report.Warnings);
    }

    [Fact]
    public void A_secret_rendered_from_helm_values_never_shows_its_values_in_a_note()
    {
        var chart = HelmChart.FromText([("values.yaml", "db:\n  password: from-values\n")], [("templates/s.yaml", "stringData:\n  password: {{ .Values.db.password }}\n")]);
        var manifests = KubernetesManifests.Parse("""
            apiVersion: v1
            kind: Secret
            metadata: {name: s}
            stringData: {password: rendered-secret}
            ---
            apiVersion: v1
            kind: Pod
            metadata: {name: p}
            spec:
              containers:
                - name: c
                  env:
                    - name: PASSWORD
                      valueFrom: {secretKeyRef: {name: s, key: password}}
            """);

        var report = ConfigTracer.ForContainer(manifests).WithHelm(chart).WithOptions(o => o.RevealSensitiveValues = false).Trace();
        var text = report.ToText() + report.ToJson();

        Assert.DoesNotContain("rendered-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("from-values", text, StringComparison.Ordinal);
        Assert.Contains("the values differ from the manifest", text, StringComparison.Ordinal);
    }
}
