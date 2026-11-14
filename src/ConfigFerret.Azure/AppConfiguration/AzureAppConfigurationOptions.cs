namespace ConfigFerret.Azure;

/// <summary>
/// Which App Configuration store to read, what to read from it, and how to sign in. Can be bound from a configuration
/// section, see <c>services.AddConfigFerretAzureAppConfiguration(...)</c>.
/// </summary>
public sealed class AzureAppConfigurationOptions
{
    /// <summary>The store address, for example <c>https://contoso.azconfig.io</c>. Set this or <see cref="ConnectionString"/>.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>
    /// A connection string with an access key (<c>Endpoint=...;Id=...;Secret=...</c>), instead of <see cref="Endpoint"/> and
    /// a Microsoft Entra sign-in. It is a credential: keep it out of appsettings files; startup fails if it is in one.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// The keys to read, as an App Configuration key filter, for example <c>Orders:*</c>. Null (the default) reads every
    /// key, like the .NET provider's <c>Select(KeyFilter.Any)</c>.
    /// </summary>
    public string? KeyFilter { get; set; }

    /// <summary>
    /// The labels to read, in the order the app's <c>Select</c> calls read them: a key with several labels takes the value
    /// of the last label. An empty entry or <c>\0</c> means no label. Empty (the default) reads only keys with no label.
    /// </summary>
    public IList<string> Labels { get; } = [];

    /// <summary>
    /// Key prefixes the app trims, like the .NET provider's <c>TrimKeyPrefix</c>, so <c>Orders:Db:Timeout</c> is traced as
    /// <c>Db:Timeout</c>. The first prefix that matches is trimmed.
    /// </summary>
    public IList<string> TrimKeyPrefixes { get; } = [];

    /// <summary>
    /// Also read Key Vault references (default true). They are shown as sensitive, with the secret's URI in the note; the
    /// secret itself is never read here. Add the Key Vault source to link them to the secret.
    /// </summary>
    public bool IncludeKeyVaultReferences { get; set; } = true;

    /// <summary>Also read feature flags (default true), which the .NET provider maps to <c>FeatureManagement:&lt;name&gt;</c>.</summary>
    public bool IncludeFeatureFlags { get; set; } = true;

    /// <summary>The Microsoft Entra tenant to sign in to. Optional.</summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// The application (client) ID. With <see cref="ClientSecret"/> it signs in as that service principal; without it,
    /// it picks the user-assigned managed identity or workload identity to use. Optional.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// A service principal's client secret. Optional: without it, <c>DefaultAzureCredential</c> signs in. Keep it out of
    /// appsettings files; startup fails if it is in one.
    /// </summary>
    public string? ClientSecret { get; set; }
}
