using ConfigFerret.Desktop.ViewModels;

namespace ConfigFerret.Desktop.Tests;

public class SettingRowTests
{
    private static ProvenanceStep Step(string source, LayerKind kind = LayerKind.DotNetProvider, bool effective = false, bool inferred = false, params ProvenanceStep[] origins) =>
        new() { Kind = kind, Source = source, IsEffective = effective, IsInferred = inferred, Origins = origins };

    private static SettingRow Row(string key, string? value, params ProvenanceStep[] steps) =>
        new(new TracedSetting(key, value, steps, false));

    [Fact]
    public void A_row_needs_a_setting()
    {
        Assert.Throws<ArgumentNullException>(() => new SettingRow(null!));
    }

    [Fact]
    public void A_setting_with_no_layers_is_not_set()
    {
        var row = Row("Db:Timeout", "30");

        Assert.Equal("(not set)", row.EffectiveSource);
        Assert.Equal(string.Empty, row.OriginSource);
        Assert.Equal("(not set)", row.ComesFrom);
        Assert.Equal(LayerKind.DotNetProvider, row.OriginKind);
        Assert.Equal(0, row.LayerCount);
        Assert.False(row.IsOverridden);
        Assert.False(row.IsInferred);
        Assert.Empty(row.Kinds);
    }

    [Fact]
    public void The_last_layer_wins_and_more_than_one_layer_means_overridden()
    {
        var row = Row("Db:Timeout", "30", Step("appsettings.json"), Step("environment variable DB__TIMEOUT"));

        Assert.Equal("environment variable DB__TIMEOUT", row.EffectiveSource);
        Assert.True(row.IsOverridden);
        Assert.Equal(2, row.LayerCount);
        Assert.Equal("Db:Timeout", row.Key);
        Assert.Equal("30", row.Value);
    }

    [Fact]
    public void One_layer_is_not_overridden()
    {
        Assert.False(Row("A", "1", Step("appsettings.json")).IsOverridden);
    }

    [Fact]
    public void The_origin_is_the_deepest_effective_upstream_layer()
    {
        var values = Step("values.yaml", LayerKind.Helm, effective: true);
        var configMap = Step("ConfigMap api", LayerKind.KubernetesConfigMap, effective: true, origins: [Step("ignored", LayerKind.Helm), values]);
        var row = Row("A", "1", Step("environment variable A", LayerKind.DotNetProvider, effective: true, origins: [configMap]));

        Assert.Equal("environment variable A", row.EffectiveSource);
        Assert.Equal("values.yaml", row.OriginSource);
        Assert.Equal("values.yaml", row.ComesFrom);
        Assert.Equal(LayerKind.Helm, row.OriginKind);
        Assert.Equal(4, row.LayerCount);
    }

    [Fact]
    public void Upstream_layers_that_do_not_win_are_not_the_origin()
    {
        var row = Row("A", "1", Step("environment variable A", effective: true, origins: [Step("ConfigMap api", LayerKind.KubernetesConfigMap)]));

        Assert.Equal(string.Empty, row.OriginSource);
        Assert.Equal("environment variable A", row.ComesFrom);
        Assert.Equal(LayerKind.DotNetProvider, row.OriginKind);
    }

    [Fact]
    public void The_last_of_several_effective_origins_is_followed()
    {
        var row = Row("A", "1", Step("env", effective: true, origins: [Step("first", LayerKind.Helm, effective: true), Step("second", LayerKind.Kustomize, effective: true)]));

        Assert.Equal("second", row.OriginSource);
        Assert.Equal(LayerKind.Kustomize, row.OriginKind);
    }

    [Fact]
    public void Chips_leave_out_the_dotnet_layer_when_there_is_something_beyond_it()
    {
        var row = Row("A", "1", Step("env", origins: [Step("cm", LayerKind.KubernetesConfigMap, origins: [Step("v", LayerKind.Helm)])]));

        Assert.Equal([LayerKind.KubernetesConfigMap, LayerKind.Helm], row.Kinds.Select(k => k.Kind));
        Assert.Equal([LayerKind.DotNetProvider, LayerKind.KubernetesConfigMap, LayerKind.Helm], row.AllKinds);
    }

    [Fact]
    public void Chips_keep_the_dotnet_layer_when_it_is_the_only_kind()
    {
        var row = Row("A", "1", Step("a.json"), Step("b.json"));

        Assert.Equal([LayerKind.DotNetProvider], row.Kinds.Select(k => k.Kind));
        Assert.Equal([LayerKind.DotNetProvider], row.AllKinds);
    }

    [Fact]
    public void Kinds_are_listed_once_in_enum_order()
    {
        var row = Row("A", "1", Step("x", LayerKind.OctopusDeploy), Step("y", LayerKind.Helm), Step("z", LayerKind.Helm));

        Assert.Equal([LayerKind.Helm, LayerKind.OctopusDeploy], row.AllKinds);
    }

    [Fact]
    public void Inferred_counts_only_when_the_inferred_link_wins()
    {
        Assert.True(Row("A", "1", Step("env", effective: true, origins: [Step("t", LayerKind.Helm, effective: true, inferred: true)])).IsInferred);
        Assert.False(Row("A", "1", Step("env", effective: true, origins: [Step("t", LayerKind.Helm, effective: false, inferred: true)])).IsInferred);
        Assert.False(Row("A", "1", Step("env", effective: true, origins: [Step("t", LayerKind.Helm, effective: true, inferred: false)])).IsInferred);
    }

    [Theory]
    [InlineData("Db:Timeout", "Db")]
    [InlineData("Db:Pool:Size", "Db")]
    [InlineData("Plain", "(top level)")]
    [InlineData(":Odd", "(top level)")]
    [InlineData("/etc/secrets/key", "Mounted files")]
    [InlineData("/etc/secrets:key", "/etc/secrets")]
    public void Section_is_the_first_key_segment(string key, string section)
    {
        Assert.Equal(section, Row(key, "1").Section);
    }

    [Fact]
    public void Value_is_shown_on_one_line()
    {
        Assert.Equal("(no value)", Row("A", null).Value);
        Assert.Equal("a ⏎ b ⏎ c", Row("A", "a\nb\r\nc").Value);
    }

    [Fact]
    public void Sensitivity_and_story_come_from_the_setting()
    {
        var setting = new TracedSetting("A", "x", [Step("appsettings.json")], true);
        var row = new SettingRow(setting);

        Assert.True(row.IsSensitive);
        Assert.Same(setting, row.Setting);
        Assert.Equal(setting.Story, row.Story);
        Assert.False(Row("A", "x").IsSensitive);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(" ", true)]
    [InlineData("db:time", true)]
    [InlineData("DB__TIME", true)]
    [InlineData("db__time", true)]
    [InlineData("THIRTY", true)]
    [InlineData("values.yaml", true)]
    [InlineData("api-config", true)]
    [InlineData("absent", false)]
    public void Matches_key_value_or_any_source_in_the_history(string? filter, bool expected)
    {
        var row = Row("Db:Timeout", "thirty", Step("env", origins: [Step("ConfigMap api-config", LayerKind.KubernetesConfigMap, origins: [Step("values.yaml", LayerKind.Helm)])]));

        Assert.Equal(expected, row.Matches(filter));
    }

    [Fact]
    public void Matches_does_not_fail_without_a_value()
    {
        Assert.False(Row("A", null, Step("env")).Matches("zzz"));
    }
}
