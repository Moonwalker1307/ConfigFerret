namespace ConfigFerret.Azure;

/// <summary>What an App Configuration key-value holds.</summary>
public enum AzureAppConfigurationSettingKind
{
    /// <summary>A plain value.</summary>
    KeyValue,

    /// <summary>A Key Vault reference: the app reads the secret it points to. Always sensitive.</summary>
    KeyVaultReference,

    /// <summary>A feature flag under <c>.appconfig.featureflag/</c>.</summary>
    FeatureFlag,
}

/// <summary>One Azure App Configuration key-value.</summary>
/// <param name="Key">The key as stored, for example <c>Orders:Db:Timeout</c> or <c>.appconfig.featureflag/Beta</c>.</param>
/// <param name="Label">The label, or null for no label.</param>
/// <param name="Store">The store name, for example <c>contoso</c>.</param>
public sealed record AzureAppConfigurationSetting(string Key, string? Label, string Store)
{
    /// <summary>
    /// The configuration key the .NET provider sets: the key with any trimmed prefix removed, or
    /// <c>FeatureManagement:&lt;name&gt;</c> for a feature flag.
    /// </summary>
    public string ConfigurationKey { get; init; } = Key;

    /// <summary>What the key-value holds.</summary>
    public AzureAppConfigurationSettingKind Kind { get; init; }

    /// <summary>
    /// The value. For a feature flag without filters, <c>True</c> or <c>False</c> as the .NET provider sets it. Null for a
    /// Key Vault reference (the value is in the vault) and for a feature flag with filters.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>The content type, for example <c>application/json</c>.</summary>
    public string? ContentType { get; init; }

    /// <summary>For a Key Vault reference, the secret it points to, when the reference could be read.</summary>
    public Uri? SecretId { get; init; }

    /// <summary>For a feature flag, whether it is on.</summary>
    public bool? Enabled { get; init; }

    /// <summary>For a feature flag, the names of its client filters (for example <c>Microsoft.Percentage</c>).</summary>
    public IReadOnlyList<string> Filters { get; init; } = [];

    /// <summary>When the key-value was last changed.</summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>True when the key-value is locked.</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>The key-value's tags.</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

    /// <summary>The label as reports show it: <c>label prod</c>, or <c>no label</c>.</summary>
    public string LabelText => Label is null ? "no label" : $"label {Label}";
}

/// <summary>A pointer to an Azure App Configuration key-value.</summary>
/// <param name="Store">The store name, for example <c>contoso</c>.</param>
/// <param name="Key">The key as stored.</param>
/// <param name="Label">The label, or null for no label.</param>
/// <param name="SecretId">For a Key Vault reference, the secret it points to, so the Key Vault source can link it.</param>
public sealed record AzureAppConfigurationSettingRef(string Store, string Key, string? Label, Uri? SecretId = null) : SourceRef
{
    /// <inheritdoc/>
    protected override string? ValueName() => Key;
}
