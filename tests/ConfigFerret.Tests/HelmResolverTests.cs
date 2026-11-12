using ConfigFerret.Helm;

namespace ConfigFerret.Tests;

public class HelmResolverTests
{
    private static readonly HelmChart Chart = HelmChart.FromText(
        [("values.yaml", "a: 1\nb: 2\nc: 5\n"), ("prod.yaml", "c: 6\n")],
        [("templates/cm.yaml", """
            data:
              KEY: {{ .Values.a }}
              KEY: {{ .Values.b }}
            """)]);

    private static ProvenanceStep ConfigMapStep(string key, string? value) => new()
    {
        Kind = LayerKind.KubernetesConfigMap,
        Source = "ConfigMap cm",
        Value = value,
        Reference = new ConfigMapKeyRef("cm", key),
    };

    private static IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep step) =>
        new HelmResolver(Chart).Resolve(step, new TraceDiagnostics());

    [Fact]
    public void Picks_the_template_line_whose_value_matches_the_rendered_value()
    {
        var step = Assert.Single(Resolve(ConfigMapStep("KEY", "2")));

        Assert.Equal(LayerKind.Helm, step.Kind);
        Assert.Equal("Helm template templates/cm.yaml", step.Source);
        Assert.Equal("line 3", step.Location);
        Assert.Equal("2", step.Value);
        Assert.True(step.IsInferred);
        Assert.Equal("renders .Values.b", step.Note);
        Assert.Equal(new HelmValueRef("b"), step.Reference);
    }

    [Fact]
    public void Falls_back_to_the_first_use_and_notes_a_mismatch()
    {
        var step = Assert.Single(Resolve(ConfigMapStep("KEY", "9")));

        Assert.Equal(new HelmValueRef("a"), step.Reference);
        Assert.Equal("renders .Values.a; the values give '1' but the manifest has '9', so the release may be out of date or the template transforms it", step.Note);
    }

    [Fact]
    public void Uses_the_first_use_without_a_mismatch_note_when_the_value_is_unknown()
    {
        var step = Assert.Single(Resolve(ConfigMapStep("KEY", null)));

        Assert.Equal(new HelmValueRef("a"), step.Reference);
        Assert.Equal("renders .Values.a", step.Note);
    }

    [Fact]
    public void Returns_nothing_for_names_no_template_uses()
    {
        Assert.Empty(Resolve(ConfigMapStep("OTHER", "1")));
        Assert.Empty(Resolve(new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "x", Reference = new FileRef("/x") }));
    }

    [Fact]
    public void Resolves_secret_and_workload_env_references_too()
    {
        var secret = Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesSecret, Source = "s", Value = "1", Reference = new SecretKeyRef("s", "KEY") });
        var env = Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesWorkload, Source = "w", Value = "2", Reference = new WorkloadEnvRef("w", "c", "KEY") });

        Assert.Equal(new HelmValueRef("a"), Assert.Single(secret).Reference);
        Assert.Equal(new HelmValueRef("b"), Assert.Single(env).Reference);
    }

    [Fact]
    public void Lists_every_values_file_that_set_a_path()
    {
        var steps = Resolve(new ProvenanceStep { Kind = LayerKind.Helm, Source = "t", Reference = new HelmValueRef("c") });

        Assert.Equal(Chart.History["c"].Select(h => h.Source), steps.Select(s => s.Source));
        Assert.Equal(["5", "6"], steps.Select(s => s.Value));
        Assert.Equal(["line 3, c", "line 1, c"], steps.Select(s => s.Location));
        Assert.All(steps, s => Assert.Equal(LayerKind.Helm, s.Kind));
    }

    [Fact]
    public void Uses_the_bare_path_as_location_for_set_values()
    {
        var chart = HelmChart.Load(TestPaths.Data("chart"), setValues: ["db.timeout=99"]);
        var steps = new HelmResolver(chart).Resolve(
            new ProvenanceStep { Kind = LayerKind.Helm, Source = "t", Reference = new HelmValueRef("db.timeout") }, new TraceDiagnostics());

        Assert.Equal("--set db.timeout", steps[^1].Source);
        Assert.Equal("db.timeout", steps[^1].Location);
        Assert.StartsWith("line ", steps[0].Location, StringComparison.Ordinal);
    }

    [Fact]
    public void Falls_back_to_the_template_default_when_no_values_file_sets_a_path()
    {
        var step = Assert.Single(Resolve(new ProvenanceStep { Kind = LayerKind.Helm, Source = "t", Reference = new HelmValueRef("missing.path") }));

        Assert.Equal("Helm template default", step.Source);
        Assert.Equal("missing.path", step.Location);
        Assert.Null(step.Value);
        Assert.Equal("no values file sets this path, so the template's own default (if any) is used", step.Note);
    }
}
