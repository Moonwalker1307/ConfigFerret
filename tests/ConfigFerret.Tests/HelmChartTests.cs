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
    public void Does_not_follow_links_in_the_templates_folder()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs extra rights on Windows.");

        var chart = Directory.CreateTempSubdirectory("cf-chart-").FullName;
        var outside = Directory.CreateTempSubdirectory("cf-chart-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Join(chart, "values.yaml"), "a: 1\n");
            Directory.CreateDirectory(Path.Join(chart, "templates"));
            File.WriteAllText(Path.Join(chart, "templates", "own.yaml"), "env:\n  - name: OWN\n    value: {{ .Values.a }}\n");
            File.WriteAllText(Path.Join(outside, "other.yaml"), "env:\n  - name: OUTSIDE\n    value: {{ .Values.a }}\n");
            Directory.CreateSymbolicLink(Path.Join(chart, "templates", "loop"), ".");
            Directory.CreateSymbolicLink(Path.Join(chart, "templates", "extra"), outside);

            var loaded = HelmChart.Load(chart);

            Assert.Equal("templates/own.yaml", Assert.Single(loaded.FindUses("OWN")).Template);
            Assert.Empty(loaded.FindUses("OUTSIDE"));
        }
        finally
        {
            Directory.Delete(chart, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void A_backslash_in_a_template_file_name_stays_a_backslash()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A backslash is the separator on Windows.");
        var chart = Directory.CreateTempSubdirectory("cf-chart-").FullName;
        try
        {
            File.WriteAllText(Path.Join(chart, "values.yaml"), "a: 1\n");
            Directory.CreateDirectory(Path.Join(chart, "templates", "a"));
            File.WriteAllText(Path.Join(chart, "templates", "a\\b.yaml"), "env:\n  - name: SLASH\n    value: {{ .Values.a }}\n");
            File.WriteAllText(Path.Join(chart, "templates", "a", "b.yaml"), "env:\n  - name: NESTED\n    value: {{ .Values.a }}\n");

            var loaded = HelmChart.Load(chart);

            Assert.Equal("templates/a\\b.yaml", Assert.Single(loaded.FindUses("SLASH")).Template);
            Assert.Equal("templates/a/b.yaml", Assert.Single(loaded.FindUses("NESTED")).Template);
        }
        finally
        {
            Directory.Delete(chart, recursive: true);
        }
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
