using ConfigFerret.Sources;

namespace ConfigFerret.Azure;

/// <summary>Describes the Azure Key Vault source to hosts such as the desktop app and the command-line tool.</summary>
public sealed class AzureKeyVaultSourceProvider : IConfigSourceProvider
{
    /// <summary>The provider's id.</summary>
    public const string ProviderId = "azure-key-vault";

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public string DisplayName => "Azure Key Vault";

    /// <inheritdoc />
    public SourceCategory Category => SourceCategory.SecretStore;

    /// <inheritdoc />
    public IReadOnlyList<SourceSetting> Settings { get; } =
    [
        new(nameof(AzureKeyVaultOptions.VaultUri), "Vault URL", SourceSettingKind.Url, Required: true, Help: "The vault address, for example https://contoso-prod.vault.azure.net/."),
        new(nameof(AzureKeyVaultOptions.SecretNames), "Secret names", SourceSettingKind.Lines, Help: "Secrets to read, one per line; end a line with * for a prefix. Empty reads every secret."),
        new(nameof(AzureKeyVaultOptions.IncludeValues), "Read values", SourceSettingKind.Boolean, Help: "Also read secret values (needs the get permission), to tell whether they differ. Values are always redacted.", DefaultValue: "false"),
        new(nameof(AzureKeyVaultOptions.IncludeDisabled), "Include disabled secrets", SourceSettingKind.Boolean, Help: "Also list disabled secrets.", DefaultValue: "false"),
        new(nameof(AzureKeyVaultTraceOptions.MatchByName), "Match by name", SourceSettingKind.Boolean, Help: "Link environment variables and ConfigMap or Secret keys to secrets with the same name or key.", DefaultValue: "true"),
        new(nameof(AzureKeyVaultOptions.TenantId), "Tenant ID", Help: "The Microsoft Entra tenant. Optional."),
        new(nameof(AzureKeyVaultOptions.ClientId), "Client ID", Help: "A service principal, or the managed or workload identity to use. Optional."),
        new(nameof(AzureKeyVaultOptions.ClientSecret), "Client secret", SourceSettingKind.Secret, Help: "A service principal's secret. Leave empty to sign in with DefaultAzureCredential (Azure CLI, managed identity and so on).", EnvironmentVariables: ["AZURE_CLIENT_SECRET"]),
    ];

    /// <inheritdoc />
    public IConfigSource Create(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var options = new AzureKeyVaultOptions
        {
            VaultUri = AzureAuth.ReadUrl(values, nameof(AzureKeyVaultOptions.VaultUri), required: true),
            IncludeValues = AzureAuth.ReadBoolean(values, nameof(AzureKeyVaultOptions.IncludeValues), false),
            IncludeDisabled = AzureAuth.ReadBoolean(values, nameof(AzureKeyVaultOptions.IncludeDisabled), false),
            TenantId = AzureAuth.ReadText(values, nameof(AzureKeyVaultOptions.TenantId)),
            ClientId = AzureAuth.ReadText(values, nameof(AzureKeyVaultOptions.ClientId)),
            ClientSecret = AzureAuth.ReadText(values, nameof(AzureKeyVaultOptions.ClientSecret)),
        };
        foreach (var name in AzureAuth.ReadLines(values, nameof(AzureKeyVaultOptions.SecretNames)))
        {
            options.SecretNames.Add(name);
        }

        if (AzureAuth.Check(options.TenantId, options.ClientId, options.ClientSecret, nameof(options.ClientSecret)) is { } problem)
        {
            throw new ArgumentException(problem, nameof(values));
        }

        var trace = new AzureKeyVaultTraceOptions { MatchByName = AzureAuth.ReadBoolean(values, nameof(AzureKeyVaultTraceOptions.MatchByName), true) };
        return new AzureKeyVaultSource(options, trace);
    }
}
