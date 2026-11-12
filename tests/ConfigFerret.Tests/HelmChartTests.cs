using ConfigFerret.Helm;

namespace ConfigFerret.Tests;

public class HelmChartTests
{
    [Fact]
    public void Merges_values_files_and_set_in_order()
    {
        var chart = HelmChart.Load(TestPaths.Data("chart"), [TestPaths.Data("values-prod.yaml")], ["db.timeout=99,db.host=a\\,b"]);

        Assert.Equal("99", chart.GetValue("db.timeout"));
        Assert.Equal("a,b", chart.GetValue("db.host"));
        Assert.Equal(["values.yaml (chart default)", "values-prod.yaml", "--set db.timeout"],
            chart.History["db.timeout"].Select(h => h.Source));
    }

    [Fact]
    public void Null_removes_a_value_and_its_children()
    {
        var chart = HelmChart.Load(TestPaths.Data("chart"), [TestPaths.Data("values-prod.yaml")]);

        Assert.Null(chart.GetValue("logging.extra.a"));
        Assert.True(chart.History["logging.extra.a"][^1].IsRemoval);
        Assert.Equal("Information", chart.GetValue("logging.level"));
    }

    [Fact]
    public void Indexes_env_entries_and_data_keys_in_templates()
    {
        var chart = HelmChart.FromText(
            [("values.yaml", "a:\n  b: 1\n")],
            [("templates/x.yaml", """
                env:
                  - name: "MY_VAR"
                    value: {{ .Values.a.b | quote }}
                data:
                  OTHER: {{ default "x" .Values.a.c }}
                  name: {{ .Values.ignored }}
                """)]);

        var env = Assert.Single(chart.FindUses("MY_VAR"));
        Assert.Equal(("templates/x.yaml", 3, "a.b"), (env.Template, env.Line, env.ValuesPath));
        Assert.Equal("a.c", Assert.Single(chart.FindUses("OTHER")).ValuesPath);
        Assert.Empty(chart.FindUses("name"));
    }

    [Fact]
    public void Lists_are_kept_as_one_value()
    {
        var chart = HelmChart.FromText([("values.yaml", "hosts: [a, b]\n"), ("prod.yaml", "hosts:\n  - c\n")]);

        Assert.Equal("[c]", chart.GetValue("hosts"));
        Assert.Equal(2, chart.History["hosts"].Count);
    }

    [Fact]
    public void Set_keeps_commas_inside_a_list()
    {
        var chart = HelmChart.Load(TestPaths.Data("chart"), setValues: ["tags={a, b,c},db.timeout=7", "empty={}"]);

        Assert.Equal("[a, b, c]", chart.GetValue("tags"));
        Assert.Equal("7", chart.GetValue("db.timeout"));
        Assert.Equal("[]", chart.GetValue("empty"));
    }
}
