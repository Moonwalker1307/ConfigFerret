using System.Runtime.Versioning;
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
        Assert.Empty(resolver.Resolve(ConfigMapKey("billing-config-production", "Currency"), diagnostics));
        Assert.Empty(resolver.Resolve(ConfigMapKey("prod-billing-config-eu-monitoring", "Currency"), diagnostics));
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

        // A template names db.password, so it is used even with no step that links to it, and it stays a secret entry.
        var loaded = new LoadedConfigSource("flux", resolver, resolver.Entries());
        Assert.DoesNotContain(loaded.FindUnused(new ProvenanceReport([], [])), e => e.Name == "db.password");
        Assert.Contains(password, loaded.Entries);
    }

    [Fact]
    public void A_template_line_that_renders_a_parent_value_is_secret_when_a_value_below_it_comes_from_a_secret()
    {
        Write("chart/values.yaml", "db:\n  host: db.local\n  password: none\n");
        Write("chart/templates/cm.yaml", """
            data:
              Db__Json: {{ .Values.db | toJson | quote }}
              Db__Host: {{ .Values.db.host }}
            """);
        var release = GitOpsApplication.Parse("""
            apiVersion: helm.toolkit.fluxcd.io/v2
            kind: HelmRelease
            metadata: {name: rel}
            spec:
              chart: {spec: {chart: orders, sourceRef: {kind: HelmRepository, name: repo}}}
              valuesFrom:
                - {kind: Secret, name: db, valuesKey: password, targetPath: db.password}
            ---
            apiVersion: v1
            kind: Secret
            metadata: {name: db}
            stringData: {password: fake-helm-json-s3cr3t}
            """, "rel.yaml");
        var manifests = KubernetesManifests.Parse("""
            apiVersion: v1
            kind: ConfigMap
            metadata: {name: cfg}
            data:
              Db__Json: '{"host":"db.local","password":"fake-helm-json-s3cr3t"}'
              Db__Host: db.local
            ---
            apiVersion: v1
            kind: Pod
            metadata: {name: p}
            spec:
              containers:
                - name: c
                  envFrom:
                    - configMapRef: {name: cfg}
            """);

        var report = ConfigTracer.ForContainer(manifests)
            .WithGitOps(release, new GitOpsOptions { ChartFolder = Path.Join(_root, "chart") })
            .Trace();
        var output = report.ToText() + report.ToJson() + string.Join("\n", report.Warnings);

        Assert.DoesNotContain("fake-helm-json-s3cr3t", output, StringComparison.Ordinal);
        Assert.True(Assert.Single(report.Settings, s => s.Key == "Db__Json").IsSensitive);
        Assert.Equal("db.local", Assert.Single(report.Settings, s => s.Key == "Db__Host").Value);
    }

    [Fact]
    public void A_deep_secret_values_path_costs_memory_in_proportion_to_its_length()
    {
        // Every parent of a 10,000-level path written out as its own string would take about 200 MB.
        var targetPath = string.Join('.', Enumerable.Repeat("a", 10_000));
        Write("chart/templates/cm.yaml", "data:\n  Db__Json: {{ .Values.a | toJson | quote }}\n");
        var release = GitOpsApplication.Parse($"""
            apiVersion: helm.toolkit.fluxcd.io/v2
            kind: HelmRelease
            metadata: {"{"}name: rel{"}"}
            spec:
              chart: {"{"}spec: {"{"}chart: orders, sourceRef: {"{"}kind: HelmRepository, name: repo{"}"}{"}"}{"}"}
              valuesFrom:
                - {"{"}kind: Secret, name: s, valuesKey: k, targetPath: {targetPath}{"}"}
            ---
            apiVersion: v1
            kind: Secret
            metadata: {"{"}name: s{"}"}
            stringData: {"{"}k: fake-deep-s3cr3t{"}"}
            """, "rel.yaml");
        var options = new GitOpsOptions { ChartFolder = Path.Join(_root, "chart") };

        var before = GC.GetAllocatedBytesForCurrentThread();
        var resolver = new GitOpsResolver(release, options);
        var step = Assert.Single(resolver.Resolve(ConfigMapKey("cfg", "Db__Json"), new TraceDiagnostics()));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(step.IsSensitive);
        Assert.True(allocated < 20_000_000, $"Building the resolver allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void A_template_line_that_renders_several_values_is_secret_when_one_comes_from_a_secret()
    {
        Write("chart/values.yaml", "db:\n  host: db.local\n  route: none\n");
        Write("chart/templates/cm.yaml", """
            data:
              Db__Route: "{{ .Values.db.host }}/{{ .Values.db.route }}"
              Db__Host: {{ .Values.db.host }}
            """);
        var release = GitOpsApplication.Parse("""
            apiVersion: helm.toolkit.fluxcd.io/v2
            kind: HelmRelease
            metadata: {name: rel}
            spec:
              chart: {spec: {chart: orders, sourceRef: {kind: HelmRepository, name: repo}}}
              valuesFrom:
                - {kind: Secret, name: route, valuesKey: route, targetPath: db.route}
            ---
            apiVersion: v1
            kind: Secret
            metadata: {name: route}
            stringData: {route: fake-helm-route-s3cr3t}
            """, "rel.yaml");
        var manifests = KubernetesManifests.Parse("""
            apiVersion: v1
            kind: ConfigMap
            metadata: {name: cfg}
            data:
              Db__Route: db.local/fake-helm-route-s3cr3t
              Db__Host: db.local
            ---
            apiVersion: v1
            kind: Pod
            metadata: {name: p}
            spec:
              containers:
                - name: c
                  envFrom:
                    - configMapRef: {name: cfg}
            """);

        var report = ConfigTracer.ForContainer(manifests)
            .WithGitOps(release, new GitOpsOptions { ChartFolder = Path.Join(_root, "chart") })
            .Trace();
        var output = report.ToText() + report.ToJson() + string.Join("\n", report.Warnings);

        Assert.DoesNotContain("fake-helm-route-s3cr3t", output, StringComparison.Ordinal);
        Assert.True(Assert.Single(report.Settings, s => s.Key == "Db__Route").IsSensitive);
        Assert.Equal("db.local", Assert.Single(report.Settings, s => s.Key == "Db__Host").Value);
    }

    [Fact]
    public void Values_that_a_template_names_outside_config_keys_are_not_unused()
    {
        Write("chart/Chart.yaml", "name: orders\n");
        Write("chart/values.yaml", "replicaCount: 1\nimage: {repository: orders, tag: latest}\ndb: {timeout: 30}\n");
        Write("chart/values-prod.yaml", "replicaCount: 3\ndb: {timeout: 40}\nresources: {limits: {cpu: 500m}}\nnobody: {reads: this}\n");
        Write("chart/templates/deployment.yaml", """
            spec:
              replicas: {{ .Values.replicaCount }}
              template:
                spec:
                  containers:
                    - name: orders
                      image: "{{ .Values.image.repository }}:{{ .Values.image.tag }}"
                      resources:
                        {{- toYaml .Values.resources | nindent 12 }}
                      env:
                        - name: Db__Timeout
                          value: "{{ .Values.db.timeout }}"
            """);
        var app = GitOpsApplication.Parse("""
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            metadata: {name: orders}
            spec:
              source:
                path: chart
                helm:
                  valueFiles: [values-prod.yaml]
                  parameters:
                    - {name: image.tag, value: 1.4.2}
            """, "orders.yaml");
        var resolver = new GitOpsResolver(app, new GitOpsOptions { RepositoryRoot = _root });
        var tracer = ConfigTracer.ForContainer(KubernetesManifests.Parse("""
            apiVersion: v1
            kind: Pod
            metadata: {name: orders}
            spec:
              containers:
                - name: orders
                  env:
                    - {name: Db__Timeout, value: "40"}
            """));
        var loaded = new LoadedConfigSource("argo", resolver, resolver.Entries());
        tracer.WithSource(loaded);

        Assert.Equal([new SourceEntry("nobody.reads", new HelmValueRef("nobody.reads"), false, "values-prod.yaml", "Argo CD Application orders")], loaded.FindUnused(tracer.Trace()));

        // Entries still lists every value the Application sets.
        Assert.Equal(["db.timeout", "image.tag", "nobody.reads", "replicaCount", "resources.limits.cpu"], loaded.Entries.Select(e => e.Name));
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
    public void A_variable_that_a_manifest_in_the_folder_uses_anywhere_is_not_unused()
    {
        Write("apps/app.yaml", """
            apiVersion: v1
            kind: ConfigMap
            metadata: {name: cfg}
            data:
              Smtp__Endpoint: "${SMTP_HOST}:${SMTP_PORT}"
              Region: ${REGION}
            ---
            apiVersion: networking.k8s.io/v1
            kind: Ingress
            metadata: {name: api}
            spec:
              rules:
                - host: api.${CLUSTER_DOMAIN}
            """);
        var resolver = new GitOpsResolver(
            Kustomization("""
                    substitute: {SMTP_HOST: smtp.local, SMTP_PORT: '25', REGION: eu, REALLY_UNUSED: x}
                    substituteFrom:
                      - {kind: Secret, name: cluster}
                """),
            new GitOpsOptions
            {
                RepositoryRoot = _root,
                Data = KubernetesManifests.Parse("""
                    apiVersion: v1
                    kind: Secret
                    metadata: {name: cluster}
                    stringData: {CLUSTER_DOMAIN: fake.example.test}
                    """, "secrets.yaml"),
            });
        var tracer = ConfigTracer.ForContainer(KubernetesManifests.Parse("""
            apiVersion: v1
            kind: ConfigMap
            metadata: {name: cfg}
            data: {Smtp__Endpoint: "smtp.local:25", Region: eu}
            ---
            apiVersion: v1
            kind: Pod
            metadata: {name: p}
            spec:
              containers:
                - name: c
                  envFrom:
                    - configMapRef: {name: cfg}
            """));
        var loaded = new LoadedConfigSource("flux", resolver, resolver.Entries());
        tracer.WithSource(loaded);

        Assert.Equal(["REALLY_UNUSED"], loaded.FindUnused(tracer.Trace()).Select(e => e.Name));

        // Entries still lists every variable that was read, secret ones too.
        Assert.Equal(["CLUSTER_DOMAIN", "REALLY_UNUSED", "REGION", "SMTP_HOST", "SMTP_PORT"], loaded.Entries.Select(e => e.Name));
        Assert.Equal(
            new SourceEntry("CLUSTER_DOMAIN", new GitOpsVariableRef("apps", "CLUSTER_DOMAIN"), true, "substituteFrom Secret cluster", "Flux Kustomization apps"),
            loaded.Entries[0]);
    }

    [Fact]
    public void Warns_when_the_manifests_behind_substitutions_cannot_be_found()
    {
        var noRoot = new GitOpsResolver(Kustomization("    substitute: {A: b}"));
        Assert.Equal(["Flux Kustomization apps substitutes ${VAR} placeholders; pass the repository checkout to link them to the manifests in ./apps. Until then no variable is listed as unused."], noRoot.Warnings);
        AssertNoneUnused(noRoot, "A");

        var noPath = new GitOpsResolver(GitOpsApplication.Parse("""
            apiVersion: kustomize.toolkit.fluxcd.io/v1
            kind: Kustomization
            metadata: {name: root}
            spec:
              postBuild: {substitute: {A: b}}
            """));
        Assert.Equal(["Flux Kustomization root substitutes ${VAR} placeholders; pass the repository checkout to link them to the manifests in the repository. Until then no variable is listed as unused."], noPath.Warnings);
        AssertNoneUnused(noPath, "A");

        var missingFolder = new GitOpsResolver(Kustomization("    substitute: {A: b}"), new GitOpsOptions { RepositoryRoot = _root });
        Assert.Equal([$"The folder {Path.Join(_root, "apps")} that Flux Kustomization apps deploys does not exist."], missingFolder.Warnings);

        // Flux fails on a path that does not exist, so its variables are still listed as unused.
        Assert.Equal(["A"], new LoadedConfigSource("flux", missingFolder, missingFolder.Entries()).FindUnused(new ProvenanceReport([], [])).Select(e => e.Name));

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
    public void Warns_once_about_a_config_map_in_the_deployed_folder_whose_name_holds_a_slash()
    {
        Write("apps/cm.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: a/b}\ndata: {K: '${VAR}'}\n");
        var resolver = new GitOpsResolver(Kustomization("    substitute: {VAR: fake-value}"), new GitOpsOptions { RepositoryRoot = _root });
        var diagnostics = new TraceDiagnostics();

        resolver.Resolve(ConfigMapKey("a/b", "K"), diagnostics);
        resolver.Resolve(ConfigMapKey("a/b", "K"), diagnostics);

        Assert.Equal(["Skipped ConfigMap a/b in apps/cm.yaml: a Kubernetes name cannot hold a slash."], resolver.Warnings);
        Assert.Single(diagnostics.Warnings, w => w.StartsWith("Skipped ConfigMap a/b", StringComparison.Ordinal));
    }

    [Fact]
    public void Warns_about_a_config_map_whose_name_holds_a_slash()
    {
        var resolver = new GitOpsResolver(
            GitOpsApplication.Parse("""
                apiVersion: kustomize.toolkit.fluxcd.io/v1
                kind: Kustomization
                metadata: {name: apps, namespace: staging}
                spec:
                  postBuild:
                    substituteFrom: [{kind: ConfigMap, name: vars, optional: true}]
                ---
                apiVersion: v1
                kind: ConfigMap
                metadata: {name: staging/vars}
                data: {HOST: fake-host}
                """, "apps.yaml"),
            new GitOpsOptions { RepositoryRoot = _root });

        Assert.Equal(["Skipped ConfigMap staging/vars in apps.yaml: a Kubernetes name cannot hold a slash."], resolver.Warnings);
    }

    [Fact]
    public void Substitutes_from_the_config_map_in_the_kustomization_namespace()
    {
        var resolver = new GitOpsResolver(
            GitOpsApplication.Parse("""
                apiVersion: kustomize.toolkit.fluxcd.io/v1
                kind: Kustomization
                metadata: {name: apps, namespace: staging}
                spec:
                  postBuild:
                    substituteFrom: [{kind: ConfigMap, name: vars}]
                ---
                apiVersion: v1
                kind: ConfigMap
                metadata: {name: vars, namespace: staging}
                data: {HOST: staging-db.internal}
                ---
                apiVersion: v1
                kind: ConfigMap
                metadata: {name: vars, namespace: prod}
                data: {HOST: prod-db.internal}
                """, "apps.yaml"),
            new GitOpsOptions { RepositoryRoot = _root });

        var host = resolver.Resolve(new ProvenanceStep { Kind = LayerKind.GitOps, Source = "t", Reference = new GitOpsVariableRef("apps", "HOST") }, new TraceDiagnostics());

        Assert.Equal(["staging-db.internal"], host.Select(h => h.Value));
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
    public void A_value_with_several_placeholders_is_secret_when_one_is_filled_from_a_secret()
    {
        Write("apps/cm.yaml", """
            apiVersion: v1
            kind: ConfigMap
            metadata: {name: cfg}
            data:
              Billing__Route: ${HOST}/${ROUTE}
              Billing__Region: ${HOST}-${REGION}
            """);
        var manifests = KubernetesManifests.Parse("""
            apiVersion: v1
            kind: ConfigMap
            metadata: {name: cfg}
            data:
              Billing__Route: billing.local/fake-flux-route-s3cr3t
              Billing__Region: billing.local-eu
            ---
            apiVersion: v1
            kind: Pod
            metadata: {name: p}
            spec:
              containers:
                - name: c
                  envFrom:
                    - configMapRef: {name: cfg}
            """);

        // The Secret sets HOST too, but the inline substitute wins, so the region is not built from the Secret.
        var report = ConfigTracer.ForContainer(manifests)
            .WithGitOps(
                Kustomization(
                    """
                        substitute: {HOST: billing.local, REGION: eu}
                        substituteFrom:
                          - {kind: Secret, name: route}
                    """,
                    """
                    ---
                    apiVersion: v1
                    kind: Secret
                    metadata: {name: route}
                    stringData: {ROUTE: fake-flux-route-s3cr3t, HOST: fake-flux-host-s3cr3t}
                    """),
                new GitOpsOptions { RepositoryRoot = _root })
            .Trace();
        var output = report.ToText() + report.ToJson() + string.Join("\n", report.Warnings);

        Assert.DoesNotContain("fake-flux-route-s3cr3t", output, StringComparison.Ordinal);
        Assert.True(Assert.Single(report.Settings, s => s.Key == "Billing__Route").IsSensitive);
        Assert.Equal("billing.local-eu", Assert.Single(report.Settings, s => s.Key == "Billing__Region").Value);
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
            Assert.Equal(
                ["Flux Kustomization root: 1 link under . leads out of it and was not followed, so it was not read and no variable is listed as unused."],
                resolver.Warnings);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Does_not_read_a_source_path_or_kustomization_path_outside_the_repository()
    {
        Write("outside/Chart.yaml", "name: c\n");
        Write("outside/cm.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: o}\ndata: {K: '${V}'}\n");
        var options = new GitOpsOptions { RepositoryRoot = Directory.CreateDirectory(Path.Join(_root, "repo")).FullName };

        var argo = new GitOpsResolver(OutsidePath("argoproj.io/v1alpha1", "Application", "spec: {source: {path: ../outside}}"), options);
        Assert.Null(argo.Chart);
        Assert.Equal(["The source path ../outside of Argo CD Application out is outside the repository checkout, so it was not read."], argo.Warnings);

        var flux = new GitOpsResolver(OutsidePath("kustomize.toolkit.fluxcd.io/v1", "Kustomization", "spec: {path: ../outside, postBuild: {substitute: {V: x}}}"), options);
        Assert.Empty(flux.Resolve(ConfigMapKey("o", "K"), new TraceDiagnostics()));
        Assert.Equal(["The folder ../outside that Flux Kustomization out deploys is outside the repository checkout, so it was not read and no variable is listed as unused."], flux.Warnings);
        AssertNoneUnused(flux, "V");
    }

    [Fact]
    public void Does_not_read_a_kustomization_path_that_links_outside_the_repository()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        Write("outside/cm.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: o}\ndata: {K: '${V}'}\n");
        var repo = Directory.CreateDirectory(Path.Join(_root, "repo")).FullName;
        Directory.CreateSymbolicLink(Path.Join(repo, "linked"), Path.Join(_root, "outside"));

        var flux = new GitOpsResolver(OutsidePath("kustomize.toolkit.fluxcd.io/v1", "Kustomization", "spec: {path: ./linked, postBuild: {substitute: {V: x}}}"), new GitOpsOptions { RepositoryRoot = repo });

        Assert.Empty(flux.Resolve(ConfigMapKey("o", "K"), new TraceDiagnostics()));
        Assert.Equal(["The folder ./linked that Flux Kustomization out deploys is outside the repository checkout, so it was not read and no variable is listed as unused."], flux.Warnings);
    }

    [Fact]
    public void A_chart_yaml_that_links_outside_never_makes_a_chart_whether_or_not_its_target_exists()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        Write("outside/Chart.yaml", "name: c\n");
        var repo = Directory.CreateDirectory(Path.Join(_root, "repo")).FullName;
        foreach (var (folder, target) in new[] { ("found", "Chart.yaml"), ("missing", "missing.yaml") })
        {
            Directory.CreateDirectory(Path.Join(repo, folder));
            File.CreateSymbolicLink(Path.Join(repo, folder, "Chart.yaml"), Path.Join(_root, "outside", target));
        }

        var options = new GitOpsOptions { RepositoryRoot = repo };
        var found = new GitOpsResolver(OutsidePath("argoproj.io/v1alpha1", "Application", "spec: {source: {path: found}}"), options);
        var missing = new GitOpsResolver(OutsidePath("argoproj.io/v1alpha1", "Application", "spec: {source: {path: missing}}"), options);

        Assert.Null(found.Chart);
        Assert.Null(missing.Chart);
        Assert.Equal(found.Warnings, missing.Warnings);
    }

    [Fact]
    public void A_path_that_is_not_a_path_is_outside_the_repository()
    {
        var options = new GitOpsOptions { RepositoryRoot = _root };

        var argo = new GitOpsResolver(OutsidePath("argoproj.io/v1alpha1", "Application", "spec: {source: {path: \"apps\\0\"}}"), options);
        Assert.Null(argo.Chart);
        Assert.Equal(["The source path apps\0 of Argo CD Application out is outside the repository checkout, so it was not read."], argo.Warnings);

        var flux = new GitOpsResolver(OutsidePath("kustomize.toolkit.fluxcd.io/v1", "Kustomization", "spec: {path: \"apps\\0\", postBuild: {substitute: {V: x}}}"), options);
        Assert.Equal(["The folder apps\0 that Flux Kustomization out deploys is outside the repository checkout, so it was not read and no variable is listed as unused."], flux.Warnings);

        var release = new GitOpsResolver(OutsidePath("helm.toolkit.fluxcd.io/v2", "HelmRelease", "spec: {chart: {spec: {chart: \"charts\\0\", sourceRef: {kind: GitRepository, name: repo}}}}"), options);
        Assert.Equal(["The chart folder charts\0 for Flux HelmRelease out is outside the repository checkout, so it was not read."], release.Warnings);
    }

    [Fact]
    public void A_kustomization_path_that_links_to_the_repository_root_is_read()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        Write("cm.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: c}\ndata: {K: '${V}'}\n");
        Directory.CreateSymbolicLink(Path.Join(_root, "linked"), ".");

        var flux = new GitOpsResolver(OutsidePath("kustomize.toolkit.fluxcd.io/v1", "Kustomization", "spec: {path: ./linked, postBuild: {substitute: {V: x}}}"), new GitOpsOptions { RepositoryRoot = _root });

        Assert.Empty(flux.Warnings);
        Assert.Equal("linked/cm.yaml", Assert.Single(flux.Resolve(ConfigMapKey("c", "K"), new TraceDiagnostics())).Source);
    }

    private static GitOpsApplication OutsidePath(string apiVersion, string kind, string spec) => GitOpsApplication.Parse($"""
        apiVersion: {apiVersion}
        kind: {kind}
        metadata: {"{"}name: out{"}"}
        {spec}
        """, "out.yaml");

    [Fact]
    public void Links_out_of_the_folder_count_as_not_read()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs extra rights on Windows.");
        Write("apps/cm.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: c}\ndata: {K: '${V}'}\n");
        Write("outside/extra.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: o}\ndata: {K: '${UNUSED}'}\n");
        File.CreateSymbolicLink(Path.Join(_root, "apps", "extra.yaml"), Path.Join(_root, "outside", "extra.yaml"));
        Directory.CreateSymbolicLink(Path.Join(_root, "apps", "more"), Path.Join(_root, "outside"));

        // A link to a place inside the folder is read under its own name, and a dangling one leads nowhere.
        Directory.CreateSymbolicLink(Path.Join(_root, "apps", "loop"), ".");
        File.CreateSymbolicLink(Path.Join(_root, "apps", "gone.yaml"), Path.Join(_root, "missing.yaml"));

        var resolver = new GitOpsResolver(Kustomization("    substitute: {V: x, UNUSED: y}"), new GitOpsOptions { RepositoryRoot = _root });

        Assert.Equal(
            ["Flux Kustomization apps: 2 links under apps lead out of it and were not followed, so they were not read and no variable is listed as unused."],
            resolver.Warnings);
        Assert.Empty(resolver.Resolve(ConfigMapKey("o", "K"), new TraceDiagnostics()));
        AssertNoneUnused(resolver);
    }

    [Fact]
    public void A_dangling_link_in_the_folder_it_deploys_is_skipped_like_any_link()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs extra rights on Windows.");
        Write("apps/cm.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: c}\ndata: {K: '${V}'}\n");
        File.CreateSymbolicLink(Path.Join(_root, "apps", "local.yaml"), Path.Join(_root, "missing.yaml"));

        var resolver = new GitOpsResolver(Kustomization("    substitute: {V: x}"), new GitOpsOptions { RepositoryRoot = _root });

        Assert.Equal("apps/cm.yaml", Assert.Single(resolver.Resolve(ConfigMapKey("c", "K"), new TraceDiagnostics())).Source);
        Assert.Empty(resolver.Warnings);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void A_file_it_may_not_read_in_the_folder_it_deploys_is_a_warning()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses Unix file modes.");
        Write("apps/cm.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: c}\ndata: {K: '${V}'}\n");
        var locked = Write("apps/locked.yaml", "a: 1\n");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            Assert.SkipWhen(CanRead(locked), "The process may read any file, for example as root.");

            var resolver = new GitOpsResolver(Kustomization("    substitute: {V: x}"), new GitOpsOptions { RepositoryRoot = _root });

            Assert.StartsWith(
                "Could not read apps/locked.yaml, so no variable of Flux Kustomization apps is listed as unused: ",
                Assert.Single(resolver.Warnings),
                StringComparison.Ordinal);
            Assert.Equal("apps/cm.yaml", Assert.Single(resolver.Resolve(ConfigMapKey("c", "K"), new TraceDiagnostics())).Source);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void A_folder_it_may_not_read_leaves_the_other_folders_read()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses Unix file modes.");
        Write("apps/a/cm.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: a}\ndata: {K: '${A}'}\n");
        Write("apps/b/cm.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: b}\ndata: {K: '${V}'}\n");
        var locked = Path.Join(_root, "apps", "a");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            Assert.SkipWhen(CanRead(locked), "The process may read any folder, for example as root.");

            var resolver = new GitOpsResolver(Kustomization("    substitute: {V: x, UNUSED: y}"), new GitOpsOptions { RepositoryRoot = _root });

            Assert.StartsWith(
                "Could not read the folder apps/a, so no variable of Flux Kustomization apps is listed as unused: ",
                Assert.Single(resolver.Warnings),
                StringComparison.Ordinal);
            Assert.Equal("apps/b/cm.yaml", Assert.Single(resolver.Resolve(ConfigMapKey("b", "K"), new TraceDiagnostics())).Source);

            // apps/a may name UNUSED, so it is not called unused, and it is still an entry.
            var loaded = new LoadedConfigSource("flux", resolver, resolver.Entries());
            Assert.Empty(loaded.FindUnused(new ProvenanceReport([], [])));
            Assert.Equal(["UNUSED", "V"], loaded.Entries.Select(e => e.Name));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void A_backslash_in_a_file_name_under_the_folder_stays_a_backslash()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A backslash is the separator on Windows.");
        Write("apps/a\\b.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: slash}\ndata: {K: '${V}'}\n");
        Write("apps/a/b.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: nested}\ndata: {K: '${V}'}\n");

        var resolver = new GitOpsResolver(Kustomization("    substitute: {V: x}"), new GitOpsOptions { RepositoryRoot = _root });

        Assert.Equal("apps/a\\b.yaml", Assert.Single(resolver.Resolve(ConfigMapKey("slash", "K"), new TraceDiagnostics())).Source);
        Assert.Equal("apps/a/b.yaml", Assert.Single(resolver.Resolve(ConfigMapKey("nested", "K"), new TraceDiagnostics())).Source);
    }

    [Fact]
    public void Lists_unused_variables_when_every_file_in_the_folder_was_read()
    {
        Write("apps/b/cm.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: b}\ndata: {K: '${V}'}\n");

        var resolver = new GitOpsResolver(Kustomization("    substitute: {V: x, UNUSED: y}"), new GitOpsOptions { RepositoryRoot = _root });

        Assert.Empty(resolver.Warnings);
        Assert.Equal(["UNUSED"], new LoadedConfigSource("flux", resolver, resolver.Entries()).FindUnused(new ProvenanceReport([], [])).Select(e => e.Name));
    }

    [Fact]
    public void The_scan_of_the_folder_reads_at_most_the_limits_the_readme_names()
    {
        Assert.Equal(new FluxScanLimits(100_000, 256L * 1024 * 1024, 64, 16L * 1024 * 1024), FluxScanLimits.Default);
        Assert.Equal(FluxScanLimits.Default, new GitOpsOptions().ScanLimits);
    }

    [Fact]
    public void Stops_at_the_limit_on_files_and_folders_and_lists_no_variable_as_unused()
    {
        for (var i = 0; i < 10; i++)
        {
            Write($"apps/cm{i}.yaml", $"apiVersion: v1\nkind: ConfigMap\nmetadata: {{name: c{i}}}\ndata: {{K: '${{V}}'}}\n");
        }

        var resolver = new GitOpsResolver(
            Kustomization("    substitute: {V: x, UNUSED: y}"),
            new GitOpsOptions { RepositoryRoot = _root, ScanLimits = FluxScanLimits.Default with { Entries = 4 } });

        Assert.Equal(
            ["Flux Kustomization apps: apps holds more than 4 files and folders, so the rest were not read and no variable is listed as unused."],
            resolver.Warnings);
        AssertNoneUnused(resolver);
    }

    [Fact]
    public void Skips_a_file_larger_than_16_mib_and_lists_no_variable_as_unused()
    {
        Write("apps/a.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: a}\ndata: {K: '${V}'}\n");
        var header = "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: big}\ndata: {K: '${V}'}\n# ";
        Write("apps/big.yaml", header + new string('x', (16 * 1024 * 1024) + 1 - header.Length));
        Write("apps/c.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: c}\ndata: {K: '${V}'}\n");

        var resolver = new GitOpsResolver(Kustomization("    substitute: {V: x, UNUSED: y}"), new GitOpsOptions { RepositoryRoot = _root });

        Assert.Equal(
            ["Flux Kustomization apps: apps/big.yaml is larger than 16777216 bytes, so it was not read and no variable is listed as unused."],
            resolver.Warnings);
        Assert.Single(resolver.Resolve(ConfigMapKey("a", "K"), new TraceDiagnostics()));
        Assert.Empty(resolver.Resolve(ConfigMapKey("big", "K"), new TraceDiagnostics()));
        Assert.Single(resolver.Resolve(ConfigMapKey("c", "K"), new TraceDiagnostics()));
        AssertNoneUnused(resolver);
    }

    [Fact]
    public void Stops_before_a_file_past_the_limit_on_bytes_and_lists_no_variable_as_unused()
    {
        Write("apps/a.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: a}\ndata: {K: '${V}'}\n");
        Write("apps/big.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: big}\ndata: {K: '${V}'}\n# " + new string('x', 4096) + "\n");

        var resolver = new GitOpsResolver(
            Kustomization("    substitute: {V: x, UNUSED: y}"),
            new GitOpsOptions { RepositoryRoot = _root, ScanLimits = FluxScanLimits.Default with { Bytes = 1024 } });

        Assert.Equal(
            ["Flux Kustomization apps: the YAML files under apps hold more than 1024 bytes, so apps/big.yaml and the files after it were not read and no variable is listed as unused."],
            resolver.Warnings);
        Assert.Single(resolver.Resolve(ConfigMapKey("a", "K"), new TraceDiagnostics()));
        Assert.Empty(resolver.Resolve(ConfigMapKey("big", "K"), new TraceDiagnostics()));
        AssertNoneUnused(resolver);
    }

    [Fact]
    public void Skips_folders_nested_deeper_than_the_limit_and_lists_no_variable_as_unused()
    {
        // 80 levels, more than the 64 the scan follows.
        var deep = Path.Join([.. Enumerable.Repeat("d", 80)]);
        Write(Path.Join("apps", deep, "cm.yaml"), "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: deep}\ndata: {K: '${V}'}\n");
        Write("apps/cm.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata: {name: top}\ndata: {K: '${V}'}\n");

        var resolver = new GitOpsResolver(Kustomization("    substitute: {V: x, UNUSED: y}"), new GitOpsOptions { RepositoryRoot = _root });

        var warning = Assert.Single(resolver.Warnings);
        Assert.StartsWith("Flux Kustomization apps: apps/d/d/", warning, StringComparison.Ordinal);
        Assert.EndsWith(" is more than 64 folders below apps, so it was not read and no variable is listed as unused.", warning, StringComparison.Ordinal);
        Assert.Single(resolver.Resolve(ConfigMapKey("top", "K"), new TraceDiagnostics()));
        Assert.Empty(resolver.Resolve(ConfigMapKey("deep", "K"), new TraceDiagnostics()));
        AssertNoneUnused(resolver);
    }

    private static void AssertNoneUnused(GitOpsResolver resolver, params string[] entries)
    {
        var loaded = new LoadedConfigSource("flux", resolver, resolver.Entries());
        Assert.Empty(loaded.FindUnused(new ProvenanceReport([], [])));
        Assert.Equal(entries.Length > 0 ? entries : ["UNUSED", "V"], loaded.Entries.Select(e => e.Name));
    }

    private static bool CanRead(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                _ = Directory.EnumerateFileSystemEntries(path).Any();
            }
            else
            {
                File.ReadAllText(path);
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
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
    [InlineData("$$${A}")]
    [InlineData("${A")]
    [InlineData("${A ${B")]
    public void Ignores_what_flux_does_not_substitute(string value)
    {
        Assert.Empty(GitOpsResolver.ReadVariables(value));
    }

    [Fact]
    public void Reads_every_placeholder_from_where_the_last_one_ended()
    {
        Assert.Equal([("A", null), ("B", "x")], GitOpsResolver.ReadVariables("${A}${B:=x}"));
        Assert.Equal([("A", "${B")], GitOpsResolver.ReadVariables("${A:-${B} ${C"));
        Assert.Equal([("B", null)], GitOpsResolver.ReadVariables("${1 ${B}"));
        Assert.Equal([("A", null), ("C", null)], GitOpsResolver.ReadVariables("${A}}$${B}${C}"));
    }

    [Theory(Timeout = 10_000)]
    [InlineData("")]
    [InlineData("}")]
    public async Task Reads_a_long_run_of_unclosed_placeholders_in_linear_time(string end)
    {
        // 2 MB of ${A, where each ${ could start a placeholder that ends at the next }.
        var value = string.Concat(Enumerable.Repeat("${A", 700_000)) + end;

        var variables = await Task.Run(() => GitOpsResolver.ReadVariables(value), TestContext.Current.CancellationToken);

        Assert.Equal(end.Length, variables.Count);
    }
}
