using ConfigFerret.GitOps;

namespace ConfigFerret.Tests;

public class GitOpsApplicationTests
{
    [Fact]
    public void Reads_an_argo_cd_application_with_helm_values_and_parameters()
    {
        var app = GitOpsApplication.Parse("""
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            metadata:
              name: orders-prod
              namespace: argocd
            spec:
              source:
                repoURL: https://charts.example.test
                chart: orders
                targetRevision: 1.4.2
                helm:
                  releaseName: orders
                  ignoreMissingValueFiles: true
                  valueFiles:
                    - values-prod.yaml
                    - ~
                  values: |
                    db:
                      timeout: 45
                  parameters:
                    - name: features.search
                      value: "true"
                      forceString: true
                    - name: db.pool
                    - value: no-name
            """, "app.yaml");

        Assert.Equal(GitOpsKind.ArgoCdApplication, app.Kind);
        Assert.Equal("orders-prod", app.Name);
        Assert.Equal("argocd", app.Namespace);
        Assert.Equal("app.yaml", app.SourceName);
        Assert.Equal(1, app.Line);
        Assert.Equal("Argo CD Application orders-prod", app.DisplayName);
        Assert.Null(app.Kustomize);
        Assert.Null(app.Path);
        Assert.Empty(app.Notes);
        var helm = Assert.Single(app.HelmSources);
        Assert.Equal("https://charts.example.test", helm.RepoUrl);
        Assert.Equal("orders", helm.Chart);
        Assert.Null(helm.Path);
        Assert.Equal("1.4.2", helm.TargetRevision);
        Assert.Equal("orders", helm.ReleaseName);
        Assert.True(helm.IgnoreMissingValuesFiles);
        Assert.True(helm.IsRemote);
        Assert.False(helm.ValuesFilesReplaceChartValues);
        Assert.Equal([new GitOpsValuesFile("values-prod.yaml", 15)], helm.ValuesFiles);
        Assert.Collection(
            helm.Layers,
            l =>
            {
                Assert.Equal(("helm.values", GitOpsValuesKind.Inline, 17), (l.Name, l.Kind, l.Line));
                Assert.Equal("db:\n  timeout: 45\n", l.Text);
                Assert.Equal(17, l.LineOffset);
                Assert.Null(l.Node);
            },
            l =>
            {
                Assert.Equal(("helm.parameters", GitOpsValuesKind.Parameter, 21), (l.Name, l.Kind, l.Line));
                Assert.Equal(("features.search", "true", true), (l.TargetPath, l.Value, l.ForceString));
            },
            l => Assert.Equal(("db.pool", "", false), (l.TargetPath, l.Value, l.ForceString)));
    }

    [Fact]
    public void Values_object_wins_over_values_as_in_argo_cd()
    {
        var app = GitOpsApplication.Parse("""
            apiVersion: argoproj.io/v1
            kind: Application
            metadata: {name: a}
            spec:
              source:
                path: chart
                helm:
                  values: "db: {timeout: 1}"
                  valuesObject:
                    db:
                      timeout: 2
                  fileParameters:
                    - name: cert
                      path: cert.pem
            """);

        var layer = Assert.Single(app.HelmSources[0].Layers);
        Assert.Equal("helm.valuesObject", layer.Name);
        Assert.NotNull(layer.Node);
        Assert.False(app.HelmSources[0].IsRemote);
        Assert.Equal("chart", app.HelmSources[0].Path);
        Assert.Equal("chart", app.Path);
        Assert.Equal(
            ["helm.values is ignored because helm.valuesObject is set, as Argo CD does", "helm.fileParameters (--set-file) are not traced"],
            app.Notes);
    }

    [Fact]
    public void Reads_values_written_as_a_mapping_or_a_quoted_string()
    {
        var mapping = GitOpsApplication.Parse("""
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            metadata: {name: a}
            spec:
              source:
                helm:
                  values:
                    a: 1
            """).HelmSources[0].Layers[0];
        Assert.Equal("helm.values", mapping.Name);
        Assert.NotNull(mapping.Node);

        var quoted = GitOpsApplication.Parse("""
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            metadata: {name: a}
            spec:
              source:
                helm:
                  values: "a: 1"
            """).HelmSources[0].Layers[0];
        Assert.Equal("a: 1", quoted.Text);
        Assert.Equal(6, quoted.LineOffset);
    }

