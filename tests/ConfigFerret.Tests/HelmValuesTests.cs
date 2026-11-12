using ConfigFerret.Helm;

namespace ConfigFerret.Tests;

public sealed class HelmValuesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cf-helm-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static HelmChart Values(params string[] yaml) =>
        HelmChart.FromText(yaml.Select((y, i) => ($"v{i}.yaml", y)));

    private static string[] Shape(HelmChart chart, string path) =>
        chart.History[path].Select(h => h.IsRemoval ? $"{h.Source}:removed" : $"{h.Source}={h.Value}").ToArray();

    [Fact]
    public void A_map_replacing_a_scalar_removes_the_scalar()
    {
        var chart = Values("a: 1\n", "a:\n  b: 2\n");

        Assert.Equal(["v0.yaml=1", "v1.yaml:removed"], Shape(chart, "a"));
        Assert.Equal(2, chart.History["a"][1].Line);
        Assert.Null(chart.GetValue("a"));
        Assert.Equal("2", chart.GetValue("a.b"));
    }

    [Fact]
    public void A_scalar_replacing_a_map_removes_its_children()
    {
        var chart = Values("a:\n  b: 1\n  c: { d: 2 }\n", "a: 3\n");

        Assert.Equal(["v0.yaml=1", "v1.yaml:removed"], Shape(chart, "a.b"));
        Assert.Equal(["v0.yaml=2", "v1.yaml:removed"], Shape(chart, "a.c.d"));
        Assert.Equal(1, chart.History["a.b"][1].Line);
        Assert.Equal("3", chart.GetValue("a"));
    }

    [Fact]
    public void Removing_twice_records_one_removal()
    {
        var chart = Values("a: 1\nm: { x: 1 }\n", "a: null\nm: ~\n", "a: ~\nm: null\n");

        Assert.Equal(["v0.yaml=1", "v1.yaml:removed"], Shape(chart, "a"));
        Assert.Equal(["v0.yaml=1", "v1.yaml:removed"], Shape(chart, "m.x"));
        Assert.Null(chart.GetValue("missing"));
    }

    [Fact]
    public void Set_values_override_split_on_commas_and_honour_escapes()
    {
        var chart = HelmChart.Load(Write("values.yaml", "a:\n  b: 1\nc: 1\nd: 1\n"), setValues:
        [
            "a=x,c=null,,noequals,=nokey,e=1\\,2,f=",
            "g=trailing\\",
            "d=2,",
        ]);

        Assert.Equal(["values.yaml (chart default)=1", "--set a:removed"], Shape(chart, "a.b"));
        Assert.Equal("x", chart.GetValue("a"));
        Assert.Equal(["values.yaml (chart default)=1", "--set c:removed"], Shape(chart, "c"));
        Assert.Equal("1,2", chart.GetValue("e"));
        Assert.Equal("", chart.GetValue("f"));
        Assert.Equal("trailing\\", chart.GetValue("g"));
        Assert.Equal("2", chart.GetValue("d"));
        Assert.Equal(0, chart.History["d"][^1].Line);
        Assert.False(chart.History.ContainsKey("noequals"));
        Assert.False(chart.History.ContainsKey(""));
    }

    [Fact]
    public void Set_null_removes_a_whole_subtree()
    {
        var chart = HelmChart.Load(Write("values.yaml", "a:\n  b: 1\n"), setValues: ["a=null"]);

        Assert.Equal(["values.yaml (chart default)=1", "--set a:removed"], Shape(chart, "a.b"));
    }

    [Fact]
    public void Looks_back_up_to_four_lines_for_the_env_name()
    {
        var chart = HelmChart.FromText([("values.yaml", "v: 1\n")],
        [
            ("t.yaml", """
                - name: OUTER
                - name: NEAR
                  # 1
                  # 2
                  value: {{ .Values.v }}
                - name: EDGE
                  # 1
                  # 2
                  # 3
                  value: {{ .Values.v }}
                - name: FAR
                  # 1
                  # 2
                  # 3
                  # 4
                  value: {{ .Values.v }}
                - name: "{{ .Release.Name }}"
                  value: {{ .Values.v }}
                """),
        ]);

        Assert.Equal(5, Assert.Single(chart.FindUses("NEAR")).Line);
        Assert.Empty(chart.FindUses("OUTER"));
        Assert.Equal(10, Assert.Single(chart.FindUses("EDGE")).Line);
        Assert.Empty(chart.FindUses("FAR"));
        Assert.DoesNotContain(chart.FindUses("{{"), _ => true);
    }

    [Fact]
    public void Loads_templates_from_subfolders_in_name_order()
    {
        var dir = Write("templates/b.yaml", "data:\n  K: {{ .Values.b }}\n");
        Write("templates/sub/a.yaml", "data:\n  K: {{ .Values.a }}\n");
        Write("templates/a.tpl", "data:\n  K: {{ .Values.tpl }}\n");

        var chart = HelmChart.Load(dir);

        Assert.Equal(["templates/a.tpl", "templates/b.yaml", "templates/sub/a.yaml"], chart.FindUses("K").Select(u => u.Template));
        Assert.Empty(chart.History);
    }

    [Fact]
    public void Loads_a_chart_without_values_or_templates()
    {
        var chart = HelmChart.Load(_dir, [Write("prod.yaml", "a: 1\n") is var d ? Path.Join(d, "prod.yaml") : ""]);

        Assert.Equal(["prod.yaml=1"], Shape(chart, "a"));
        Assert.Equal(_dir, chart.ChartDirectory);
    }

    [Fact]
    public void Reports_bad_input()
    {
        Assert.Throws<ArgumentNullException>(() => HelmChart.Load(null!));
        Assert.Throws<ArgumentNullException>(() => HelmChart.FromText(null!));
        var ex = Assert.Throws<FormatException>(() => HelmChart.FromText([("bad.yaml", "a: [\n")]));
        Assert.StartsWith("Could not parse Helm values in bad.yaml: ", ex.Message, StringComparison.Ordinal);
    }

    private string Write(string relative, string text)
    {
        var path = Path.Join(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return _dir;
    }
}
