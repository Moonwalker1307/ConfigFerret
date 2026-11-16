using System.Text.RegularExpressions;

namespace ConfigFerret;

/// <summary>Options for a trace.</summary>
public sealed class TraceOptions
{
    /// <summary>
    /// The default pattern for keys whose values are treated as secret. Words such as <c>password</c>, <c>token</c> and
    /// <c>connectionstring</c> count anywhere, also with <c>_</c> or <c>-</c> between their parts
    /// (<c>DB_CONNECTION_STRING</c>). Short words such as <c>pass</c>, <c>key</c>, <c>sas</c> and <c>dsn</c> count only
    /// as a whole word: a key segment (<c>Db:Pass</c>, <c>Jwt:Key</c>, <c>DB_PASS</c>) or the last word of a PascalCase
    /// or camelCase name (<c>JwtKey</c>, <c>smtpPass</c>, <c>SentryDsn</c>). So <c>Cache:KeyPrefix</c>,
    /// <c>KeyVaultName</c> and <c>PassThrough</c> stay visible, and so do <c>PartitionKey</c>, <c>RoutingKey</c> and
    /// <c>PublicKey</c>, which are not secret.
    /// </summary>
    public const string DefaultSensitiveKeyPattern =
        @"(password|passwd|pwd|passphrase|secret|token|api[_-]?key|connection[_-]?string|conn[_-]?str|credential|private[_-]?key|access[_-]?key"
        + @"|shared[_-]?access|(signing|encryption|master|shared|account|storage|hmac|subscription)_?key|bearer"
        + @"|(^|[^a-z])(pass|key|sas|dsn)($|[^a-z])"
        + @"|(?-i:((?<![Pp]artition|[Rr]outing|[Pp]ublic)Key|Pass|Sas|Dsn)(?![A-Za-z]))"
        + @"|((?<![a-z])sas|(?-i:Sas))(?-i:Ur[il](?![A-Za-z])))";

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
