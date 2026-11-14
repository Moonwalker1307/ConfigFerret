using ConfigFerret.Desktop.Services;

namespace ConfigFerret.Desktop.Tests;

public class HelmValueUsageTests
{
    private static List<string> Dead(string template, string values, params string[] subcharts) =>
        HelmValueUsage.FindDead([template], HelmValueUsage.Leaves(values).Select(p => (p, "values.yaml")), subcharts).Select(d => d.Path).ToList();

    [Fact]
    public void Values_no_template_reads_are_dead()
    {
        var dead = Dead(
            "image: {{ .Values.image.repository }}:{{ $.Values.image.tag }}\n{{- with .Values.env }}{{ toYaml . }}{{ end }}",
            "image:\n  repository: x\n  tag: y\n  pullPolicy: Always\nenv:\n  A: 1\n  B: 2\nlegacy:\n  enabled: true\nglobal:\n  region: eu\nredis:\n  port: 1\n",
            "redis");

        Assert.Equal(["image.pullPolicy", "legacy.enabled"], dead);
    }

    [Fact]
    public void A_template_reading_a_value_below_a_list_or_with_index_counts()
    {
        var dead = Dead(
            """{{ index .Values "feature-flags" "beta" }} {{ index .Values.db "host" }} {{ range .Values.hosts }}{{ . }}{{ end }}""",
            "feature-flags:\n  beta: true\n  gamma: false\ndb:\n  host: h\nhosts: [a, b]\n");

        Assert.Equal(["feature-flags.gamma"], dead);
    }

    [Fact]
    public void A_bare_Values_reads_everything()
    {
        Assert.Empty(Dead("{{ toYaml .Values }}", "a: 1\nb:\n  c: 2\n"));
        Assert.Empty(Dead("{{ index .Values $key }}", "a: 1\n"));
    }

    [Fact]
    public void Reads_the_chart_and_its_values_files()
    {
        var dead = HelmValueUsage.FindDead(OrdersRepo.Full("deploy/chart"), [OrdersRepo.Full("deploy/values-prod.yaml")]);

        Assert.Equal(["features.recommendations"], dead.Select(d => d.Path).Distinct());
        Assert.Contains(dead, d => d.File == "values.yaml");
        Assert.Contains(dead, d => d.File == "values-prod.yaml");
    }
}
