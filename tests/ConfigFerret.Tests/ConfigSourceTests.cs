using ConfigFerret.Sources;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Tests;

public class ConfigSourceTests
{
    private sealed record StoreRef(string Name) : SourceRef;

    /// <summary>Links every layer whose name is in the store to that entry, like a real store resolver.</summary>
    private sealed class StoreResolver(Dictionary<string, string> values) : IOriginResolver
    {
        public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
        {
            var name = LayerNames.Of(layer);
            return name is not null && values.TryGetValue(LayerNames.ToConfigurationKey(name), out var value)
                ? [new ProvenanceStep { Kind = LayerKind.Other, Source = "store " + name, Value = value, Reference = new StoreRef(LayerNames.ToConfigurationKey(name)) }]
                : [];
        }
    }

    private sealed class Store(Dictionary<string, string> values) : IConfigSource
    {
        public int Loads { get; private set; }

        public string DisplayName => "test store";

        public Task<LoadedConfigSource> LoadAsync(CancellationToken cancellationToken = default)
        {
            Loads++;
            return Task.FromResult(new LoadedConfigSource(
                DisplayName,
                new StoreResolver(values),
                values.Select(v => new SourceEntry(v.Key, new StoreRef(v.Key), IsSensitive: false)),
                ["a warning"]));
        }
    }

    [Fact]
    public async Task WithSourceAsync_loads_once_and_links_layers_and_finds_unused_entries()
    {
        var prefix = $"CFSRC{Guid.NewGuid():N}_";
        Environment.SetEnvironmentVariable(prefix + "Db__Timeout", "42");
        try
        {
            var store = new Store(new() { [prefix + "Db:Timeout"] = "42", ["Db:Unused"] = "1" });
            var tracer = ConfigTracer.For(new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build())
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + "Db__Timeout"] = "42" });
            var loaded = await tracer.WithSourceAsync(store, TestContext.Current.CancellationToken);
            var report = tracer.Trace();

            Assert.Equal(1, store.Loads);
            Assert.Equal("test store", loaded.DisplayName);
            Assert.Equal(["a warning"], loaded.Warnings);
            var origin = Assert.Single(report.Find("Db:Timeout")!.EffectiveStep!.Origins);
            Assert.Equal($"store {prefix}Db__Timeout", origin.Source);
            Assert.Equal(["Db:Unused"], loaded.FindUnused(report).Select(e => e.Name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "Db__Timeout", null);
        }
    }

    [Fact]
    public void FindUnused_looks_at_nested_origins()
    {
        var nested = new ProvenanceStep { Kind = LayerKind.Other, Source = "deep", Reference = new StoreRef("b") };
        var middle = new ProvenanceStep { Kind = LayerKind.KubernetesSecret, Source = "mid", Origins = [nested] };
        var top = new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "top", Reference = new StoreRef("a"), Origins = [middle] };
        var report = new ProvenanceReport([new TracedSetting("k", "v", [top], false)], []);
        var loaded = new LoadedConfigSource("s", new StoreResolver([]), [new("a", new StoreRef("a"), false), new("b", new StoreRef("b"), true), new("c", new StoreRef("c"), false)]);

        Assert.Equal(["c"], loaded.FindUnused(report).Select(e => e.Name));
    }

    [Fact]
    public void LoadedConfigSource_rejects_missing_arguments()
    {
        Assert.Throws<ArgumentException>(() => new LoadedConfigSource(" ", new StoreResolver([]), []));
        Assert.Throws<ArgumentNullException>(() => new LoadedConfigSource("s", null!, []));
        Assert.Throws<ArgumentNullException>(() => new LoadedConfigSource("s", new StoreResolver([]), null!));
        Assert.Empty(new LoadedConfigSource("s", new StoreResolver([]), []).Warnings);
    }

    [Theory]
    [InlineData("Db__Timeout", null, "Db:Timeout")]
    [InlineData("Db--Timeout", "--", "Db:Timeout")]
    [InlineData("/orders/Db/Timeout", "/", "orders:Db:Timeout")]
    [InlineData("DB_TIMEOUT", null, "DB_TIMEOUT")]
    [InlineData("a__b--c", "", "a:b--c")]
    public void ToConfigurationKey_replaces_separators(string name, string? separator, string expected) =>
        Assert.Equal(expected, LayerNames.ToConfigurationKey(name, separator));

    [Fact]
    public void LayerNames_Of_reads_each_reference_kind()
    {
        static ProvenanceStep With(SourceRef? reference) => new() { Kind = LayerKind.Other, Source = "s", Reference = reference };

        Assert.Equal("A", LayerNames.Of(With(new EnvironmentVariableRef("A"))));
        Assert.Equal("B", LayerNames.Of(With(new WorkloadEnvRef("w", "c", "B"))));
        Assert.Equal("C", LayerNames.Of(With(new ConfigMapKeyRef("cm", "C"))));
        Assert.Equal("D", LayerNames.Of(With(new SecretKeyRef("s", "D"))));
        Assert.Equal("prod/db", LayerNames.Of(With(new ExternalSecretRef("aws", "store", "prod/db", "password"))));
        Assert.Null(LayerNames.Of(With(new FileRef("/x"))));
        Assert.Null(LayerNames.Of(With(null)));
    }

    [Fact]
    public async Task WithSource_checks_arguments()
    {
        var tracer = ConfigTracer.For(new ConfigurationBuilder().Build());
        Assert.Throws<ArgumentNullException>(() => ConfigSourceTracerExtensions.WithSource(null!, new LoadedConfigSource("s", new StoreResolver([]), [])));
        Assert.Throws<ArgumentNullException>(() => tracer.WithSource(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => tracer.WithSourceAsync(null!, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() => LayerNames.Of(null!));
        Assert.Throws<ArgumentNullException>(() => LayerNames.ToConfigurationKey(null!));
    }
}
