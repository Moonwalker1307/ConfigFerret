using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Tests;

public class ConfigTracerTests
{
    private static IConfigurationRoot Memory(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value)).Build();

    private static ProvenanceStep Step(string source, string? value = "v", bool sensitive = false, bool removal = false) => new()
    {
        Kind = LayerKind.Helm,
        Source = source,
        Value = value,
        IsSensitive = sensitive,
        IsRemoval = removal,
    };

    /// <summary>Answers every step with the same upstream chain, whatever its depth.</summary>
    private sealed class Fixed(params ProvenanceStep[] chain) : IOriginResolver
    {
        public int Calls { get; private set; }

        public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
        {
            Calls++;
            diagnostics.Warn("resolver ran");
            return chain;
        }
    }

    private static int Depth(ProvenanceStep step) => step.Origins.Count == 0 ? 0 : 1 + Depth(step.Origins[^1]);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public void Follows_origins_up_to_MaxDepth(int maxDepth, int depth)
    {
        var setting = ConfigTracer.For(Memory(("K", "v")))
            .WithResolver(new Fixed(Step("up")))
            .WithOptions(o => o.MaxDepth = maxDepth)
            .Trace().Find("K")!;

        Assert.Equal(depth, Depth(setting.Steps[0]));
    }

    [Fact]
    public void Does_not_resolve_at_all_with_a_max_depth_of_zero()
    {
        var resolver = new Fixed(Step("up"));
        var report = ConfigTracer.For(Memory(("K", "v"))).WithResolver(resolver).WithOptions(o => o.MaxDepth = 0).Trace();

        Assert.Equal(0, resolver.Calls);
        Assert.Empty(report.Find("K")!.Steps[0].Origins);
    }

    [Fact]
    public void Joins_origins_from_every_resolver_and_marks_the_last_of_each_chain_effective()
    {
        var setting = ConfigTracer.For(Memory(("K", "v")))
            .WithResolver(new Fixed())
            .WithResolver(new Fixed(Step("a1"), Step("a2")))
            .WithResolver(new Fixed(Step("b1"), Step("b2", removal: true)))
            .WithOptions(o => o.MaxDepth = 1)
            .Trace().Find("K")!;

        var origins = setting.Steps[0].Origins;
        Assert.Equal(["a1", "a2", "b1", "b2"], origins.Select(o => o.Source));
        Assert.Equal([false, true, false, false], origins.Select(o => o.IsEffective));
        Assert.Contains("resolver ran", ConfigTracer.For(Memory(("K", "v"))).WithResolver(new Fixed()).Trace().Warnings);
    }

    [Fact]
    public void Keeps_origins_a_resolver_already_returned()
    {
        var preset = Step("up") with { Origins = [Step("preset")] };
        var setting = ConfigTracer.For(Memory(("K", "v")))
            .WithResolver(new Fixed(preset))
            .WithOptions(o => o.MaxDepth = 2)
            .Trace().Find("K")!;

        var up = Assert.Single(setting.Steps[0].Origins);
        Assert.Equal(["preset", "up"], up.Origins.Select(o => o.Source));
    }

    [Fact]
    public void Redacts_values_whose_winning_origin_is_secret()
    {
        var report = ConfigTracer.For(Memory(("Plain", "v")))
            .WithResolver(new Fixed(Step("public", "p"), Step("secret", "s", sensitive: true)))
            .WithOptions(o => o.MaxDepth = 1)
            .Trace();

        var setting = report.Find("Plain")!;
        Assert.True(setting.IsSensitive);
        Assert.Equal("***", setting.Value);
        Assert.Equal("***", setting.Steps[0].Value);
        Assert.True(setting.Steps[0].IsSensitive);
        Assert.Equal(["p", "***"], setting.Steps[0].Origins.Select(o => o.Value));
    }

    [Fact]
    public void A_secret_that_lost_does_not_make_the_value_sensitive()
    {
        var setting = ConfigTracer.For(Memory(("Plain", "v")))
            .WithResolver(new Fixed(Step("secret", "s", sensitive: true), Step("public", "p")))
            .WithOptions(o => o.MaxDepth = 1)
            .Trace().Find("Plain")!;

        Assert.False(setting.IsSensitive);
        Assert.Equal("v", setting.Value);
        Assert.Equal(["***", "p"], setting.Steps[0].Origins.Select(o => o.Value));
    }

    [Fact]
    public void Uses_a_custom_key_pattern_redacted_text_and_reveal_switch()
    {
        var config = Memory(("Hidden", "h"), ("Password", "pw"));

        var custom = ConfigTracer.For(config)
            .WithOptions(new TraceOptions { SensitiveKeyPattern = new Regex("^hid", RegexOptions.IgnoreCase), RedactedText = "[x]" })
            .Trace();
        var off = ConfigTracer.For(config).WithOptions(new TraceOptions { SensitiveKeyPattern = null }).Trace();
        var revealed = ConfigTracer.For(config).WithOptions(o => o.RevealSensitiveValues = true).Trace();

        Assert.Equal("[x]", custom.Find("Hidden")!.Value);
        Assert.Equal("[x]", custom.Find("Hidden")!.Steps[0].Value);
        Assert.Equal("pw", custom.Find("Password")!.Value);
        Assert.False(off.Find("Password")!.IsSensitive);
        Assert.Equal("pw", off.Find("Password")!.Value);
        Assert.True(revealed.Find("Password")!.IsSensitive);
        Assert.Equal("pw", revealed.Find("Password")!.Value);
        Assert.Equal("pw", revealed.Find("Password")!.Steps[0].Value);
    }

    [Fact]
    public void Matches_environment_variables_from_the_given_environment()
    {
        Environment.SetEnvironmentVariable("CFTR_A", "process");
        var config = new ConfigurationBuilder().AddEnvironmentVariables("CFTR_").Build();
        var options = new TraceOptions { Environment = new Dictionary<string, string?> { ["CFTR_a"] = "process" } };

        var given = ConfigTracer.For(config).WithOptions(options).Trace().Find("A")!;
        var process = ConfigTracer.For(config).Trace().Find("A")!;

        Assert.Equal("environment variable CFTR_a", given.Steps[0].Source);
        Assert.Equal("environment variable CFTR_A", process.Steps[0].Source);
    }

    [Fact]
    public void Leaves_out_keys_with_no_value_and_sorts_keys()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["b"] = "1", ["A:x"] = "2", ["c"] = null })
            .Build();

        var report = ConfigTracer.For(config).Trace();

        Assert.Equal(["A:x", "b"], report.Settings.Select(s => s.Key));
    }

    [Fact]
    public void Rejects_null_arguments()
    {
        var tracer = ConfigTracer.For(Memory());

        Assert.Throws<ArgumentNullException>(() => ConfigTracer.For(null!));
        Assert.Throws<ArgumentNullException>(() => tracer.WithOptions((TraceOptions)null!));
        Assert.Throws<ArgumentNullException>(() => tracer.WithOptions((Action<TraceOptions>)null!));
        Assert.Throws<ArgumentNullException>(() => tracer.WithKubernetes(null!));
        Assert.Throws<ArgumentNullException>(() => tracer.WithHelm(null!));
        Assert.Throws<ArgumentNullException>(() => tracer.WithKustomize(null!));
        Assert.Throws<ArgumentNullException>(() => tracer.WithResolver(null!));
        Assert.Throws<ArgumentNullException>(() => tracer.Trace().Find(null!));
    }

    [Fact]
    public void Story_says_not_set_without_steps_and_follows_at_most_seven_origins()
    {
        var chain = Step("s9");
        for (var i = 8; i >= 1; i--)
        {
            chain = Step($"s{i}") with { IsEffective = true, Kind = LayerKind.KubernetesConfigMap, Origins = [chain with { IsEffective = true }] };
        }

        var empty = new TracedSetting("K", null, [], false);
        var deep = new TracedSetting("K", "v", [Step("root") with { Kind = LayerKind.DotNetProvider, Origins = [chain] }], false);

        Assert.Equal("not set", empty.Story);
        Assert.Null(empty.EffectiveStep);
        Assert.Equal("root", deep.EffectiveStep!.Source);
        Assert.Equal("set in root from s1 from s2 from s3 from s4 from s5 from s6 from s7", deep.Story);
    }
}
