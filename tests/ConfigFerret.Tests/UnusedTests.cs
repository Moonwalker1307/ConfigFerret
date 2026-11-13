using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Tests;

public class UnusedTests
{
    private static ProvenanceStep Step(string source, string? value = "v", bool sensitive = false, bool removal = false) => new()
    {
        Kind = LayerKind.Helm,
        Source = source,
        Value = value,
        IsSensitive = sensitive,
        IsRemoval = removal,
    };

    private sealed class Fixed(params ProvenanceStep[] chain) : IOriginResolver
    {
        public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics) =>
            layer.Kind == LayerKind.DotNetProvider ? chain : [];
    }

    private static IConfigurationRoot Layer(Dictionary<string, string?> layer) => Layers([layer]);

    private static IConfigurationRoot Layers(params Dictionary<string, string?>[] layers)
    {
        var builder = new ConfigurationBuilder();
        foreach (var layer in layers)
        {
            builder.AddInMemoryCollection(layer);
        }

        return builder.Build();
    }

    [Fact]
    public void Reports_a_dotnet_layer_that_a_later_layer_overrides()
    {
        var config = Layers(new() { ["Db:Timeout"] = "30", ["Only:Here"] = "x" }, new() { ["Db:Timeout"] = "42" });

        var unused = config.TraceProvenance().FindUnused();

        var item = Assert.Single(unused.NeverWinning);
        Assert.Equal("Db:Timeout", item.Key);
        Assert.Equal("30", item.Step.Value);
        Assert.Equal("42", item.WonBy.Value);
        Assert.Empty(item.Upstream);
        Assert.False(item.SameValueAsWinner);
        Assert.Null(unused.NotRead);
        Assert.Null(unused.RecordedReads);
    }

    [Fact]
    public void Flags_an_override_that_sets_the_same_value()
    {
        var config = Layers(new() { ["A"] = "1" }, new() { ["A"] = "1" });

        Assert.True(Assert.Single(config.TraceProvenance().FindUnused().NeverWinning).SameValueAsWinner);
    }

    [Fact]
    public void Never_compares_sensitive_values()
    {
        var config = Layers(new() { ["Db:Password"] = "same" }, new() { ["Db:Password"] = "same" });

        var item = Assert.Single(config.TraceProvenance().FindUnused().NeverWinning);
        Assert.False(item.SameValueAsWinner);
        Assert.Equal("***", item.Step.Value);
        Assert.DoesNotContain("same", config.TraceProvenance().FindUnused().ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_overridden_upstream_layers_with_the_path_to_them()
    {
        var config = Layer(new() { ["K"] = "42" });

        var unused = ConfigTracer.For(config)
            .WithResolver(new Fixed(Step("values.yaml", "30"), Step("values-prod.yaml", "42")))
            .Trace().FindUnused();

        var item = Assert.Single(unused.NeverWinning);
        Assert.Equal("values.yaml", item.Step.Source);
        Assert.Equal("values-prod.yaml", item.WonBy.Source);
        Assert.Equal(["in-memory collection"], item.Upstream.Select(s => s.Source));
        Assert.Contains("upstream of in-memory collection", unused.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Skips_what_sits_upstream_of_a_losing_layer()
    {
        // Both layers get the same upstream chain; only the winning layer's chain counts.
        var config = Layers(new() { ["K"] = "1" }, new() { ["K"] = "2" });

        var unused = ConfigTracer.For(config)
            .WithResolver(new Fixed(Step("low.yaml", "1"), Step("high.yaml", "2")))
            .Trace().FindUnused();

        Assert.Equal(2, unused.NeverWinning.Count);
        Assert.Single(unused.NeverWinning, i => i.Step.Source == "in-memory collection");
        var upstream = Assert.Single(unused.NeverWinning, i => i.Step.Source == "low.yaml");
        Assert.Single(upstream.Upstream);
    }

    [Fact]
    public void Says_removed_by_for_a_removal_and_does_not_report_the_removal_itself()
    {
        var config = Layer(new() { ["K"] = "42" });

        var unused = ConfigTracer.For(config)
            .WithResolver(new Fixed(Step("base.yaml", "1"), Step("overlay.yaml", null, removal: true), Step("overlay.yaml", "42")))
            .Trace().FindUnused();

        var item = Assert.Single(unused.NeverWinning);
        Assert.Equal("base.yaml", item.Step.Source);
        Assert.True(item.WonBy.IsRemoval);
        Assert.Contains("removed by overlay.yaml", unused.ToText(), StringComparison.Ordinal);
        Assert.Contains("\"removedBy\"", unused.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Pairs_each_losing_step_with_the_winner_of_its_own_chain()
    {
        var config = Layer(new() { ["K"] = "v" });

        var unused = ConfigTracer.For(config)
            .WithResolver(new Fixed(Step("a1"), Step("a2")))
            .WithResolver(new Fixed(Step("b1"), Step("b2")))
            .Trace().FindUnused();

        Assert.Equal(["a1->a2", "b1->b2"], unused.NeverWinning.Select(i => $"{i.Step.Source}->{i.WonBy.Source}"));
    }

    [Fact]
    public void Lists_configured_keys_that_were_not_read()
    {
        var config = Layer(new() { ["Db:Timeout"] = "30", ["Legacy:Mode"] = "on", ["Db:Host"] = "h" });

        var unused = config.TraceProvenance().FindUnused(["db__timeout", "Db:Host", "Never:Configured"]);

        Assert.Equal(["Legacy:Mode"], unused.NotRead!.Select(s => s.Key));
        Assert.Equal(3, unused.RecordedReads);
        Assert.Contains("Keys the app did not read (1, from 3 recorded reads)", unused.ToText(), StringComparison.Ordinal);
        Assert.Contains("\"notRead\": [", unused.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Says_unread_keys_were_not_checked_without_reads()
    {
        var unused = Layer(new() { ["A"] = "1" }).TraceProvenance().FindUnused();

        Assert.Contains("not checked", unused.ToText(), StringComparison.Ordinal);
        Assert.Contains("\"notRead\": null", unused.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_out_plain_process_environment_variables_but_keeps_config_shaped_ones()
    {
        Environment.SetEnvironmentVariable("CFUNUSEDTEST__Section", "x");
        Environment.SetEnvironmentVariable("CFUNUSEDTESTPLAIN", "y");
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();

        var unused = config.TraceProvenance().FindUnused(Array.Empty<string>());

        Assert.Contains(unused.NotRead!, s => s.Key == "CFUNUSEDTEST:Section");
        Assert.DoesNotContain(unused.NotRead!, s => s.Key == "CFUNUSEDTESTPLAIN");
        Assert.True(unused.SkippedEnvironmentKeys > 0);
    }

    [Fact]
    public void Keeps_prefixed_environment_variables()
    {
        var env = new Dictionary<string, string?> { ["CFPREFIXTEST_Plain"] = "x" };
        Environment.SetEnvironmentVariable("CFPREFIXTEST_Plain", "x");
        var config = new ConfigurationBuilder().AddEnvironmentVariables("CFPREFIXTEST_").Build();

        var unused = ConfigTracer.For(config).WithOptions(o => o.Environment = env).Trace().FindUnused([]);

        Assert.Equal(["Plain"], unused.NotRead!.Select(s => s.Key));
        Assert.Equal(0, unused.SkippedEnvironmentKeys);
    }

    [Fact]
    public void Marks_the_removing_layer_in_JSON()
    {
        var config = Layer(new() { ["K"] = "42" });

        var json = ConfigTracer.For(config)
            .WithResolver(new Fixed(Step("base.yaml", "1"), Step("overlay.yaml", null, removal: true), Step("overlay.yaml", "42")))
            .Trace().FindUnused().ToJson();

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var removedBy = doc.RootElement.GetProperty("neverWinning")[0].GetProperty("removedBy");
        Assert.True(removedBy.GetProperty("removal").GetBoolean());
        Assert.False(removedBy.TryGetProperty("value", out _));
    }

    [Fact]
    public void Says_how_many_plain_environment_variables_it_left_out()
    {
        Environment.SetEnvironmentVariable("CFUNUSEDCOUNTTEST", "y");
        try
        {
            var unused = new ConfigurationBuilder().AddEnvironmentVariables().Build().TraceProvenance().FindUnused([]);

            Assert.Contains($"  ({unused.SkippedEnvironmentKeys} plain process environment variables such as PATH are left out)", unused.ToText(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CFUNUSEDCOUNTTEST", null);
        }
    }

    [Fact]
    public void Follows_the_report_order_for_a_container()
    {
        // A container report lists environment variables before files, so FindUnused does too rather than sorting.
        var manifests = ConfigFerret.Kubernetes.KubernetesManifests.Parse("""
            apiVersion: v1
            kind: Pod
            metadata: { name: api }
            spec:
              containers:
                - name: app
                  env:
                    - { name: Z_LAST, value: "1" }
                  volumeMounts:
                    - { name: cfg, mountPath: /app/a }
              volumes:
                - name: cfg
                  configMap: { name: cfg }
            ---
            apiVersion: v1
            kind: ConfigMap
            metadata: { name: cfg }
            data: { settings.json: "{}" }
            """);

        var report = ConfigTracer.ForContainer(manifests).Trace();
        var unused = report.FindUnused([]);

        Assert.Equal(report.Settings.Select(s => s.Key), unused.NotRead!.Select(s => s.Key));
        Assert.Equal(["Z_LAST", "/app/a/settings.json"], unused.NotRead!.Select(s => s.Key));
    }
}
