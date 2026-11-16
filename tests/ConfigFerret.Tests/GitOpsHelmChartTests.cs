using ConfigFerret.GitOps;
using ConfigFerret.Helm;
using ConfigFerret.Kubernetes;

namespace ConfigFerret.Tests;

public sealed class GitOpsHelmChartTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cf-gitops-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Join(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static GitOpsApplication Argo(string helm, string source = "path: chart") => GitOpsApplication.Parse($"""
        apiVersion: argoproj.io/v1alpha1
        kind: Application
        metadata: {"{"}name: app{"}"}
        spec:
          source:
            {source}
            helm:
        {helm}
        """, "app.yaml");

    private void WriteChart(string folder = "chart")
    {
        Write($"{folder}/Chart.yaml", "name: c\n");
        Write($"{folder}/values.yaml", "db:\n  timeout: 30\n  host: default\n");
        Write($"{folder}/templates/cm.yaml", "data:\n  Db__Timeout: {{ .Values.db.timeout }}\n");
    }

    [Fact]
    public void Merges_argo_cd_layers_in_order_and_names_them_after_the_application()
    {
        WriteChart();
        Write("chart/values-prod.yaml", "db:\n  timeout: 40\n");
        var app = Argo("""
                  valueFiles: [values-prod.yaml]
                  values: |
                    db:
                      timeout: 45
                  parameters:
                    - name: db.timeout
                      value: "50"
            """);

        var chart = HelmChart.ForGitOps(app, new GitOpsOptions { RepositoryRoot = _root });

        Assert.Empty(chart.Warnings);
        Assert.Equal(Path.Join(_root, "chart"), chart.ChartDirectory);
        Assert.Equal(
            [
                new HelmValueStep("values.yaml (chart default)", 2, "30", false),
                new HelmValueStep("values-prod.yaml", 2, "40", false),
                new HelmValueStep("Argo CD Application app (helm.values)", 11, "45", false),
                new HelmValueStep("Argo CD Application app (helm.parameters)", 13, "50", false),
            ],
            chart.History["db.timeout"]);
        Assert.Equal("db.timeout", Assert.Single(chart.FindUses("Db__Timeout")).ValuesPath);
        Assert.Equal(LayerKind.GitOps, chart.KindOf("Argo CD Application app (helm.values)"));
        Assert.Equal(LayerKind.Helm, chart.KindOf("values-prod.yaml"));
        Assert.Equal(LayerKind.Helm, chart.KindOf("values.yaml (chart default)"));
        Assert.True(chart.IsGitOpsSource("values-prod.yaml"));
        Assert.False(chart.IsGitOpsSource("values.yaml (chart default)"));
        Assert.False(chart.IsSensitiveSource("Argo CD Application app (helm.values)"));
        Assert.False(chart.IsSensitivePath("db.timeout"));
        Assert.False(chart.IsSensitivePath("nope"));
    }

    [Fact]
    public void Parameters_set_lists_remove_with_null_and_keep_forced_strings()
    {
        var app = Argo("""
                  values: "a: {b: 1}"
                  parameters:
                    - name: list
                      value: "{x,y}"
                    - name: a
                      value: "null"
                    - name: forced
                      value: "null"
                      forceString: true
                    - name: raw
                      value: "{x,y}"
                      forceString: true
            """);

        var chart = HelmChart.ForGitOps(app);

        Assert.Equal("[x, y]", chart.GetValue("list"));
        Assert.Null(chart.GetValue("a.b"));
        Assert.True(chart.History["a.b"][^1].IsRemoval);
        Assert.Equal("Argo CD Application app (helm.parameters)", chart.History["a.b"][^1].Source);
        Assert.Equal("null", chart.GetValue("forced"));
        Assert.Equal("{x,y}", chart.GetValue("raw"));
    }

    [Fact]
    public void Warns_about_charts_it_cannot_read()
    {
        var remote = HelmChart.ForGitOps(Argo("      values: 'a: 1'", "chart: orders\n    repoURL: https://charts.example.test"));
        Assert.Equal(
            ["Argo CD Application app deploys chart orders from https://charts.example.test, which ConfigFerret cannot fetch. Pull it (helm pull --untar) and pass its folder as the chart folder to trace the chart's templates and defaults."],
            remote.Warnings);
        Assert.Null(remote.ChartDirectory);
        Assert.Equal("1", remote.GetValue("a"));

        var noRepo = HelmChart.ForGitOps(Argo("      values: 'a: 1'", "chart: orders"));
        Assert.Contains("from a remote repository, which", noRepo.Warnings[0], StringComparison.Ordinal);

        var noRoot = HelmChart.ForGitOps(Argo("      releaseName: x"));
        Assert.Equal(["Argo CD Application app deploys the chart in chart; pass the repository checkout or the chart folder to trace the chart's templates and defaults."], noRoot.Warnings);

        var missing = HelmChart.ForGitOps(Argo("      releaseName: x"), new GitOpsOptions { RepositoryRoot = _root });
        Assert.Equal([$"The chart folder {Path.Join(_root, "chart")} for Argo CD Application app does not exist."], missing.Warnings);
        Assert.Null(missing.ChartDirectory);
    }

    [Fact]
    public void A_source_path_that_leads_outside_the_repository_is_not_read()
    {
        WriteChart("outside");
        var repo = Directory.CreateDirectory(Path.Join(_root, "repo")).FullName;

        var chart = HelmChart.ForGitOps(Argo("      values: 'a: 1'", "path: ../outside"), new GitOpsOptions { RepositoryRoot = repo });

        Assert.Equal(["The chart folder ../outside for Argo CD Application app is outside the repository checkout, so it was not read."], chart.Warnings);
        Assert.Null(chart.ChartDirectory);
        Assert.Null(chart.GetValue("db.timeout"));
        Assert.Empty(chart.FindUses("Db__Timeout"));
        Assert.Equal("1", chart.GetValue("a"));
    }

    [Fact]
    public void A_chart_at_the_repository_root_is_read()
    {
        WriteChart(".");

        var chart = HelmChart.ForGitOps(Argo("      releaseName: x", "path: ."), new GitOpsOptions { RepositoryRoot = _root });

        Assert.Empty(chart.Warnings);
        Assert.Equal("30", chart.GetValue("db.timeout"));
        Assert.Single(chart.FindUses("Db__Timeout"));
    }

    [Fact]
    public void Chart_files_that_link_outside_the_repository_are_not_read()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        WriteChart("outside");
        Write("repo/chart/Chart.yaml", "name: c\n");
        var repo = Path.Join(_root, "repo");
        Directory.CreateSymbolicLink(Path.Join(repo, "linked"), Path.Join(_root, "outside"));
        File.CreateSymbolicLink(Path.Join(repo, "chart", "values.yaml"), Path.Join(_root, "outside", "values.yaml"));
        Directory.CreateSymbolicLink(Path.Join(repo, "chart", "templates"), Path.Join(_root, "outside", "templates"));
        var options = new GitOpsOptions { RepositoryRoot = repo };

        var linked = HelmChart.ForGitOps(Argo("      releaseName: x", "path: linked"), options);
        Assert.Equal(["The chart folder linked for Argo CD Application app is outside the repository checkout, so it was not read."], linked.Warnings);
        Assert.Null(linked.ChartDirectory);
        Assert.Empty(linked.History);

        var chart = HelmChart.ForGitOps(Argo("      releaseName: x"), options);
        Assert.Equal(
            [
                $"The chart's values.yaml for Argo CD Application app is outside {repo}, so it was not read.",
                $"The chart's templates folder for Argo CD Application app is outside {repo}, so it was not read.",
            ],
            chart.Warnings);
        Assert.Empty(chart.History);
        Assert.Empty(chart.FindUses("Db__Timeout"));
    }

    [Fact]
    public void Chart_files_that_link_outside_give_the_same_warning_whether_or_not_their_target_exists()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        // Whether the warning appears must not tell whether a path outside the checkout exists.
        WriteChart("outside");
        var repo = Path.Join(_root, "repo");
        foreach (var (folder, values, templates) in new[] { ("found", "values.yaml", "templates"), ("missing", "missing.yaml", "missing") })
        {
            Write($"repo/{folder}/Chart.yaml", "name: c\n");
            File.CreateSymbolicLink(Path.Join(repo, folder, "values.yaml"), Path.Join(_root, "outside", values));
            Directory.CreateSymbolicLink(Path.Join(repo, folder, "templates"), Path.Join(_root, "outside", templates));
        }

        var options = new GitOpsOptions { RepositoryRoot = repo };
        var found = HelmChart.ForGitOps(Argo("      releaseName: x", "path: found"), options);
        var missing = HelmChart.ForGitOps(Argo("      releaseName: x", "path: missing"), options);

        Assert.Equal(
            [
                $"The chart's values.yaml for Argo CD Application app is outside {repo}, so it was not read.",
                $"The chart's templates folder for Argo CD Application app is outside {repo}, so it was not read.",
            ],
            found.Warnings);
        Assert.Equal(found.Warnings, missing.Warnings);
    }

    [Fact]
    public void A_source_path_that_is_not_a_path_is_outside_the_repository()
    {
        var chart = HelmChart.ForGitOps(Argo("      releaseName: x", "path: \"chart\\0\""), new GitOpsOptions { RepositoryRoot = _root });

        Assert.Equal(["The chart folder chart\0 for Argo CD Application app is outside the repository checkout, so it was not read."], chart.Warnings);
        Assert.Null(chart.ChartDirectory);
    }

    [Fact]
    public void A_source_path_that_links_to_the_repository_root_is_read()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        WriteChart(".");
        Directory.CreateSymbolicLink(Path.Join(_root, "linked"), ".");

        var chart = HelmChart.ForGitOps(Argo("      releaseName: x", "path: linked"), new GitOpsOptions { RepositoryRoot = _root });

        Assert.Empty(chart.Warnings);
        Assert.Equal("30", chart.GetValue("db.timeout"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void A_blank_repository_root_is_no_repository_root(string root)
    {
        WriteChart();
        Write("chart/values-prod.yaml", "db:\n  timeout: 40\n");
        var app = Argo("""
                  valueFiles: [$values/envs/prod.yaml, values-prod.yaml]
            """);

        var chart = HelmChart.ForGitOps(app, new GitOpsOptions { ChartFolder = Path.Join(_root, "chart"), RepositoryRoot = root });

        Assert.Equal(["Argo CD Application app (line 8) names the values file $values/envs/prod.yaml, which was not found; pass the repository checkout to read it."], chart.Warnings);
        Assert.Equal("40", chart.GetValue("db.timeout"));
    }

    [Fact]
    public void A_chart_folder_wins_over_the_repository_and_works_for_remote_charts()
    {
        WriteChart("pulled");
        var chart = HelmChart.ForGitOps(Argo("      releaseName: x", "chart: orders"), new GitOpsOptions { ChartFolder = Path.Join(_root, "pulled"), RepositoryRoot = "/nonexistent" });
        Assert.Empty(chart.Warnings);
        Assert.Equal("30", chart.GetValue("db.timeout"));
        Assert.Single(chart.FindUses("Db__Timeout"));

        var blank = HelmChart.ForGitOps(Argo("      releaseName: x"), new GitOpsOptions { ChartFolder = " ", RepositoryRoot = _root });
        Assert.Single(blank.Warnings);
    }

    [Fact]
    public void Resolves_values_files_and_warns_about_the_ones_it_cannot_read()
    {
        WriteChart();
        Write("envs/prod.yaml", "db:\n  host: from-ref\n");
        Write("shared.yaml", "db:\n  host: from-root\n");
        Write("chart/broken.yaml", "a: [");
        Write("outside.yaml", "a: 1\n");
        var app = Argo("""
                  valueFiles:
                    - https://example.test/values.yaml
                    - HTTP://example.test/values.yaml
                    - $values/envs/prod.yaml
                    - shared.yaml
                    - missing.yaml
                    - $values
                    - ../outside.yaml
                    - broken.yaml
            """);

        var chart = HelmChart.ForGitOps(app, new GitOpsOptions { RepositoryRoot = Path.Join(_root, "chart", "..") });

        Assert.Equal(["values.yaml (chart default)", "$values/envs/prod.yaml", "shared.yaml"], chart.History["db.host"].Select(h => h.Source));
        Assert.Equal(
            [
                "Argo CD Application app (line 9) names the remote values file https://example.test/values.yaml, which ConfigFerret does not fetch.",
                "Argo CD Application app (line 10) names the remote values file HTTP://example.test/values.yaml, which ConfigFerret does not fetch.",
                $"Argo CD Application app (line 13) names the values file missing.yaml, which was not found under {Path.Join(_root, "chart")} or {Path.Join(_root, "chart", "..")}.",
                $"Argo CD Application app (line 14) names the values file $values, which was not found under {Path.Join(_root, "chart", "..")}.",
            ],
            chart.Warnings.Take(4));
        Assert.StartsWith("Could not parse Helm values in broken.yaml: ", chart.Warnings[4], StringComparison.Ordinal);
        Assert.Equal(5, chart.Warnings.Count);
        Assert.Equal(["../outside.yaml"], chart.History["a"].Select(h => h.Source));
    }

    [Fact]
    public void Reads_a_values_file_outside_the_chart_folder_that_is_inside_the_repository()
    {
        WriteChart("repo/charts/orders");
        Write("repo/envs/prod/values.yaml", "db:\n  timeout: 99\n");
        Write("outside.yaml", "db:\n  host: outside\n");
        var repo = Path.Join(_root, "repo");
        var app = Argo("""
                  valueFiles: [../../envs/prod/values.yaml, ../../../outside.yaml]
            """, "path: charts/orders");

        var chart = HelmChart.ForGitOps(app, new GitOpsOptions { RepositoryRoot = repo });

        Assert.Equal(["values.yaml (chart default)", "../../envs/prod/values.yaml"], chart.History["db.timeout"].Select(h => h.Source));
        Assert.Equal("99", chart.GetValue("db.timeout"));
        Assert.Equal("default", chart.GetValue("db.host"));
        Assert.Equal(
            [$"Argo CD Application app (line 8) names the values file ../../../outside.yaml, which is outside {Path.Join(repo, "charts", "orders")} and {repo}, so it was not read."],
            chart.Warnings);
    }

    [Fact]
    public void A_values_file_that_links_outside_the_repository_is_not_read()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        WriteChart("repo/chart");
        Write("outside.yaml", "db:\n  host: outside\n");
        var repo = Path.Join(_root, "repo");
        File.CreateSymbolicLink(Path.Join(repo, "envs.yaml"), Path.Join(_root, "outside.yaml"));
        var app = Argo("""
                  valueFiles: [../envs.yaml]
            """);

        var chart = HelmChart.ForGitOps(app, new GitOpsOptions { RepositoryRoot = repo });

        Assert.Equal("default", chart.GetValue("db.host"));
        Assert.Equal(
            [$"Argo CD Application app (line 8) names the values file ../envs.yaml, which is outside {Path.Join(repo, "chart")} and {repo}, so it was not read."],
            chart.Warnings);
    }

    [Fact]
    public void A_values_file_outside_the_repository_gives_the_same_warning_whether_or_not_it_exists()
    {
        WriteChart("repo/chart");
        Write("outside.yaml", "db:\n  host: outside\n");
        var repo = Path.Join(_root, "repo");
        var app = Argo("""
                  valueFiles: [../../outside.yaml, ../../missing.yaml, "bad\0.yaml"]
            """);

        var chart = HelmChart.ForGitOps(app, new GitOpsOptions { RepositoryRoot = repo });

        Assert.Equal(
            [
                $"Argo CD Application app (line 8) names the values file ../../outside.yaml, which is outside {Path.Join(repo, "chart")} and {repo}, so it was not read.",
                $"Argo CD Application app (line 8) names the values file ../../missing.yaml, which is outside {Path.Join(repo, "chart")} and {repo}, so it was not read.",
                $"Argo CD Application app (line 8) names the values file bad\0.yaml, which is outside {Path.Join(repo, "chart")} and {repo}, so it was not read.",
            ],
            chart.Warnings);
    }

    [Fact]
    public void A_values_file_link_outside_the_repository_gives_the_same_warning_whether_or_not_its_target_exists()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs extra rights on Windows.
        }

        WriteChart("repo/chart");
        Write("outside.yaml", "db:\n  host: outside\n");
        var repo = Path.Join(_root, "repo");
        File.CreateSymbolicLink(Path.Join(repo, "chart", "found.yaml"), Path.Join(_root, "outside.yaml"));
        File.CreateSymbolicLink(Path.Join(repo, "chart", "missing.yaml"), Path.Join(_root, "missing.yaml"));
        var app = Argo("""
                  valueFiles: [found.yaml, missing.yaml]
            """);

        var chart = HelmChart.ForGitOps(app, new GitOpsOptions { RepositoryRoot = repo });

        Assert.Equal(
            [
                $"Argo CD Application app (line 8) names the values file found.yaml, which is outside {Path.Join(repo, "chart")} and {repo}, so it was not read.",
                $"Argo CD Application app (line 8) names the values file missing.yaml, which is outside {Path.Join(repo, "chart")} and {repo}, so it was not read.",
            ],
            chart.Warnings);
    }

    [Fact]
    public void A_ref_values_file_needs_the_repository_and_missing_files_can_be_ignored()
    {
        WriteChart();
        var app = Argo("""
                  valueFiles: [$values/envs/prod.yaml]
            """);
        var chart = HelmChart.ForGitOps(app, new GitOpsOptions { ChartFolder = Path.Join(_root, "chart") });
        Assert.Equal(["Argo CD Application app (line 8) names the values file $values/envs/prod.yaml, which was not found; pass the repository checkout to read it."], chart.Warnings);

        var ignoring = Argo("""
                  ignoreMissingValueFiles: true
                  valueFiles: [missing.yaml]
            """);
        Assert.Empty(HelmChart.ForGitOps(ignoring, new GitOpsOptions { ChartFolder = Path.Join(_root, "chart") }).Warnings);
    }

    [Fact]
    public void Flux_values_files_replace_the_chart_values_unless_they_list_them()
    {
        WriteChart();
        Write("chart/values-prod.yaml", "db:\n  timeout: 40\n");
        var release = Flux("      valuesFiles: [values-prod.yaml]");
        var chart = HelmChart.ForGitOps(release, new GitOpsOptions { ChartFolder = Path.Join(_root, "chart") });
        Assert.Equal(["values-prod.yaml"], chart.History["db.timeout"].Select(h => h.Source));
        Assert.False(chart.History.ContainsKey("db.host"));

        var withDefaults = HelmChart.ForGitOps(Flux("      valuesFiles: [values.yaml, values-prod.yaml]"), new GitOpsOptions { ChartFolder = Path.Join(_root, "chart") });
        Assert.Equal(["values.yaml", "values-prod.yaml"], withDefaults.History["db.timeout"].Select(h => h.Source));

        var noFiles = HelmChart.ForGitOps(Flux("      version: 1.0.0"), new GitOpsOptions { ChartFolder = Path.Join(_root, "chart") });
        Assert.Equal(["values.yaml (chart default)"], noFiles.History["db.timeout"].Select(h => h.Source));
    }

    private static GitOpsApplication Flux(string chartSpec, string spec = "", string data = "") => GitOpsApplication.Parse($"""
        apiVersion: helm.toolkit.fluxcd.io/v2
        kind: HelmRelease
        metadata: {"{"}name: rel{"}"}
        spec:
          chart:
            spec:
              chart: orders
              sourceRef: {"{"}kind: HelmRepository, name: repo{"}"}
        {chartSpec}
        {spec}
        {data}
        """, "rel.yaml");

    [Fact]
    public void Reads_values_from_config_maps_and_secrets_in_order()
    {
        var release = Flux(
            "      version: 1.0.0",
            """
              valuesFrom:
                - kind: ConfigMap
                  name: vals
                - kind: Secret
                  name: creds
                  valuesKey: password
                  targetPath: db.password
                - kind: ConfigMap
                  name: elsewhere
                  valuesKey: timeout
                  targetPath: db.timeout
              values:
                db: {timeout: 99}
            ---
            apiVersion: v1
            kind: ConfigMap
            metadata: {name: vals}
            data:
              values.yaml: |
                db:
                  timeout: 60
            ---
            apiVersion: v1
            kind: Secret
            metadata: {name: creds}
            stringData: {password: hunter2}
            """);
        var extra = KubernetesManifests.Parse("""
            apiVersion: v1
            kind: ConfigMap
            metadata: {name: elsewhere}
            data: {timeout: "null"}
            """, "extra.yaml");

        var chart = HelmChart.ForGitOps(release, new GitOpsOptions { Data = extra });

        Assert.Equal(
            [
                new HelmValueStep("Flux HelmRelease rel (valuesFrom ConfigMap vals, key values.yaml)", 30, "60", false),
                new HelmValueStep("Flux HelmRelease rel (valuesFrom ConfigMap elsewhere, key timeout)", 4, "null", false),
                new HelmValueStep("Flux HelmRelease rel (spec.values)", 22, "99", false),
            ],
            chart.History["db.timeout"]);
        Assert.Equal("hunter2", chart.GetValue("db.password"));
        Assert.True(chart.IsSensitiveSource("Flux HelmRelease rel (valuesFrom Secret creds, key password)"));
        Assert.True(chart.IsSensitivePath("db.password"));
        Assert.False(chart.IsSensitivePath("db.timeout"));
        Assert.Equal(LayerKind.GitOps, chart.KindOf("Flux HelmRelease rel (spec.values)"));
        Assert.Single(chart.Warnings);
        Assert.StartsWith("Flux HelmRelease rel deploys chart orders from HelmRepository/repo", chart.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Warns_about_values_from_objects_and_keys_it_cannot_find_unless_optional()
    {
        var release = Flux(
            "      version: 1.0.0",
            """
              valuesFrom:
                - {kind: ConfigMap, name: missing}
                - {kind: Secret, name: missing-optional, optional: true}
                - {kind: ConfigMap, name: vals, valuesKey: nope}
                - {kind: ConfigMap, name: vals, valuesKey: nope-optional, optional: true}
                - {kind: ConfigMap, name: vals, valuesKey: broken}
                - {kind: Secret, name: vals}
            ---
            apiVersion: v1
            kind: ConfigMap
            metadata: {name: vals}
            data:
              broken: "a: ["
            binaryData:
              values.yaml: AAAA
            """);

        var warnings = HelmChart.ForGitOps(release, new GitOpsOptions { ChartFolder = _root }).Warnings;

        Assert.Equal(
            [
                "Flux HelmRelease rel reads valuesFrom ConfigMap missing, which was not loaded; pass it with the GitOps data manifests.",
                "Flux HelmRelease rel reads key nope of valuesFrom ConfigMap vals, which has no such key with a text value.",
                "Flux HelmRelease rel reads valuesFrom Secret vals, which was not loaded; pass it with the GitOps data manifests.",
            ],
            warnings.Where(w => !w.StartsWith("Could not parse", StringComparison.Ordinal)));
        Assert.Contains(warnings, w => w.StartsWith("Could not parse Helm values in Flux HelmRelease rel (valuesFrom ConfigMap vals, key broken): ", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains("values.yaml", StringComparison.Ordinal));
    }

    [Fact]
    public void Binary_values_from_keys_are_reported_as_missing()
    {
        var release = Flux(
            "      version: 1.0.0",
            """
              valuesFrom:
                - {kind: ConfigMap, name: vals}
            ---
            apiVersion: v1
            kind: ConfigMap
            metadata: {name: vals}
            binaryData:
              values.yaml: AAAA
            """);

        Assert.Contains(
            "Flux HelmRelease rel reads key values.yaml of valuesFrom ConfigMap vals, which has no such key with a text value.",
            HelmChart.ForGitOps(release, new GitOpsOptions { ChartFolder = _root }).Warnings);
    }

    [Fact]
    public void Invalid_inline_values_become_a_warning()
    {
        var chart = HelmChart.ForGitOps(Argo("      values: 'a: ['"), new GitOpsOptions { ChartFolder = _root });
        Assert.StartsWith("Could not parse Helm values in Argo CD Application app (helm.values): ", Assert.Single(chart.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_first_of_several_helm_sources_is_traced()
    {
        var app = GitOpsApplication.Parse("""
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            metadata: {name: multi}
            spec:
              sources:
                - chart: first
                  helm: {values: 'a: 1'}
                - chart: second
                  helm: {values: 'a: 2'}
            """);

        var chart = HelmChart.ForGitOps(app, new GitOpsOptions { ChartFolder = _root });

        Assert.Equal("Argo CD Application multi has 2 Helm sources; only the first (first) is traced.", chart.Warnings[0]);
        Assert.Equal("1", chart.GetValue("a"));
    }

    [Fact]
    public void An_application_without_a_helm_chart_is_refused()
    {
        var kustomization = GitOpsApplication.Parse("""
            apiVersion: kustomize.toolkit.fluxcd.io/v1
            kind: Kustomization
            metadata: {name: ks}
            spec: {path: ./apps}
            """);
        var ex = Assert.Throws<ArgumentException>(() => HelmChart.ForGitOps(kustomization));
        Assert.StartsWith("Flux Kustomization ks does not deploy a Helm chart.", ex.Message, StringComparison.Ordinal);

        var kustomize = GitOpsApplication.Parse("""
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            metadata: {name: k}
            spec:
              source: {path: overlays, kustomize: {}}
            """);
        Assert.Throws<ArgumentException>(() => HelmChart.ForGitOps(kustomize));

        var noPath = GitOpsApplication.Parse("""
            apiVersion: argoproj.io/v1alpha1
            kind: ApplicationSet
            metadata: {name: k}
            spec: {template: {spec: {}}}
            """);
        Assert.Throws<ArgumentException>(() => HelmChart.ForGitOps(noPath));
        Assert.Throws<ArgumentNullException>(() => HelmChart.ForGitOps(null!));
    }

    [Fact]
    public void A_plain_argo_cd_path_is_read_as_a_chart()
    {
        WriteChart();
        var app = GitOpsApplication.Parse("""
            apiVersion: argoproj.io/v1alpha1
            kind: ApplicationSet
            metadata: {name: set}
            spec:
              template:
                spec:
                  source: {path: chart}
            """);

        var chart = HelmChart.ForGitOps(app, new GitOpsOptions { RepositoryRoot = _root });

        Assert.Empty(chart.Warnings);
        Assert.Equal("30", chart.GetValue("db.timeout"));
    }
}
