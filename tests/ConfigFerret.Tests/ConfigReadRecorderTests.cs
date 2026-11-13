using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace ConfigFerret.Tests;

public class ConfigReadRecorderTests
{
    private sealed class DbOptions
    {
        public int Timeout { get; set; }

        public string Host { get; set; } = "";
    }

    private static readonly Dictionary<string, string?> Settings = new()
    {
        ["Db:Timeout"] = "30",
        ["Db:Host"] = "db",
        ["Db:Legacy"] = "old",
        ["Feature"] = "on",
        ["Unused"] = "x",
    };

    [Fact]
    public void Records_indexer_reads_and_options_binding()
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(Settings);
        var reads = builder.RecordReads();
        var config = builder.Build();

        _ = config["Feature"];
        var db = config.GetSection("Db").Get<DbOptions>()!;

        Assert.Equal(30, db.Timeout);
        Assert.True(reads.WasRead("Feature"));
        Assert.True(reads.WasRead("db__timeout"));
        Assert.True(reads.WasRead("Db:Host"));
        Assert.False(reads.WasRead("Db:Legacy"));
        Assert.False(reads.WasRead("Unused"));

        var unused = config.TraceProvenance().FindUnused(reads);
        Assert.Equal(["Db:Legacy", "Unused"], unused.NotRead!.Select(s => s.Key));
    }

    [Fact]
    public void Does_not_record_its_own_tracing()
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(Settings);
        var reads = builder.RecordReads();
        var config = builder.Build();

        var report = config.TraceProvenance();

        Assert.Equal(5, report.Settings.Count);
        Assert.Empty(reads.ReadKeys);
        _ = config["Unused"];
        Assert.Equal(["Unused"], reads.ReadKeys);
    }

    [Fact]
    public void Still_traces_through_the_recording_providers()
    {
        var env = new Dictionary<string, string?> { ["CFRECTEST_Db__Timeout"] = "42" };
        Environment.SetEnvironmentVariable("CFRECTEST_Db__Timeout", "42");
        var builder = new ConfigurationBuilder().AddInMemoryCollection(Settings).AddEnvironmentVariables("CFRECTEST_");
        builder.RecordReads();
        var config = builder.Build();

        var setting = ConfigTracer.For(config).WithOptions(o => o.Environment = env).Trace().Find("Db:Timeout")!;

        Assert.Equal(["in-memory collection", "environment variable CFRECTEST_Db__Timeout"], setting.Steps.Select(s => s.Source));
    }

    [Fact]
    public void Works_with_ConfigurationManager()
    {
        using var manager = new ConfigurationManager();
        manager.AddInMemoryCollection(Settings);
        var reads = manager.RecordReads();

        Assert.Equal("on", manager["Feature"]);
        Assert.Equal(["Feature"], reads.ReadKeys);
        Assert.Equal("30", manager.TraceProvenance().Find("Db:Timeout")!.Value);
        Assert.Equal(["Feature"], reads.ReadKeys);
    }

    [Fact]
    public void Recording_again_replaces_the_earlier_recorder()
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(Settings);
        var first = builder.RecordReads();
        var second = builder.RecordReads();
        var config = builder.Build();

        _ = config["Feature"];

        Assert.Empty(first.ReadKeys);
        Assert.Equal(["Feature"], second.ReadKeys);
        var source = Assert.IsType<RecordingConfigurationSource>(Assert.Single(builder.Sources));
        Assert.IsType<MemoryConfigurationSource>(source.Inner);
    }

    [Fact]
    public void Records_keys_with_no_value_and_supports_clear_and_add()
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(Settings);
        var reads = builder.RecordReads();
        var config = builder.Build();

        _ = config["Missing:Key"];
        Assert.True(reads.WasRead("Missing:Key"));

        reads.Clear();
        Assert.Empty(reads.ReadKeys);

        reads.Add(["b", "", "A", "a"]);
        Assert.Equal(["A", "b"], reads.ReadKeys);
    }

    [Fact]
    public void Passes_writes_reloads_and_child_keys_through()
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(Settings);
        builder.RecordReads();
        var config = builder.Build();
        using var disposeConfig = (IDisposable)config;

        config["Db:Timeout"] = "99";
        Assert.Equal("99", config["Db:Timeout"]);
        Assert.Equal(["Host", "Legacy", "Timeout"], config.GetSection("Db").GetChildren().Select(c => c.Key).Order());
        Assert.NotNull(config.GetReloadToken());
        var provider = Assert.IsType<RecordingConfigurationProvider>(Assert.Single(config.Providers));
        Assert.Equal(provider.Inner.ToString(), provider.ToString());
        config.Reload();
    }

    [Fact]
    public void Rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => ConfigReadRecorderExtensions.RecordReads(null!));
        var reads = new ConfigurationBuilder().RecordReads();
        Assert.Throws<ArgumentNullException>(() => reads.WasRead(null!));
        Assert.Throws<ArgumentNullException>(() => reads.Add(null!));
        var report = new ConfigurationBuilder().Build().TraceProvenance();
        Assert.Throws<ArgumentNullException>(() => report.FindUnused((ConfigReadRecorder)null!));
        Assert.Throws<ArgumentNullException>(() => report.FindUnused((IEnumerable<string>)null!));
    }

    [Fact]
    public void Agrees_with_FindUnused_on_double_underscore_keys()
    {
        // Found by fuzzing: keys read or added with __ were stored as spelled, so WasRead missed them.
        var builder = new ConfigurationBuilder().AddInMemoryCollection(Settings);
        var reads = builder.RecordReads();
        var config = builder.Build();

        _ = config["Db__Timeout"];
        reads.Add(["Db__Host"]);

        Assert.True(reads.WasRead("Db__Timeout"));
        Assert.True(reads.WasRead("db:timeout"));
        Assert.True(reads.WasRead("Db__Host"));
        Assert.Equal(["Db:Host", "Db:Timeout"], reads.ReadKeys);
        var unused = config.TraceProvenance().FindUnused(reads);
        Assert.DoesNotContain(unused.NotRead!, s => s.Key is "Db:Host" or "Db:Timeout");
    }
}
