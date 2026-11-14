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

    [Fact]
    public void Rejects_missing_arguments()
    {
        Assert.Throws<ArgumentException>(() => HelmValueUsage.FindDead(" ", []));
        Assert.Throws<ArgumentNullException>(() => HelmValueUsage.FindDead("chart", null!));
        Assert.Throws<ArgumentNullException>(() => HelmValueUsage.FindDead(null!, [], []));
        Assert.Throws<ArgumentNullException>(() => HelmValueUsage.FindDead([], null!, []));
        Assert.Throws<ArgumentNullException>(() => HelmValueUsage.FindDead([], [], null!));
    }

    [Fact]
    public void A_name_that_only_starts_like_another_is_not_related_to_it()
    {
        Assert.Equal(["logLevel"], Dead("{{ .Values.log }}", "log: 1\nlogLevel: debug\n"));
        Assert.Equal(["log"], Dead("{{ .Values.logLevel }}", "log: 1\nlogLevel: debug\n"));
        Assert.Equal(["b.c"], Dead("{{ .Values.a }}", "a: 1\nb:\n  c: 2\n"));
    }

    [Fact]
    public void A_chart_without_a_templates_folder_has_no_dead_values()
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "a: 1\n");
        temp.Write("notes.txt", "{{ .Values.b }}");

        Assert.Empty(HelmValueUsage.FindDead(temp.Path, []));
    }

    [Fact]
    public void Reads_every_file_under_templates_including_nested_ones()
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "a: 1\nb: 2\nc: 3\nd: 4\n");
        temp.Write("templates/deployment.yaml", "{{ .Values.a }}");
        temp.Write("templates/_helpers.tpl", "{{ .Values.b }}");
        temp.Write("templates/sub/deeper/cm.yaml", "{{ .Values.c }}");

        Assert.Equal(["d"], HelmValueUsage.FindDead(temp.Path, []).Select(d => d.Path));
    }

    [Fact]
    public void Reads_a_template_of_exactly_two_megabytes_and_skips_a_bigger_one()
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "a: 1\nb: 2\n");
        const string Exact = "{{ .Values.a }}";
        temp.Write("templates/exact.yaml", Exact + new string(' ', 2 * 1024 * 1024 - Exact.Length));
        temp.Write("templates/over.yaml", "{{ .Values.b }}" + new string(' ', 2 * 1024 * 1024));

        Assert.Equal(["b"], HelmValueUsage.FindDead(temp.Path, []).Select(d => d.Path));
    }

    [Fact]
    public void Does_not_follow_symbolic_links_in_templates()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var outside = new TempFolder();
        outside.Write("linked.yaml", "{{ .Values.a }}");
        using var temp = new TempFolder();
        temp.Write("values.yaml", "a: 1\n");
        temp.Write("templates/own.yaml", "kind: ConfigMap");
        Directory.CreateSymbolicLink(temp.Full("templates/link"), outside.Path);

        Assert.Equal(["a"], HelmValueUsage.FindDead(temp.Path, []).Select(d => d.Path));
    }

    [Fact]
    public void Values_for_subcharts_count_as_read()
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "postgres:\n  x: 1\nredis:\n  y: 1\nkafka:\n  z: 1\ncache:\n  w: 1\nglobal:\n  g: 1\ntemplates:\n  enabled: true\nother:\n  o: 1\n");
        temp.Write("templates/cm.yaml", "kind: ConfigMap");
        temp.Write("charts/postgres/Chart.yaml", "name: postgres");
        temp.Write("charts/redis-17.0.1.tgz", "x");
        temp.Write("Chart.yaml", "apiVersion: v2\ndependencies:\n  - name: kafka\n    version: 1.0.0\n  - name: kafka-ui\n    alias: cache\n");

        var dead = HelmValueUsage.FindDead(temp.Path, []).Select(d => d.Path).ToList();

        Assert.Equal(["templates.enabled", "other.o"], dead);
    }

    [Fact]
    public void Without_a_charts_folder_or_Chart_yaml_nothing_is_a_subchart()
    {
        using var temp = new TempFolder();
        temp.Write("values.yaml", "templates:\n  enabled: true\nChart:\n  x: 1\n");
        temp.Write("templates/cm.yaml", "kind: ConfigMap");

        Assert.Equal(["templates.enabled", "Chart.x"], HelmValueUsage.FindDead(temp.Path, []).Select(d => d.Path));
    }

    [Fact]
    public void Names_values_files_by_where_they_are()
    {
        using var temp = new TempFolder();
        temp.Write("chart/values.yaml", "a: 1\n");
        temp.Write("chart/templates/cm.yaml", "kind: ConfigMap");
        temp.Write("chart/env/values-eu.yaml", "b: 1\n");
        temp.Write("elsewhere/values-us.yaml", "c: 1\n");
        temp.Write("chart/broken.yaml", "a: [1, 2\n");
        temp.Write("chart/not-a-map.yaml", "- one\n- two\n");

        var dead = HelmValueUsage.FindDead(
            temp.Full("chart"),
            [temp.Full("chart/env/values-eu.yaml"), temp.Full("elsewhere/values-us.yaml"), temp.Full("chart/missing.yaml"), temp.Full("chart/broken.yaml"), temp.Full("chart/not-a-map.yaml"), temp.Full("chart/values.yaml")]);

        Assert.Equal([("a", "values.yaml"), ("b", "env/values-eu.yaml"), ("c", "values-us.yaml")], dead.Select(d => (d.Path, d.File)));
    }

    [Fact]
    public void Lists_a_value_once_per_file_that_sets_it()
    {
        var dead = HelmValueUsage.FindDead(
            ["kind: ConfigMap"],
            [("a", "one.yaml"), ("a", "one.yaml"), ("a", "two.yaml")],
            []);

        Assert.Equal([("a", "one.yaml"), ("a", "two.yaml")], dead.Select(d => (d.Path, d.File)));
    }

    [Fact]
    public void Malformed_values_YAML_is_a_YamlException_whatever_YamlDotNet_throws()
    {
        // Found by fuzzing: this made YamlDotNet's scanner throw InvalidOperationException.
        const string values = "replicaCount: 2\nimage:\n  repository: nginx\n  tag: \"1.25\"\n  pullPolra: {{ index .Values \"config\" \"level\" }}\n  {{ index .Values.ingresel: info\n  unusedKey: x\ningress:\n  hosts: [a, b]\n";

        Assert.ThrowsAny<YamlDotNet.Core.YamlException>(() => HelmValueUsage.Leaves(values).ToList());
    }

    [Theory]
    [InlineData("a: &x\n  b: *x\n")]
    [InlineData("a: &x\n  - *x\n")]
    public void Recursive_anchors_in_values_are_a_YamlException_not_a_stack_overflow(string values)
    {
        // Found by fuzzing: YamlDotNet hashes a mapping that holds itself until the stack overflows.
        Assert.ThrowsAny<YamlDotNet.Core.YamlException>(() => HelmValueUsage.Leaves(values).ToList());
    }
}
