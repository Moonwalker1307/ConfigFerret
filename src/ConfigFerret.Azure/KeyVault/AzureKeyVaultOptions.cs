namespace ConfigFerret.Azure;

/// <summary>
/// Which Key Vault to read, what to read from it, and how to sign in. Can be bound from a configuration section, see
/// <c>services.AddConfigFerretAzureKeyVault(...)</c>.
/// </summary>
public sealed class AzureKeyVaultOptions
{
    /// <summary>The vault address, for example <c>https://contoso-prod.vault.azure.net/</c>.</summary>
    public Uri? VaultUri { get; set; }

    /// <summary>
    /// The secrets to read, by name (Key Vault compares names case-insensitively). An entry that ends in <c>*</c> is a
    /// prefix, for example <c>Orders--*</c>. Empty (the default) reads every secret.
    /// </summary>
    public IList<string> SecretNames { get; } = [];

    /// <summary>
    /// Also read each secret's current value, so a report can say whether it differs from the value the app sees.
    /// Off by default: without it only names and metadata are read, which needs only the "list" permission. Values are
    /// always marked sensitive and redacted unless the report reveals sensitive values.
    /// </summary>
    public bool IncludeValues { get; set; }

    /// <summary>Also list disabled secrets. Off by default, since the app cannot read them.</summary>
    public bool IncludeDisabled { get; set; }

    /// <summary>The Microsoft Entra tenant to sign in to. Optional.</summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// The application (client) ID. With <see cref="ClientSecret"/> it signs in as that service principal; without it,
    /// it picks the user-assigned managed identity or workload identity to use. Optional.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// A service principal's client secret. Optional: without it, <c>DefaultAzureCredential</c> signs in (managed
    /// identity, workload identity, environment, Azure CLI and so on). Keep it out of appsettings files; startup fails if
    /// it is in one.
    /// </summary>
    public string? ClientSecret { get; set; }
}
