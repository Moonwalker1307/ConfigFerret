namespace ConfigFerret;

/// <summary>The result of a trace: every effective setting with its full history.</summary>
public sealed class ProvenanceReport
{
    private readonly Dictionary<string, TracedSetting> _byKey;

    // .NET configuration keys ignore case, but a container's environment variable names do not, so a container trace
    // passes an ordinal comparer.
    internal ProvenanceReport(IReadOnlyList<TracedSetting> settings, IReadOnlyList<string> warnings, StringComparer? keyComparer = null)
    {
        Settings = settings;
        Warnings = warnings;
        _byKey = new Dictionary<string, TracedSetting>(keyComparer ?? StringComparer.OrdinalIgnoreCase);
        foreach (var s in settings)
        {
            _byKey[s.Key] = s;
        }
    }

    /// <summary>Every setting that has a value, sorted by key.</summary>
    public IReadOnlyList<TracedSetting> Settings { get; }

    /// <summary>Warnings found while tracing.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// Finds a setting by key. Accepts <c>:</c> or <c>__</c> as the separator. Keys are case-insensitive, except in a
    /// trace started with <see cref="ConfigTracer.ForContainer"/>, whose keys are environment variable names.
    /// </summary>
    public TracedSetting? Find(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _byKey.GetValueOrDefault(key) ?? _byKey.GetValueOrDefault(key.Replace("__", ":", StringComparison.Ordinal));
    }

    /// <summary>Answers "why is this value what it is?" for one key, as text.</summary>
    public string Explain(string key)
    {
        var setting = Find(key);
        return setting is null
            ? $"{key} is not set by any configuration provider."
            : TextRenderer.Render(setting);
    }

    /// <summary>
    /// Lists values that never win in this trace because a later layer always overrides or removes them.
    /// The findings are possibly unused, not safe to delete.
    /// </summary>
    public UnusedReport FindUnused() => UnusedReport.Create(this, null);

    /// <summary>
    /// Lists values that never win, and the configured keys that <paramref name="reads"/> did not see the app read.
    /// The findings are possibly unused, not safe to delete.
    /// </summary>
    public UnusedReport FindUnused(ConfigReadRecorder reads)
    {
        ArgumentNullException.ThrowIfNull(reads);
        return UnusedReport.Create(this, reads.ReadKeys);
    }

    /// <summary>
    /// Lists values that never win, and the configured keys missing from <paramref name="readKeys"/>, for example
    /// keys saved from <see cref="ConfigReadRecorder.ReadKeys"/> over several runs. Accepts <c>:</c> or <c>__</c> as the separator.
    /// </summary>
    public UnusedReport FindUnused(IEnumerable<string> readKeys)
    {
        ArgumentNullException.ThrowIfNull(readKeys);
        return UnusedReport.Create(this, readKeys);
    }

    /// <summary>Renders every setting as text.</summary>
    public string ToText() => TextRenderer.Render(this);

    /// <summary>Renders the report as indented JSON.</summary>
    public string ToJson() => JsonRenderer.Render(this);
}
