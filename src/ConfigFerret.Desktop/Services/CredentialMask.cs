using System.Buffers;

namespace ConfigFerret.Desktop.Services;

/// <summary>
/// Hides the values the secrets audit takes for a credential by their shape (<see cref="SecretAudit.LooksLikeCredential"/>),
/// which a trace hides only by key name or source. Made from a trace with the values revealed, it hides them in a trace
/// without: a setting that holds one in any of its layers, also inside a longer value, is hidden as a whole, like a
/// setting whose key looks secret, and a note or warning that holds one is replaced as a whole by a fixed sentence. The
/// values are searched for all at once, so each text is searched once, in a time that grows with its length, not with
/// the number of values.
/// </summary>
internal sealed class CredentialMask
{
    /// <summary>What a note, warning or problem that holds a credential reads instead.</summary>
    public const string HiddenText = "This note is hidden because it holds a value that looks like a credential. Turn on Reveal secrets to see it.";

    private readonly SearchValues<string> _search;
    private readonly string _redacted;

    private CredentialMask(string[] values, string redacted)
    {
        _search = SearchValues.Create(values, StringComparison.Ordinal);
        _redacted = redacted;
    }

    /// <summary>A mask for the credentials in <paramref name="revealed"/>, a trace with the values revealed; null when it holds none.</summary>
    public static CredentialMask? From(ProvenanceReport revealed, string redactedText)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setting in revealed.Settings)
        {
            Add(setting.Value);
            foreach (var step in setting.Steps)
            {
                AddAll(step);
            }
        }

        return values.Count == 0 ? null : new CredentialMask([.. values], redactedText);

        void AddAll(ProvenanceStep step)
        {
            Add(step.Value);
            foreach (var origin in step.Origins)
            {
                AddAll(origin);
            }
        }

        void Add(string? value)
        {
            if (SecretAudit.LooksLikeCredential(value))
            {
                values.Add(value!.Trim());
            }
        }
    }

    /// <summary>True when <paramref name="text"/> holds one of the credentials.</summary>
    public bool Holds(string? text) => text is not null && text.AsSpan().ContainsAny(_search);

    /// <summary>
    /// <paramref name="text"/>, or <see cref="HiddenText"/> when it holds a credential. The whole text goes, so nothing of
    /// two credentials that overlap is left.
    /// </summary>
    public string Scrub(string text) => Holds(text) ? HiddenText : text;

    /// <summary><paramref name="report"/> with every setting that holds a credential hidden, and every warning that holds one replaced.</summary>
    public ProvenanceReport Hide(ProvenanceReport report)
    {
        var settings = report.Settings.Select(s => Holds(s.Value) || s.Steps.Any(HoldsAny) ? HideAll(s) : s).ToList();
        return report.With(settings, report.Warnings.Select(Scrub).ToList());
    }

    private bool HoldsAny(ProvenanceStep step) => Holds(step.Value) || step.Origins.Any(HoldsAny);

    private TracedSetting HideAll(TracedSetting setting) => setting with
    {
        Value = setting.Value is null ? null : _redacted,
        Steps = setting.Steps.Select(HideAll).ToList(),
        IsSensitive = true,
    };

    private ProvenanceStep HideAll(ProvenanceStep step) => step with
    {
        Value = step.Value is null ? null : _redacted,
        IsSensitive = true,
        Origins = step.Origins.Select(HideAll).ToList(),
    };
}
