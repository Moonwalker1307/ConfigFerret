using System.Text.Json;

namespace ConfigFerret.Tests;

public class RendererTests
{
    private static ProvenanceReport SampleReport()
    {
        var origin = new ProvenanceStep
        {
            Kind = LayerKind.Helm,
            Source = "values-prod.yaml",
            Location = "line 3, db.timeout",
            Value = "60",
            IsEffective = true,
            IsInferred = true,
            Note = "renders .Values.db.timeout",
        };
        var steps = new List<ProvenanceStep>
        {
            new() { Kind = LayerKind.DotNetProvider, Source = "appsettings.json", Location = "line 4", Value = "30" },
            new() { Kind = LayerKind.DotNetProvider, Source = "removed layer", IsRemoval = true, Value = "ignored" },
            new()
            {
                Kind = LayerKind.DotNetProvider,
                Source = "environment variable Db__Timeout",
                Value = "60",
                IsEffective = true,
                IsSensitive = true,
                Origins = [origin],
            },
        };
        var settings = new List<TracedSetting>
        {
            new("Db:Timeout", "60", steps, false),
            new("Empty", null, [new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "memory", IsEffective = true }], true),
        };
        return new ProvenanceReport(settings, ["first warning", "second warning"]);
    }

    [Fact]
    public void Text_renders_every_step_note_origin_and_warning()
    {
        var report = SampleReport();
        var story = report.Settings[0].Story;
        var nl = Environment.NewLine;

        var expected = string.Join(nl,
            "Db:Timeout = \"60\"",
            "  " + story,
            "  [overridden] appsettings.json (line 4) = \"30\"",
            "  [removed]    removed layer",
            "  [effective]  environment variable Db__Timeout = \"60\"",
            "    <- [effective]  values-prod.yaml (line 3, db.timeout) = \"60\" [inferred]",
            "                    renders .Values.db.timeout",
            "",
            "Empty = (null)",
            "  set in memory",
            "  [effective]  memory",
            "",
            "Warnings:",
            "  - first warning",
            "  - second warning",
            "");

        Assert.Equal(expected, TextRenderer.Render(report));
        Assert.Equal(expected, report.ToText());
    }

    [Fact]
    public void Text_puts_a_top_level_note_under_the_source()
    {
        var setting = new TracedSetting("K", "v", [new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "s", Value = "v", IsEffective = true, Note = "a note" }], false);
        var nl = Environment.NewLine;

        Assert.Equal($"K = \"v\"{nl}  set in s{nl}  [effective]  s = \"v\"{nl}               a note{nl}", TextRenderer.Render(setting));
    }

    [Fact]
    public void Json_keeps_text_readable_and_still_escapes_quotes_and_control_characters()
    {
        const string value = "it's <ü> & \"q\"\n";
        var report = new ProvenanceReport([new TracedSetting("K", value, [], false)], []);

        var json = report.ToJson();

        Assert.Contains("it's <ü> & \\\"q\\\"\\n", json, StringComparison.Ordinal);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(value, doc.RootElement.GetProperty("settings")[0].GetProperty("value").GetString());
    }

    [Fact]
    public void Text_leaves_out_the_warnings_header_when_there_are_none()
    {
        var report = new ProvenanceReport([], []);

        Assert.Equal("", TextRenderer.Render(report));
    }

    [Theory]
    [InlineData(null, "(null)")]
    [InlineData("a\r\nb", "\"a\\nb\"")]
    public void Show_quotes_values_on_one_line(string? value, string expected) =>
        Assert.Equal(expected, TextRenderer.Show(value));

    [Fact]
    public void Show_cuts_values_longer_than_120_characters()
    {
        Assert.Equal($"\"{new string('x', 120)}\"", TextRenderer.Show(new string('x', 120)));
        Assert.Equal($"\"{new string('x', 117)}...\"", TextRenderer.Show(new string('x', 121)));
    }

    [Fact]
    public void Renderers_reject_null()
    {
        Assert.Throws<ArgumentNullException>(() => TextRenderer.Render((ProvenanceReport)null!));
        Assert.Throws<ArgumentNullException>(() => TextRenderer.Render((TracedSetting)null!));
        Assert.Throws<ArgumentNullException>(() => JsonRenderer.Render(null!));
    }

    [Fact]
    public void Json_writes_every_field_and_only_the_flags_that_are_set()
    {
        var report = SampleReport();
        var json = JsonRenderer.Render(report);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Contains("\n  \"settings\"", json, StringComparison.Ordinal);
        Assert.Equal(json, report.ToJson());
        Assert.Equal(["first warning", "second warning"], root.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));

        var settings = root.GetProperty("settings").EnumerateArray().ToList();
        Assert.Equal(2, settings.Count);
        var timeout = settings[0];
        Assert.Equal("Db:Timeout", timeout.GetProperty("key").GetString());
        Assert.Equal("60", timeout.GetProperty("value").GetString());
        Assert.False(timeout.GetProperty("sensitive").GetBoolean());
        Assert.Equal(report.Settings[0].Story, timeout.GetProperty("story").GetString());
        Assert.True(settings[1].GetProperty("sensitive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, settings[1].GetProperty("value").ValueKind);

        var layers = timeout.GetProperty("layers").EnumerateArray().ToList();
        Assert.Equal(3, layers.Count);

        var first = layers[0];
        Assert.Equal(["kind", "source", "location", "value", "effective"], first.EnumerateObject().Select(p => p.Name));
        Assert.Equal("DotNetProvider", first.GetProperty("kind").GetString());
        Assert.Equal("appsettings.json", first.GetProperty("source").GetString());
        Assert.Equal("line 4", first.GetProperty("location").GetString());
        Assert.Equal("30", first.GetProperty("value").GetString());
        Assert.False(first.GetProperty("effective").GetBoolean());

        Assert.Equal(["kind", "source", "value", "effective", "removal"], layers[1].EnumerateObject().Select(p => p.Name));
        Assert.True(layers[1].GetProperty("removal").GetBoolean());

        var env = layers[2];
        Assert.Equal(["kind", "source", "value", "effective", "sensitive", "origins"], env.EnumerateObject().Select(p => p.Name));
        Assert.True(env.GetProperty("effective").GetBoolean());
        Assert.True(env.GetProperty("sensitive").GetBoolean());

        var origin = Assert.Single(env.GetProperty("origins").EnumerateArray());
        Assert.Equal(["kind", "source", "location", "value", "effective", "inferred", "note"], origin.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Helm", origin.GetProperty("kind").GetString());
        Assert.True(origin.GetProperty("inferred").GetBoolean());
        Assert.Equal("renders .Values.db.timeout", origin.GetProperty("note").GetString());
    }
}
