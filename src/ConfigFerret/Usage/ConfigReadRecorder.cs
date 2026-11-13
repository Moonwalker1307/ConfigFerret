using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace ConfigFerret;

/// <summary>
/// Records which configuration keys the app reads while it runs, so <see cref="ProvenanceReport.FindUnused(ConfigReadRecorder)"/>
/// can list the configured keys it never read. Create one with
/// <see cref="ConfigReadRecorderExtensions.RecordReads(IConfigurationBuilder)"/>.
/// </summary>
/// <remarks>
/// A read is recorded whenever a provider is asked for a key: through <c>configuration["X"]</c>, <c>GetValue</c>,
/// <c>GetSection("X").Value</c>, or options binding (<c>Bind</c>, <c>Get&lt;T&gt;</c>, <c>Configure&lt;T&gt;</c>), which
/// reads the keys that match properties of the options type. Code that walks every key, such as
/// <c>AsEnumerable()</c> or <c>GetDebugView()</c>, marks every key as read. ConfigFerret's own tracing is not recorded.
/// </remarks>
public sealed class ConfigReadRecorder
{
    [ThreadStatic]
    private static int t_paused;

    private readonly ConcurrentDictionary<string, byte> _keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every key read so far, sorted, case-insensitive, with <c>:</c> as the separator. Includes keys that had no value.</summary>
    public IReadOnlyList<string> ReadKeys => [.. _keys.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>True when <paramref name="key"/> was read (case-insensitive). Accepts <c>:</c> or <c>__</c> as the separator.</summary>
    public bool WasRead(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _keys.ContainsKey(Normalize(key));
    }

    /// <summary>Forgets every recorded read, for example after startup to see only what a request reads.</summary>
    public void Clear() => _keys.Clear();

    /// <summary>Adds keys recorded elsewhere, for example by an earlier run saved with <see cref="ReadKeys"/>.</summary>
    public void Add(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var key in keys.Where(k => !string.IsNullOrEmpty(k)))
        {
            _keys.TryAdd(Normalize(key), 0);
        }
    }

    internal void Record(string key)
    {
        if (t_paused == 0)
        {
            _keys.TryAdd(Normalize(key), 0);
        }
    }

    // FindUnused and WasRead treat __ as the separator too, so store one spelling and every lookup agrees.
    private static string Normalize(string key) => key.Replace("__", ":", StringComparison.Ordinal);

    /// <summary>Stops recording on this thread until the returned scope is disposed. Used while tracing.</summary>
    internal static PauseScope Pause()
    {
        t_paused++;
        return default;
    }

    internal readonly struct PauseScope : IDisposable
    {
        public void Dispose() => t_paused--;
    }
}

/// <summary>Turns on read recording for a configuration builder.</summary>
public static class ConfigReadRecorderExtensions
{
    /// <summary>
    /// Records every key the app reads from the sources already added to <paramref name="builder"/>. Call it after the
    /// last source is added: sources added later are not recorded. Works with <c>builder.Configuration</c> in ASP.NET Core.
    /// </summary>
    /// <example>
    /// <code>
    /// var builder = WebApplication.CreateBuilder(args);
    /// var reads = builder.Configuration.RecordReads();
    /// // ... run the app and exercise it ...
    /// Console.WriteLine(builder.Configuration.TraceProvenance().FindUnused(reads).ToText());
    /// </code>
    /// </example>
    public static ConfigReadRecorder RecordReads(this IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var recorder = new ConfigReadRecorder();
        var sources = builder.Sources;
        for (var i = 0; i < sources.Count; i++)
        {
            var inner = sources[i] is RecordingConfigurationSource wrapped ? wrapped.Inner : sources[i];
            // Assigning through the indexer lets ConfigurationManager rebuild its providers.
            sources[i] = new RecordingConfigurationSource(inner, recorder);
        }

        return recorder;
    }
}

internal sealed class RecordingConfigurationSource(IConfigurationSource inner, ConfigReadRecorder recorder) : IConfigurationSource
{
    public IConfigurationSource Inner { get; } = inner;

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new RecordingConfigurationProvider(Inner.Build(builder), recorder);
}

/// <summary>Passes everything to the wrapped provider and records each key looked up.</summary>
internal sealed class RecordingConfigurationProvider(IConfigurationProvider inner, ConfigReadRecorder recorder)
    : IConfigurationProvider, IDisposable
{
    public IConfigurationProvider Inner { get; } = inner;

    public bool TryGet(string key, out string? value)
    {
        recorder.Record(key);
        return Inner.TryGet(key, out value);
    }

    public void Set(string key, string? value) => Inner.Set(key, value);

    public IChangeToken GetReloadToken() => Inner.GetReloadToken();

    public void Load() => Inner.Load();

    public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath) =>
        Inner.GetChildKeys(earlierKeys, parentPath);

    public void Dispose() => (Inner as IDisposable)?.Dispose();

    public override string ToString() => Inner.ToString() ?? Inner.GetType().Name;
}