    [Fact]
    public void A_source_without_chart_or_helm_is_not_a_helm_source()
    {
        var app = GitOpsApplication.Parse("""
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            metadata: {name: plain}
            spec:
              sources:
                - repoURL: https://git.example.test/x.git
                  ref: values
                  path: ignored
                - repoURL: https://git.example.test/x.git
                  path: manifests/prod
            """);

        Assert.Empty(app.HelmSources);
        Assert.Equal("manifests/prod", app.Path);
    }

    [Fact]
    public void Reads_an_application_set_template()
    {
        var named = GitOpsApplication.Parse("""
            apiVersion: argoproj.io/v1alpha1
            kind: ApplicationSet
            metadata: {name: orders-set}
            spec:
              template:
                metadata: {name: 'orders-{{cluster}}'}
                spec:
                  source:
                    chart: orders
                    helm:
                      parameters:
                        - name: region
                          value: '{{region}}'
            """);
        Assert.Equal(GitOpsKind.ArgoCdApplicationSet, named.Kind);
        Assert.Equal("orders-{{cluster}}", named.Name);
        Assert.Equal("Argo CD ApplicationSet orders-{{cluster}}", named.DisplayName);
        Assert.Equal(["ApplicationSet generator parameters such as {{name}} are not filled in, so the template is read as written"], named.Notes);
        Assert.Equal("{{region}}", named.HelmSources[0].Layers[0].Value);

        var unnamed = GitOpsApplication.Parse("""
            apiVersion: argoproj.io/v1alpha1
            kind: ApplicationSet
            metadata: {name: orders-set}
            spec:
              template:
                spec: {}
            """);
        Assert.Equal("orders-set", unnamed.Name);
    }

    [Fact]
    public void Reads_a_flux_helm_release()
    {
        var release = GitOpsApplication.Parse("""
            apiVersion: helm.toolkit.fluxcd.io/v2beta2
            kind: HelmRelease
            metadata:
              name: orders
              namespace: apps
            spec:
              releaseName: orders-eu
              chart:
                spec:
                  chart: ./charts/orders
                  version: 1.x
                  sourceRef: {kind: GitRepository, name: deploy}
                  valuesFile: legacy.yaml
                  valuesFiles: [values-prod.yaml]
                  ignoreMissingValuesFiles: true
              valuesFrom:
                - kind: ConfigMap
                  name: orders-values
                - kind: Secret
                  name: orders-db
                  valuesKey: password
                  targetPath: db.password
                  optional: true
                - kind: Other
                  name: skipped
                - kind: ConfigMap
              values:
                cache: {ttl: 900}
              postRenderers:
                - kustomize: {}
            """);

        Assert.Equal(GitOpsKind.FluxHelmRelease, release.Kind);
        Assert.Equal("Flux HelmRelease orders", release.DisplayName);
        Assert.Equal("apps", release.Namespace);
        Assert.Equal("./charts/orders", release.Path);
        Assert.Equal(["spec.postRenderers are applied by Flux after Helm renders the chart and are not traced"], release.Notes);
        var helm = Assert.Single(release.HelmSources);
        Assert.Equal("GitRepository/deploy", helm.RepoUrl);
        Assert.Null(helm.Chart);
        Assert.Equal("./charts/orders", helm.Path);
        Assert.Equal("1.x", helm.TargetRevision);
        Assert.Equal("orders-eu", helm.ReleaseName);
        Assert.False(helm.IsRemote);
        Assert.True(helm.ValuesFilesReplaceChartValues);
        Assert.True(helm.IgnoreMissingValuesFiles);
        Assert.Equal(["legacy.yaml", "values-prod.yaml"], helm.ValuesFiles.Select(f => f.Path));
        Assert.Collection(
            helm.Layers,
            l =>
            {
                Assert.Equal(("valuesFrom ConfigMap orders-values, key values.yaml", GitOpsValuesKind.ConfigMap, 17), (l.Name, l.Kind, l.Line));
                Assert.Equal(("orders-values", "values.yaml", null, false), (l.ObjectName, l.Key, l.TargetPath, l.Optional));
            },
            l =>
            {
                Assert.Equal(("valuesFrom Secret orders-db, key password", GitOpsValuesKind.Secret), (l.Name, l.Kind));
                Assert.Equal(("orders-db", "password", "db.password", true), (l.ObjectName, l.Key, l.TargetPath, l.Optional));
            },
            l => Assert.Equal(("spec.values", GitOpsValuesKind.Inline, 28), (l.Name, l.Kind, l.Line)));
    }

