using System.Globalization;

namespace ConfigFerret.GoogleCloud;

/// <summary>The state of a Secret Manager secret version.</summary>
public enum GcpSecretVersionState
{
    /// <summary>The server sent a state this package does not know.</summary>
    Unknown,

    /// <summary>The version can be accessed.</summary>
    Enabled,

    /// <summary>The version cannot be accessed, but can be enabled again.</summary>
    Disabled,

    /// <summary>The version's data is gone for good.</summary>
    Destroyed,
}

/// <summary>One version of a Secret Manager secret.</summary>
/// <param name="Id">The version id, for example <c>3</c>.</param>
/// <param name="State">Enabled, disabled or destroyed.</param>
/// <param name="CreateTime">When the version was added, when known.</param>
public sealed record GcpSecretVersion(string Id, GcpSecretVersionState State, DateTimeOffset? CreateTime = null);

/// <summary>
/// One Secret Manager secret. Its <see cref="Value"/> is read only when <see cref="GcpSecretManagerOptions.IncludeValues"/>
/// is on, and <see cref="ToString"/> never shows it.
/// </summary>
public sealed record GcpSecret
{
    /// <summary>The secret id, for example <c>Db__Password</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The project the server named in the secret's resource name: its number or id.</summary>
    public required string Project { get; init; }

    /// <summary>The location of a regional secret, or null for a global one.</summary>
    public string? Location { get; init; }

    /// <summary>The full resource name, for example <c>projects/123/secrets/Db__Password</c>.</summary>
    public string Name => Location is null ? $"projects/{Project}/secrets/{Id}" : $"projects/{Project}/locations/{Location}/secrets/{Id}";

    /// <summary>When the secret was created, when known.</summary>
    public DateTimeOffset? CreateTime { get; init; }

    /// <summary>The secret's labels.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    /// <summary>The secret's annotations.</summary>
    public IReadOnlyDictionary<string, string> Annotations { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// How the secret is replicated: <c>automatic</c>, <c>user-managed (us-east1, europe-west1)</c>, or null when the
    /// server did not say (regional secrets have no replication).
    /// </summary>
    public string? Replication { get; init; }

    /// <summary>The versions read, newest first, or null when versions were not read.</summary>
    public IReadOnlyList<GcpSecretVersion>? Versions { get; init; }

    /// <summary>The id of the newest enabled version, when versions were read and one is enabled.</summary>
    public string? LatestEnabledVersion => Versions?
        .Where(v => v.State == GcpSecretVersionState.Enabled)
        .OrderByDescending(v => long.TryParse(v.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1)
        .Select(v => v.Id)
        .FirstOrDefault();

    /// <summary>The version whose value was read, when it was.</summary>
    public string? ValueVersion { get; init; }

    /// <summary>The value of <see cref="ValueVersion"/> as UTF-8 text. Null when values were not read or the payload is not text. Secret.</summary>
    public string? Value { get; init; }

    /// <summary>True when the payload was read but is not UTF-8 text, so <see cref="Value"/> is null.</summary>
    public bool IsBinary { get; init; }

    /// <summary>Describes the secret without its value.</summary>
    public override string ToString() =>
        $"GcpSecret {{ Name = {Name}, Versions = {Versions?.Count.ToString(CultureInfo.InvariantCulture) ?? "not read"}, Value = {(Value is null ? "not read" : "(redacted)")} }}";
}

/// <summary>A pointer to a Google Cloud Secret Manager secret.</summary>
/// <param name="Project">The project as the server named it (id or number).</param>
/// <param name="Location">The location of a regional secret, or null.</param>
/// <param name="SecretId">The secret id.</param>
public sealed record GcpSecretRef(string Project, string? Location, string SecretId) : SourceRef;
