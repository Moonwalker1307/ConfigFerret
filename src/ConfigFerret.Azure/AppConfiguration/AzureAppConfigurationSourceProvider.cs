using ConfigFerret.Sources;

namespace ConfigFerret.Azure;

/// <summary>Describes the Azure App Configuration source to hosts such as the desktop app and the command-line tool.</summary>
public sealed class AzureAppConfigurationSourceProvider : IConfigSourceProvider
{
    /// <summary>The provider's id.</summary>
    public const string ProviderId = "azure-app-configuration";

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public string DisplayName => "Azure App Configuration";

    /// <inheritdoc />
    public SourceCategory Category => SourceCategory.ConfigurationService;

    /// <inheritdoc />
    public IReadOnlyList<SourceSetting> Settings { get; } =
    [
        new(nameof(AzureAppConfigurationOptions.Endpoint), "Endpoint", SourceSettingKind.Url, Help: "The store address, for example https://contoso.azconfig.io. Leave empty to use a connection string."),
        new(nameof(AzureAppConfigurationOptions.KeyFilter), "Key filter", Help: "Keys to read, for example Orders:*. Empty reads every key."),
        new(nameof(AzureAppConfigurationOptions.Labels), "Labels", SourceSettingKind.Lines, Help: "Labels to read, one per line, in the order the app selects them; the last one wins. Empty reads keys with no label."),
        new(nameof(AzureAppConfigurationOptions.TrimKeyPrefixes), "Trimmed key prefixes", SourceSettingKind.Lines, Help: "Key prefixes the app trims (TrimKeyPrefix), one per line."),
        new(nameof(AzureAppConfigurationOptions.IncludeKeyVaultReferences), "Include Key Vault references", SourceSettingKind.Boolean, Help: "Also list Key Vault references. The secrets themselves are not read.", DefaultValue: "true"),
        new(nameof(AzureAppConfigurationOptions.IncludeFeatureFlags), "Include feature flags", SourceSettingKind.Boolean, Help: "Also list feature flags, as FeatureManagement:<name>.", DefaultValue: "true"),
        new(nameof(AzureAppConfigurationOptions.FeatureFlagLabels), "Feature flag labels", SourceSettingKind.Lines, Help: "Labels to read feature flags with, one per line, as the app gives them to UseFeatureFlags. Empty reads flags with no label, whatever the labels above."),
        new(nameof(AzureAppConfigurationTraceOptions.MatchByName), "Match by name", SourceSettingKind.Boolean, Help: "Link environment variables and ConfigMap or Secret keys to key-values with the same key.", DefaultValue: "true"),
        new(nameof(AzureAppConfigurationOptions.TenantId), "Tenant ID", Help: "The Microsoft Entra tenant. Optional."),
        new(nameof(AzureAppConfigurationOptions.ClientId), "Client ID", Help: "A service principal, or the managed or workload identity to use. Optional."),
        new(nameof(AzureAppConfigurationOptions.ClientSecret), "Client secret", SourceSettingKind.Secret, Help: "A service principal's secret. Leave empty to sign in with DefaultAzureCredential (Azure CLI, managed identity and so on).", EnvironmentVariables: ["AZURE_CLIENT_SECRET"]),
        new(nameof(AzureAppConfigurationOptions.ConnectionString), "Connection string", SourceSettingKind.Secret, Help: "A connection string with an access key, instead of an endpoint and a Microsoft Entra sign-in.", EnvironmentVariables: ["AZURE_APPCONFIG_CONNECTION_STRING"]),
    ];

    /// <inheritdoc />
    public IConfigSource Create(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var options = new AzureAppConfigurationOptions
        {
            Endpoint = AzureAuth.ReadUrl(values, nameof(AzureAppConfigurationOptions.Endpoint), required: false),
            ConnectionString = AzureAuth.ReadText(values, nameof(AzureAppConfigurationOptions.ConnectionString)),
            KeyFilter = AzureAuth.ReadText(values, nameof(AzureAppConfigurationOptions.KeyFilter)),
            IncludeKeyVaultReferences = AzureAuth.ReadBoolean(values, nameof(AzureAppConfigurationOptions.IncludeKeyVaultReferences), true),
            IncludeFeatureFlags = AzureAuth.ReadBoolean(values, nameof(AzureAppConfigurationOptions.IncludeFeatureFlags), true),
            TenantId = AzureAuth.ReadText(values, nameof(AzureAppConfigurationOptions.TenantId)),
            ClientId = AzureAuth.ReadText(values, nameof(AzureAppConfigurationOptions.ClientId)),
            ClientSecret = AzureAuth.ReadText(values, nameof(AzureAppConfigurationOptions.ClientSecret)),
        };
        foreach (var label in AzureAuth.ReadLines(values, nameof(AzureAppConfigurationOptions.Labels)))
        {
            options.Labels.Add(label);
        }

        foreach (var label in AzureAuth.ReadLines(values, nameof(AzureAppConfigurationOptions.FeatureFlagLabels)))
        {
            options.FeatureFlagLabels.Add(label);
        }

        foreach (var prefix in AzureAuth.ReadLines(values, nameof(AzureAppConfigurationOptions.TrimKeyPrefixes)))
        {
            options.TrimKeyPrefixes.Add(prefix);
        }

        if (AzureAppConfigurationOptionsValidator.CheckTarget(options, nameof(options.Endpoint), nameof(options.ConnectionString)) is { } target)
        {
            throw new ArgumentException(target, nameof(values));
        }

        if (AzureAuth.Check(options.TenantId, options.ClientId, options.ClientSecret, nameof(options.ClientSecret)) is { } problem)
        {
            throw new ArgumentException(problem, nameof(values));
        }

        var trace = new AzureAppConfigurationTraceOptions { MatchByName = AzureAuth.ReadBoolean(values, nameof(AzureAppConfigurationTraceOptions.MatchByName), true) };
        return new AzureAppConfigurationSource(options, trace);
    }
}
