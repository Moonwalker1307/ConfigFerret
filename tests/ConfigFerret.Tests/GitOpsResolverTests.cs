using ConfigFerret.GitOps;
using ConfigFerret.Kubernetes;
using ConfigFerret.Sources;

namespace ConfigFerret.Tests;

public sealed class GitOpsResolverTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cf-gitops-resolver-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Join(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static ProvenanceStep ConfigMapKey(string name, string key, string? value = null) =>
        new() { Kind = LayerKind.KubernetesConfigMap, Source = $"ConfigMap {name}", Value = value, Reference = new ConfigMapKeyRef(name, key) };

    private static ProvenanceStep SecretKey(string name, string key) =>
        new() { Kind = LayerKind.KubernetesSecret, Source = $"Secret {name}", IsSensitive = true, Reference = new SecretKeyRef(name, key) };

    private static ProvenanceStep Env(string workload, string container, string name) =>
        new() { Kind = LayerKind.KubernetesWorkload, Source = $"{workload} env {name}", Reference = new WorkloadEnvRef(workload, container, name) };

    private static readonly GitOpsApplication Patched = GitOpsApplication.Parse("""
        apiVersion: argoproj.io/v1alpha1
        kind: Application
        metadata: {name: billing}
        spec:
          source:
            path: overlays/prod
            kustomize:
              namePrefix: prod-
              nameSuffix: -eu
              images: [billing=registry/billing:2.0]
              patches:
                - target: {kind: ConfigMap, name: billing-config}
                  patch: |
                    - op: replace
                      path: /data/Currency
                      value: EUR
                    - op: remove
                      path: /data/Currency
        """, "billing-app.yaml");

    [Fact]
    public void Links_config_map_keys_to_the_kustomize_patches_that_set_them()
    {
        var resolver = new GitOpsResolver(Patched);
        var diagnostics = new TraceDiagnostics();

        foreach (var name in new[] { "billing-config", "prod-billing-config-eu", "billing-config-5f7h8k9m2t", "prod-billing-config-eu-5f7h8k9m2t" })
        {
            var steps = resolver.Resolve(ConfigMapKey(name, "Currency"), diagnostics);
            Assert.Equal(2, steps.Count);
            Assert.Equal(LayerKind.GitOps, steps[0].Kind);
            Assert.Equal("Argo CD Application billing (kustomize.patches)", steps[0].Source);
            Assert.Equal("billing-app.yaml:14", steps[0].Location);
            Assert.Equal("EUR", steps[0].Value);
            Assert.False(steps[0].IsRemoval);
            Assert.False(steps[0].IsInferred);
            Assert.Equal("kustomize patch on ConfigMap billing-config", steps[0].Note);
            Assert.Equal(new GitOpsPatchRef("billing", "billing-config", "Currency"), steps[0].Reference);
            Assert.True(steps[1].IsRemoval);
            Assert.Null(steps[1].Value);
            Assert.Equal("billing-app.yaml:17", steps[1].Location);
        }

        Assert.Empty(resolver.Resolve(ConfigMapKey("billing-config", "Other"), diagnostics));
        Assert.Empty(resolver.Resolve(ConfigMapKey("prod-billing-config", "Currency"), diagnostics));
        Assert.Empty(resolver.Resolve(ConfigMapKey("billing-config-short", "Currency"), diagnostics));
        Assert.Empty(resolver.Resolve(ConfigMapKey("x-billing-config", "Currency"), diagnostics));
        Assert.Empty(resolver.Resolve(SecretKey("billing-config", "Currency"), diagnostics));
        Assert.Empty(resolver.Resolve(new ProvenanceStep { Kind = LayerKind.Helm, Source = "x", Reference = new GitOpsPatchRef("billing", "billing-config", "Currency") }, diagnostics));
        Assert.Equal(
            ["Argo CD Application billing: kustomize namePrefix 'prod-'; nameSuffix '-eu'; images billing=registry/billing:2.0 are applied by Argo CD on top of the manifests in Git."],
            diagnostics.Warnings);
    }

    [Fact]
    public void Warnings_are_added_once_per_resolver()
    {
        var resolver = new GitOpsResolver(Patched);
        var first = new TraceDiagnostics();
        resolver.Resolve(ConfigMapKey("a", "b"), first);
        var second = new TraceDiagnostics();
        resolver.Resolve(ConfigMapKey("a", "b"), second);

        Assert.Single(first.Warnings);
        Assert.Empty(second.Warnings);
        Assert.Single(resolver.Warnings);
        Assert.Null(resolver.Chart);
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(null!, first));
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(ConfigMapKey("a", "b"), null!));
        Assert.Throws<ArgumentNullException>(() => new GitOpsResolver(null!));
    }

    [Fact]
    public void Patch_entries_list_each_patched_key_once()
    {
        var entries = new GitOpsResolver(Patched).Entries();
        var entry = Assert.Single(entries);
        Assert.Equal(new SourceEntry("ConfigMap billing-config key Currency", new GitOpsPatchRef("billing", "billing-config", "Currency"), false, "kustomize patch", "Argo CD Application billing"), entry);
    }

    [Fact]
    public void Builds_the_chart_for_a_helm_release_and_lists_the_values_it_sets()
    {
        var resolver = new GitOpsResolver(GitOpsApplication.Load(Path.Join(SourceGoldenTests.Fixture("argocd"), "repo", "apps", "orders-prod.yaml")), new GitOpsOptions { RepositoryRoot = Path.Join(SourceGoldenTests.Fixture("argocd"), "repo") });

        Assert.NotNull(resolver.Chart);
        Assert.Empty(resolver.Warnings);
        Assert.Equal(
            [
                new SourceEntry("cache.ttl", new HelmValueRef("cache.ttl"), false, "Argo CD Application orders-prod (helm.values)", "Argo CD Application orders-prod"),
                new SourceEntry("db.host", new HelmValueRef("db.host"), false, "values-prod.yaml", "Argo CD Application orders-prod"),
                new SourceEntry("db.pool", new HelmValueRef("db.pool"), false, "Argo CD Application orders-prod (helm.parameters)", "Argo CD Application orders-prod"),
                new SourceEntry("db.timeout", new HelmValueRef("db.timeout"), false, "Argo CD Application orders-prod (helm.values)", "Argo CD Application orders-prod"),
                new SourceEntry("features.search", new HelmValueRef("features.search"), false, "Argo CD Application orders-prod (helm.parameters)", "Argo CD Application orders-prod"),
                new SourceEntry("logLevel", new HelmValueRef("logLevel"), false, "$values/envs/prod/orders.yaml", "Argo CD Application orders-prod"),
            ],
            resolver.Entries());
    }

    [Fact]
    public void Values_from_a_secret_are_sensitive_entries_and_steps()
    {
        var resolver = new GitOpsResolver(
            GitOpsApplication.Load(Path.Join(SourceGoldenTests.Fixture("flux"), "repo", "clusters", "prod", "orders-helmrelease.yaml")),
            new GitOpsOptions { ChartFolder = Path.Join(SourceGoldenTests.Fixture("argocd"), "repo", "charts", "orders") });

        var password = Assert.Single(resolver.Entries(), e => e.Name == "db.password");
        Assert.True(password.IsSensitive);
        Assert.Equal("Flux HelmRelease orders (valuesFrom Secret orders-db, key password)", password.Detail);

        var steps = resolver.Resolve(new ProvenanceStep { Kind = LayerKind.Helm, Source = "t", Reference = new HelmValueRef("db.password") }, new TraceDiagnostics());
        Assert.Equal([false, true], steps.Select(s => s.IsSensitive));
        Assert.Equal([LayerKind.Helm, LayerKind.GitOps], steps.Select(s => s.Kind));
    }

    [Fact]
    public void A_plain_argo_cd_path_is_a_chart_only_when_it_holds_a_chart_yaml()
    {
        var app = GitOpsApplication.Parse("""
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            metadata: {name: plain}
            spec:
              source: {path: deploy}
            """);
        Assert.Null(new GitOpsResolver(app).Chart);
        Assert.Null(new GitOpsResolver(app, new GitOpsOptions { RepositoryRoot = _root }).Chart);

        Write("deploy/Chart.yaml", "name: deploy\n");
        Assert.NotNull(new GitOpsResolver(app, new GitOpsOptions { RepositoryRoot = _root }).Chart);
        Assert.NotNull(new GitOpsResolver(app, new GitOpsOptions { ChartFolder = Path.Join(_root, "deploy"), RepositoryRoot = "/nonexistent" }).Chart);
        Assert.Null(new GitOpsResolver(app, new GitOpsOptions { ChartFolder = _root, RepositoryRoot = _root }).Chart);
        Assert.Empty(new GitOpsResolver(app).Entries());
    }

    private static GitOpsApplication Kustomization(string postBuild, string data = "") => GitOpsApplication.Parse($"""
        apiVersion: kustomize.toolkit.fluxcd.io/v1
        kind: Kustomization
        metadata: {"{"}name: apps{"}"}
        spec:
          path: ./apps
          postBuild:
        {postBuild}
        {data}
        """, "apps.yaml");

    [Fact]
    public void Links_placeholders_in_source_manifests_to_the_variables_that_fill_them()
    {
        Write("apps/cm.yaml", """
            apiVersion: v1
            kind: ConfigMap
            metadata: {name: cfg}
            data:
              One: ${ONE}
              Two: ${A}-${B}
              Defaulted: ${MISSING:=fallback}
              Bare: ${NOTHING}
              Escaped: $${ONE}
              Plain: plain
            ---
            apiVersion: v1
            kind: Secret
            metadata: {name: sec}
            stringData:
              Password: ${PASSWORD:=default-secret}
            ---
            apiVersion: apps/v1
            kind: Deployment
            metadata: {name: web}
            spec:
              template:
                spec:
                  containers:
                    - name: app
                      env:
                        - name: MODE
                          value: ${ONE}
                        - name: FROM_CM
                          valueFrom: {configMapKeyRef: {name: cfg, key: One}}
            """);
        Write("apps/broken.yml", "a: [");
        Write("apps/notes.txt", "${ONE}");
        var resolver = new GitOpsResolver(
            Kustomization(
                """
                    substitute: {ONE: "1"}
                    substituteFrom:
                      - {kind: ConfigMap, name: vars}
                      - {kind: Secret, name: secret-vars}
                      - {kind: ConfigMap, name: missing}
                      - {kind: ConfigMap, name: optional-missing, optional: true}
                """,
                """
                ---
                apiVersion: v1
                kind: ConfigMap
                metadata: {name: vars}
                data: {ONE: "0", UNUSED: x}
                """),
            new GitOpsOptions
            {
                RepositoryRoot = _root,
                Data = KubernetesManifests.Parse("""
                    apiVersion: v1
                    kind: Secret
                    metadata: {name: secret-vars}
                    stringData: {PASSWORD: hunter2}
                    """, "secrets.yaml"),
            });
        var diagnostics = new TraceDiagnostics();

        var one = Assert.Single(resolver.Resolve(ConfigMapKey("cfg", "One", "1"), diagnostics));
        Assert.Equal(LayerKind.GitOps, one.Kind);
        Assert.Equal("apps/cm.yaml", one.Source);
        Assert.Equal("line 5, ConfigMap cfg key One", one.Location);
        Assert.Equal("${ONE}", one.Value);
        Assert.False(one.IsSensitive);
        Assert.False(one.IsInferred);
        Assert.Equal("Flux Kustomization apps replaces ${ONE} after kustomize build", one.Note);
        Assert.Equal(new GitOpsVariableRef("apps", "ONE"), one.Reference);

        var variable = resolver.Resolve(new ProvenanceStep { Kind = LayerKind.GitOps, Source = "t", Reference = one.Reference }, diagnostics);
        Assert.Equal(
            [
                ("Flux Kustomization apps (substituteFrom ConfigMap vars)", "apps.yaml:17", "0", false),
                ("Flux Kustomization apps (postBuild.substitute)", "apps.yaml:7", "1", false),
            ],
            variable.Select(v => (v.Source, v.Location, v.Value, v.IsSensitive)));
        Assert.All(variable, v => Assert.Null(v.Reference));
        Assert.All(variable, v => Assert.Equal(LayerKind.GitOps, v.Kind));

        var two = Assert.Single(resolver.Resolve(ConfigMapKey("cfg", "Two"), diagnostics));
        Assert.Null(two.Reference);
        Assert.Equal("Flux Kustomization apps replaces ${A}, ${B} after kustomize build; a value with several variables is not traced further", two.Note);

        Assert.Equal(
            "Flux Kustomization apps replaces ${MISSING} after kustomize build; no loaded substitute sets MISSING, so Flux uses the placeholder's default",
            Assert.Single(resolver.Resolve(ConfigMapKey("cfg", "Defaulted"), diagnostics)).Note);
        Assert.Equal(
            "Flux Kustomization apps replaces ${NOTHING} after kustomize build; no loaded substitute sets NOTHING, so Flux uses the placeholder's default, or an empty string",
            Assert.Single(resolver.Resolve(ConfigMapKey("cfg", "Bare"), diagnostics)).Note);
        Assert.Empty(resolver.Resolve(ConfigMapKey("cfg", "Escaped"), diagnostics));
        Assert.Empty(resolver.Resolve(ConfigMapKey("cfg", "Plain"), diagnostics));
        Assert.Empty(resolver.Resolve(ConfigMapKey("other", "One"), diagnostics));

        var secret = Assert.Single(resolver.Resolve(SecretKey("sec", "Password"), diagnostics));
        Assert.True(secret.IsSensitive);
        Assert.Equal("line 16, Secret sec key Password", secret.Location);
        Assert.DoesNotContain("default-secret", secret.Note, StringComparison.Ordinal);
        var password = Assert.Single(resolver.Resolve(new ProvenanceStep { Kind = LayerKind.GitOps, Source = "t", Reference = secret.Reference }, diagnostics));
        Assert.Equal(("Flux Kustomization apps (substituteFrom Secret secret-vars)", "secrets.yaml:4", "hunter2", true), (password.Source, password.Location, password.Value, password.IsSensitive));

        var env = Assert.Single(resolver.Resolve(Env("web", "app", "MODE"), diagnostics));
        Assert.Equal("line 27, workload web, container app env MODE", env.Location);
        Assert.Empty(resolver.Resolve(Env("web", "app", "FROM_CM"), diagnostics));
        Assert.Empty(resolver.Resolve(Env("web", "other", "MODE"), diagnostics));
        Assert.Empty(resolver.Resolve(new ProvenanceStep { Kind = LayerKind.GitOps, Source = "t", Reference = new GitOpsVariableRef("other", "ONE") }, diagnostics));

        Assert.Equal(
            "Flux Kustomization apps reads substituteFrom ConfigMap missing, which was not loaded; pass it with the GitOps data manifests.",
            diagnostics.Warnings[0]);
        Assert.StartsWith("Could not parse Kubernetes manifests in apps/broken.yml: ", diagnostics.Warnings[1], StringComparison.Ordinal);
        Assert.Equal(2, diagnostics.Warnings.Count);

        Assert.Equal(
            [
                new SourceEntry("ONE", new GitOpsVariableRef("apps", "ONE"), false, "postBuild.substitute", "Flux Kustomization apps"),
                new SourceEntry("PASSWORD", new GitOpsVariableRef("apps", "PASSWORD"), true, "substituteFrom Secret secret-vars", "Flux Kustomization apps"),
                new SourceEntry("UNUSED", new GitOpsVariableRef("apps", "UNUSED"), false, "substituteFrom ConfigMap vars", "Flux Kustomization apps"),
            ],
            resolver.Entries());
    }

    [Fact]
    public void Warns_when_the_manifests_behind_substitutions_cannot_be_found()
    {
        var noRoot = new GitOpsResolver(Kustomization("    substitute: {A: b}"));
        Assert.Equal(["Flux Kustomization apps substitutes ${VAR} placeholders; pass the repository checkout to link them to the manifests in ./apps."], noRoot.Warnings);

        var noPath = new GitOpsResolver(GitOpsApplication.Parse("""
            apiVersion: kustomize.toolkit.fluxcd.io/v1
            kind: Kustomization
            metadata: {name: root}
            spec:
              postBuild: {substitute: {A: b}}
            """));
        Assert.Equal(["Flux Kustomization root substitutes ${VAR} placeholders; pass the repository checkout to link them to the manifests in the repository."], noPath.Warnings);

        var missingFolder = new GitOpsResolver(Kustomization("    substitute: {A: b}"), new GitOpsOptions { RepositoryRoot = _root });
        Assert.Equal([$"The folder {Path.Join(_root, "apps")} that Flux Kustomization apps deploys does not exist."], missingFolder.Warnings);

        var noSubstitutions = new GitOpsResolver(Kustomization("    wait: true"), new GitOpsOptions { RepositoryRoot = _root });
        Assert.Empty(noSubstitutions.Warnings);
        Assert.Empty(noSubstitutions.Entries());
    }

    [Fact]
    public void A_kustomization_without_a_path_scans_the_repository_root()
    {
        Write("cm.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: c}\ndata: {K: '${V}'}\n");
        var resolver = new GitOpsResolver(
            GitOpsApplication.Parse("""
                apiVersion: kustomize.toolkit.fluxcd.io/v1
                kind: Kustomization
                metadata: {name: root}
                spec:
                  postBuild:
                    substituteFrom: [{kind: ConfigMap, name: optional, optional: true}]
                """),
            new GitOpsOptions { RepositoryRoot = _root });

        Assert.Empty(resolver.Warnings);
        Assert.Equal("cm.yaml", Assert.Single(resolver.Resolve(ConfigMapKey("c", "K"), new TraceDiagnostics())).Source);
    }

    [Fact]
    public void A_key_that_is_secret_by_name_never_shows_its_placeholder_default_in_a_note()
    {
        // Neither key comes from a Secret: only their names say they are secret.
        Write("apps/app.yaml", """
            apiVersion: v1
            kind: ConfigMap
            metadata: {name: cfg}
            data:
              Db__Password: ${DB_PASSWORD:=fake-default-pw}
            ---
            apiVersion: v1
            kind: Pod
            metadata: {name: p}
            spec:
              containers:
                - name: c
                  env:
                    - name: Api__Token
                      value: ${API_TOKEN:-fake-default-token}
            """);
        var manifests = KubernetesManifests.Parse("""
            apiVersion: v1
            kind: ConfigMap
            metadata: {name: cfg}
            data:
              Db__Password: fake-default-pw
            ---
            apiVersion: v1
            kind: Pod
            metadata: {name: p}
            spec:
              containers:
                - name: c
                  envFrom:
                    - configMapRef: {name: cfg}
                  env:
                    - name: Api__Token
                      value: fake-default-token
            """);

        var report = ConfigTracer.ForContainer(manifests)
            .WithGitOps(Kustomization("    substitute: {REGION: eu}"), new GitOpsOptions { RepositoryRoot = _root })
            .Trace();
        var output = report.ToText() + report.ToJson() + string.Join("\n", report.Warnings);

        Assert.Equal(["Api__Token", "Db__Password"], report.Settings.Select(s => s.Key));
        Assert.All(report.Settings, s => Assert.True(s.IsSensitive));
        Assert.DoesNotContain("fake-default-pw", output, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-default-token", output, StringComparison.Ordinal);
        Assert.All(report.Settings, s => Assert.EndsWith(
            ", so Flux uses the placeholder's default",
            Assert.Single(Assert.Single(s.Steps).Origins).Note,
            StringComparison.Ordinal));
    }

    [Fact]
    public void Does_not_follow_links_in_the_folder_it_deploys()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        var outside = Directory.CreateTempSubdirectory("cf-gitops-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Join(outside, "other.yaml"), "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: o}\ndata: {K: '${V}'}\n");
            Write("cm.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: c}\ndata: {K: '${V}'}\n");
            Directory.CreateSymbolicLink(Path.Join(_root, "loop"), ".");
            Directory.CreateSymbolicLink(Path.Join(_root, "extra"), outside);
            var resolver = new GitOpsResolver(
                GitOpsApplication.Parse("""
                    apiVersion: kustomize.toolkit.fluxcd.io/v1
                    kind: Kustomization
                    metadata: {name: root}
                    spec:
                      postBuild:
                        substitute: {V: x}
                    """),
                new GitOpsOptions { RepositoryRoot = _root });

            Assert.Equal("cm.yaml", Assert.Single(resolver.Resolve(ConfigMapKey("c", "K"), new TraceDiagnostics())).Source);
            Assert.Empty(resolver.Resolve(ConfigMapKey("o", "K"), new TraceDiagnostics()));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Theory]
    [InlineData("${A}", "A", null)]
    [InlineData("${A:=x}", "A", "x")]
    [InlineData("${A:-y}", "A", "y")]
    [InlineData("${A=z}", "A", "z")]
    [InlineData("${A-w}", "A", "w")]
    [InlineData("${A:=}", "A", "")]
    [InlineData("${A:0:2}", "A", null)]
    [InlineData("${A^^}", "A", null)]
    [InlineData("pre ${_b9} post", "_b9", null)]
    public void Reads_flux_placeholders(string value, string name, string? fallback)
    {
        Assert.Equal([(name, fallback)], GitOpsResolver.ReadVariables(value));
    }

    [Theory]
    [InlineData("$A")]
    [InlineData("$${A}")]
    [InlineData("${1A}")]
    [InlineData("${}")]
    [InlineData("{A}")]
    public void Ignores_what_flux_does_not_substitute(string value)
    {
        Assert.Empty(GitOpsResolver.ReadVariables(value));
    }
}