    [Fact]
    public void A_helm_release_from_a_helm_repository_or_chart_ref_is_remote()
    {
        var fromRepository = GitOpsApplication.Parse("""
            apiVersion: helm.toolkit.fluxcd.io/v2
            kind: HelmRelease
            metadata: {name: a}
            spec:
              chart:
                spec:
                  chart: orders
                  sourceRef: {kind: HelmRepository, name: platform}
            """).HelmSources[0];
        Assert.True(fromRepository.IsRemote);
        Assert.Equal("orders", fromRepository.Chart);
        Assert.Equal("HelmRepository/platform", fromRepository.RepoUrl);

        var fromChartRef = GitOpsApplication.Parse("""
            apiVersion: helm.toolkit.fluxcd.io/v2
            kind: HelmRelease
            metadata: {name: a}
            spec:
              chartRef: {kind: OCIRepository, name: orders}
            """);
        Assert.Equal("OCIRepository/orders", fromChartRef.HelmSources[0].RepoUrl);
        Assert.True(fromChartRef.HelmSources[0].IsRemote);
        Assert.Null(fromChartRef.Path);

        var noSource = GitOpsApplication.Parse("""
            apiVersion: helm.toolkit.fluxcd.io/v2
            kind: HelmRelease
            metadata: {name: a}
            spec: {values: [1, 2]}
            """).HelmSources[0];
        Assert.Null(noSource.RepoUrl);
        Assert.Empty(noSource.Layers);
    }

    [Fact]
    public void Reads_a_flux_kustomization()
    {
        var ks = GitOpsApplication.Parse("""
            apiVersion: kustomize.toolkit.fluxcd.io/v1
            kind: Kustomization
            metadata: {name: billing, namespace: flux-system}
            spec:
              path: ./apps/billing
              patches:
                - patch: x
              images:
                - name: billing
              postBuild:
                substitute:
                  CURRENCY: EUR
                  EMPTY: ~
                substituteFrom:
                  - kind: ConfigMap
                    name: vars
                  - kind: Secret
                    name: secrets
                    optional: true
                  - kind: Bucket
                    name: skipped
                  - kind: ConfigMap
                    name: ' '
            """);

        Assert.Equal(GitOpsKind.FluxKustomization, ks.Kind);
        Assert.Equal("Flux Kustomization billing", ks.DisplayName);
        Assert.Equal("flux-system", ks.Namespace);
        Assert.Equal("./apps/billing", ks.Path);
        Assert.Empty(ks.HelmSources);
        Assert.Equal([new GitOpsSubstitution("CURRENCY", "EUR", 12), new GitOpsSubstitution("EMPTY", "", 13)], ks.Substitutions);
        Assert.Equal([new GitOpsDataReference(false, "vars", false, 15), new GitOpsDataReference(true, "secrets", true, 17)], ks.SubstituteFrom);
        Assert.Equal(
            ["spec.patches are applied by Flux on top of the manifests in Git and are not traced", "spec.images are applied by Flux on top of the manifests in Git"],
            ks.Notes);
    }

