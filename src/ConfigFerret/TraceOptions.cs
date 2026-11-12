using System.Text.RegularExpressions;

namespace ConfigFerret;

/// <summary>Options for a trace.</summary>
public sealed class TraceOptions
{
    /// <summary>The default pattern for keys whose values are treated as secret.</summary>
    public const string DefaultSensitiveKeyPattern =
        @"(password|passwd|pwd|secret|token|apikey|api_key|api-key|connectionstring|credential|privatekey|private_key|accesskey|access_key)";

    /// <summary>
    /// When false (the default), values that come from Secrets, user secrets or key-per-file mounts,
    /// or whose key matches <see cref="SensitiveKeyPattern"/>, are replaced with <c>***</c> in the report.
    /// </summary>
    public bool RevealSensitiveValues { get; set; }

    /// <summary>Keys matching this pattern (case-insensitive) are treated as secret. Set to null to turn off.</summary>
    public Regex? SensitiveKeyPattern { get; set; } = new(DefaultSensitiveKeyPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The environment variables to match against environment variable providers.
    /// Defaults to the current process environment. Set this when tracing another process's environment.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; set; }

    /// <summary>How many layers upstream to follow from each .NET configuration step. Default 8.</summary>
    public int MaxDepth { get; set; } = 8;

    /// <summary>The text used in place of a redacted value.</summary>
    public string RedactedText { get; set; } = "***";
}
