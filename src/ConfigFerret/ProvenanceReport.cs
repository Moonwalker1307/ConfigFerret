namespace ConfigFerret;

/// <summary>The result of a trace: every effective setting with its full history.</summary>
public sealed class ProvenanceReport
{
    private readonly Dictionary<string, TracedSetting> _byKey;

    internal ProvenanceReport(IReadOnlyList<TracedSetting> settings, IReadOnlyList<string> warnings)
    {
        Settings = settings;
        Warnings = warnings;
        _byKey = new Dictionary<string, TracedSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in settings)
        {
            _byKey[s.Key] = s;
        }
    }

    /// <summary>Every setting that has a value, sorted by key.</summary>
    public IReadOnlyList<TracedSetting> Settings { get; }

    /// <summary>Warnings found while tracing.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Finds a setting by key (case-insensitive). Accepts <c>:</c> or <c>__</c> as the separator.</summary>
    public TracedSetting? Find(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _byKey.GetValueOrDefault(key.Replace("__", ":", StringComparison.Ordinal));
    }

    /// <summary>Answers "why is this value what it is?" for one key, as text.</summary>
    public string Explain(string key)
    {
        var setting = Find(key);
        return setting is null
            ? $"{key} is not set by any configuration provider."
            : TextRenderer.Render(setting);
    }

    /// <summary>Renders every setting as text.</summary>
    public string ToText() => TextRenderer.Render(this);

    /// <summary>Renders the report as indented JSON.</summary>
    public string ToJson() => JsonRenderer.Render(this);
}