    [Fact]
    public void Reads_argo_cd_kustomize_overrides_and_patches()
    {
        var app = GitOpsApplication.Parse("""
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            metadata: {name: billing}
            spec:
              source:
                path: overlays/prod
                kustomize:
                  namePrefix: prod-
                  nameSuffix: -eu
                  namespace: billing
                  images: [billing=registry/billing:2.0]
                  commonLabels: {team: payments}
                  commonAnnotations: {owner: someone}
                  patches:
                    - target: {kind: ConfigMap, name: billing-config}
                      patch: |
                        apiVersion: v1
                        kind: ConfigMap
                        metadata:
                          name: billing-config
                        data:
                          Currency: EUR
                          Removed: null
                    - patch: |
                        kind: ConfigMap
                        metadata: {name: other}
                        data: {A: "1"}
                    - target: {kind: ConfigMap, name: billing-config}
                      patch: |
                        - op: replace
                          path: /data/Retry~1Count
                          value: 5
                        - op: remove
                          path: /data/Old~0Key
                        - op: test
                          path: /data/Skipped
                        - op: add
                          path: /data/
                        - op: add
                          path: /spec/x
                    - target: {kind: Deployment, name: billing}
                      patch: |
                        - op: replace
                          path: /data/x
                          value: 1
                    - path: patches/env.yaml
                      target: {kind: Deployment}
                    - patch: '[unclosed'
            """);

        var kustomize = Assert.IsType<GitOpsKustomize>(app.Kustomize);
        Assert.Equal("overlays/prod", app.Path);
        Assert.Empty(app.HelmSources);
        Assert.Equal(("prod-", "-eu", "billing"), (kustomize.NamePrefix, kustomize.NameSuffix, kustomize.Namespace));
        Assert.Equal(["billing=registry/billing:2.0"], kustomize.Images);
        Assert.Equal("payments", kustomize.CommonLabels["team"]);
        Assert.Equal("someone", kustomize.CommonAnnotations["owner"]);
        Assert.Equal(6, kustomize.Patches.Count);
        Assert.Equal(
            [
                new GitOpsPatchValue("billing-config", "Currency", "EUR", 22, false),
                new GitOpsPatchValue("billing-config", "Removed", null, 23, true),
            ],
            kustomize.Patches[0].Values);
        Assert.Equal(("ConfigMap", "billing-config", 15), (kustomize.Patches[0].TargetKind, kustomize.Patches[0].TargetName, kustomize.Patches[0].Line));
        Assert.Equal([new GitOpsPatchValue("other", "A", "1", 27, false)], kustomize.Patches[1].Values);
        Assert.Equal(
            [
                new GitOpsPatchValue("billing-config", "Retry/Count", "5", 30, false),
                new GitOpsPatchValue("billing-config", "Old~Key", null, 33, true),
            ],
            kustomize.Patches[2].Values);
        Assert.Empty(kustomize.Patches[3].Values);
        Assert.Equal("patches/env.yaml", kustomize.Patches[4].File);
        Assert.Empty(kustomize.Patches[5].Values);
        Assert.Equal(
            ["kustomize namePrefix 'prod-'; nameSuffix '-eu'; namespace 'billing'; images billing=registry/billing:2.0; commonLabels team=payments; commonAnnotations owner; 3 patches that do not set ConfigMap data are applied by Argo CD on top of the manifests in Git"],
            app.Notes);
    }

    [Fact]
    public void Labels_whose_keys_differ_only_by_a_tag_keep_the_last_one()
    {
        // YAML holds a and !x a as two keys, but both are the label a.
        var app = GitOpsApplication.Parse("""
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            metadata: {name: a}
            spec:
              source:
                kustomize:
                  commonLabels: {a: "1", !x a: "2"}
                  commonAnnotations: {b: "1", !x b: "2"}
            """);

        Assert.Equal("2", app.Kustomize!.CommonLabels["a"]);
        Assert.Equal("2", app.Kustomize.CommonAnnotations["b"]);
    }

    [Fact]
    public void A_single_untraced_patch_is_named_once_and_empty_overrides_add_no_note()
    {
        var one = GitOpsApplication.Parse("""
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            metadata: {name: a}
            spec:
              source:
                kustomize:
                  patches:
                    - path: p.yaml
            """);
        Assert.Equal(["kustomize 1 patch that does not set ConfigMap data are applied by Argo CD on top of the manifests in Git"], one.Notes);

        var none = GitOpsApplication.Parse("""
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            metadata: {name: a}
            spec:
              source:
                kustomize: {}
            """);
        Assert.Empty(none.Notes);
        Assert.NotNull(none.Kustomize);
    }

    [Fact]
    public void Skips_other_objects_and_keeps_config_maps_and_secrets_as_data()
    {
        var all = GitOpsApplication.ParseAll("""
            apiVersion: v1
            kind: List
            items:
              - apiVersion: argoproj.io/v1alpha1
                kind: Application
                metadata: {name: a}
              - apiVersion: v1
                kind: ConfigMap
                metadata: {name: cm}
                data: {k: v}
            ---
            apiVersion: example.test/v1
            kind: Application
            metadata: {name: not-argo}
            ---
            apiVersion: helm.fluxcd.io/v1
            kind: HelmRelease
            metadata: {name: flux-v1}
            ---
            apiVersion: helm.toolkit.fluxcd.io/v2
            kind: Kustomization
            metadata: {name: wrong-group}
            ---
            apiVersion: kustomize.toolkit.fluxcd.io/v1
            kind: HelmRelease
            metadata: {name: wrong-group-too}
            ---
            apiVersion: v1
            kind: Secret
            metadata: {name: s}
            stringData: {p: secret}
            ---
            kind: ApplicationSet
            metadata: {name: no-api-version}
            ---
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            """, "many.yaml");

        Assert.Equal(["a", "(unnamed)"], all.Select(a => a.Name));
        Assert.Equal("v", all[0].Data.ConfigMaps["cm"].Data["k"].Value);
        Assert.Equal("secret", all[0].Data.Secrets["s"].Data["p"].Value);
        Assert.Same(all[0].Data, all[1].Data);
        Assert.Equal("many.yaml", all[0].Data.ConfigMaps["cm"].SourceName);
    }

