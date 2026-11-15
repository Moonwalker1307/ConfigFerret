using ConfigFerret.Sources;

namespace ConfigFerret.Azure;

/// <summary>One Azure Key Vault secret: its properties, and its value when it was read.</summary>
/// <param name="Name">The secret name, for example <c>Db--Password</c>.</param>
/// <param name="Vault">The vault name, for example <c>contoso-prod</c>.</param>
/// <param name="Enabled">False when the secret is disabled, so no one can read it.</param>
public sealed record AzureKeyVaultSecret(string Name, string Vault, bool Enabled = true)
{
    /// <summary>The content type set on the secret, for example <c>application/json</c>.</summary>
    public string? ContentType { get; init; }

    /// <summary>When the secret was last changed.</summary>
    public DateTimeOffset? UpdatedOn { get; init; }

    /// <summary>When the secret expires, if it does.</summary>
    public DateTimeOffset? ExpiresOn { get; init; }

    /// <summary>When the secret starts being valid, if set.</summary>
    public DateTimeOffset? NotBefore { get; init; }

    /// <summary>The secret's tags.</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

    /// <summary>The current version. Known only when the value was read, because listing secrets does not return it.</summary>
    public string? Version { get; init; }

    /// <summary>True for a secret Key Vault manages for a certificate.</summary>
    public bool Managed { get; init; }

    /// <summary>The current value, only when <see cref="AzureKeyVaultOptions.IncludeValues"/> was on. Always sensitive.</summary>
    public string? Value { get; init; }

    /// <summary>The configuration key the Key Vault configuration provider maps the name to: <c>Db--Password</c> becomes <c>Db:Password</c>.</summary>
    public string ConfigurationKey => LayerNames.ToConfigurationKey(Name, "--");

    /// <summary>Describes the secret without its value, so it is safe to log.</summary>
    public override string ToString() => $"Azure Key Vault secret {Name} ({Vault})";
}

/// <summary>A pointer to an Azure Key Vault secret.</summary>
/// <param name="Vault">The vault name, for example <c>contoso-prod</c>.</param>
/// <param name="Name">The secret name.</param>
public sealed record AzureKeyVaultSecretRef(string Vault, string Name) : SourceRef;