    [Fact]
    public void Parse_picks_the_object_by_name_and_explains_what_is_there()
    {
        const string two = """
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            metadata: {name: a}
            ---
            apiVersion: kustomize.toolkit.fluxcd.io/v1
            kind: Kustomization
            metadata: {name: b}
            """;

        Assert.Equal(GitOpsKind.FluxKustomization, GitOpsApplication.Parse(two, "two.yaml", "b").Kind);
        Assert.Equal(GitOpsKind.ArgoCdApplication, GitOpsApplication.Parse(two, "two.yaml", "a").Kind);
        Assert.Throws<InvalidOperationException>(() => GitOpsApplication.Parse(two, "two.yaml", " "));
        var several = Assert.Throws<InvalidOperationException>(() => GitOpsApplication.Parse(two, "two.yaml"));
        Assert.Equal("two.yaml has 2 GitOps objects (Argo CD Application a, Flux Kustomization b); pass the name of the one to trace.", several.Message);
        var missing = Assert.Throws<InvalidOperationException>(() => GitOpsApplication.Parse(two, "two.yaml", "c"));
        Assert.Equal("two.yaml has no GitOps object named 'c'. It has: Argo CD Application a, Flux Kustomization b.", missing.Message);
        var none = Assert.Throws<InvalidOperationException>(() => GitOpsApplication.Parse("kind: ConfigMap", "cm.yaml"));
        Assert.Equal("cm.yaml has no Argo CD Application or Flux HelmRelease or Kustomization.", none.Message);
        var noneNamed = Assert.Throws<InvalidOperationException>(() => GitOpsApplication.Parse("kind: ConfigMap", "cm.yaml", "x"));
        Assert.Equal("cm.yaml has no GitOps object named 'x'. It has: none.", noneNamed.Message);
    }

    [Fact]
    public void Pick_compares_names_exactly()
    {
        var apps = GitOpsApplication.ParseAll("""
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            metadata: {name: Orders}
            """);
        Assert.Throws<InvalidOperationException>(() => GitOpsApplication.Pick(apps, "f", "orders"));
        Assert.Throws<InvalidOperationException>(() => GitOpsApplication.Pick(apps, "f", null, a => a.Kind == GitOpsKind.FluxHelmRelease));
        Assert.Same(apps[0], GitOpsApplication.Pick(apps, "f", null, a => a.Kind == GitOpsKind.ArgoCdApplication));
    }

    [Fact]
    public void Invalid_yaml_is_a_format_exception_naming_the_source()
    {
        var ex = Assert.Throws<FormatException>(() => GitOpsApplication.ParseAll("a: [", "broken.yaml"));
        Assert.StartsWith("Could not parse GitOps manifests in broken.yaml: ", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => GitOpsApplication.ParseAll(null!));
        Assert.Throws<ArgumentException>(() => GitOpsApplication.ParseAll("", " "));
        Assert.Throws<ArgumentException>(() => GitOpsApplication.Load(" "));
    }

    [Fact]
    public void Load_names_the_source_after_the_file()
    {
        var app = GitOpsApplication.Load(Path.Join(SourceGoldenTests.Fixture("argocd"), "repo", "apps", "orders-prod.yaml"));
        Assert.Equal("orders-prod.yaml", app.SourceName);
        Assert.Equal("charts/orders", app.Path);
        Assert.Equal(
            ["values-prod.yaml", "$values/envs/prod/orders.yaml"],
            app.HelmSources[0].ValuesFiles.Select(f => f.Path));

        var named = GitOpsApplication.Load(Path.Join(SourceGoldenTests.Fixture("flux"), "repo", "clusters", "prod", "billing.yaml"), "billing");
        Assert.Equal("billing", named.Name);
    }
}
