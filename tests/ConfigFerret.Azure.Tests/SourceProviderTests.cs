using ConfigFerret.Sources;

namespace ConfigFerret.Azure.Tests;

public class SourceProviderTests
{
    private static Dictionary<string, string?> Values(params (string Key, string? Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);

    [Fact]
    public void Key_Vault_provider_describes_its_settings()
    {
        var provider = new AzureKeyVaultSourceProvider();

        Assert.Equal("azure-key-vault", provider.Id);
        Assert.Equal(AzureKeyVaultSourceProvider.ProviderId, provider.Id);
        Assert.Equal("Azure Key Vault", provider.DisplayName);
        Assert.Equal(SourceCategory.SecretStore, provider.Category);
        Assert.Equal(["VaultUri", "SecretNames", "IncludeValues", "IncludeDisabled", "MatchByName", "TenantId", "ClientId", "ClientSecret"], provider.Settings.Select(s => s.Name));
        Assert.True(provider.Settings[0].Required);
        Assert.Equal(SourceSettingKind.Url, provider.Settings[0].Kind);
        Assert.Equal(["VaultUri"], provider.Settings.Where(s => s.Required).Select(s => s.Name));
        var secret = Assert.Single(provider.Settings, s => s.Kind == SourceSettingKind.Secret);
        Assert.Equal("ClientSecret", secret.Name);
        Assert.Equal(["AZURE_CLIENT_SECRET"], secret.EnvironmentVariables);
        Assert.All(provider.Settings, s => Assert.False(string.IsNullOrWhiteSpace(s.Help)));
        Assert.Equal("false", provider.Settings.Single(s => s.Name == "IncludeValues").DefaultValue);
    }

    [Fact]
    public void Key_Vault_provider_creates_a_source_from_values()
    {
        var source = (AzureKeyVaultSource)new AzureKeyVaultSourceProvider().Create(Values(
            ("VaultUri", " https://contoso-prod.vault.azure.net/ "),
            ("SecretNames", "Db--*\n\n Api--Key \r\n"),
            ("IncludeValues", "True"),
            ("IncludeDisabled", " true "),
            ("MatchByName", "false"),
            ("TenantId", "t"),
            ("ClientId", "c"),
            ("ClientSecret", "s")));

        Assert.Equal("Azure Key Vault contoso-prod", source.DisplayName);
        Assert.Equal(["Db--*", "Api--Key"], source.Options.SecretNames);
        Assert.True(source.Options.IncludeValues);
        Assert.True(source.Options.IncludeDisabled);
        Assert.False(source.TraceOptions.MatchByName);
        Assert.Equal("t", source.Options.TenantId);
        Assert.Equal("c", source.Options.ClientId);
        Assert.Equal("s", source.Options.ClientSecret);

        var defaults = (AzureKeyVaultSource)new AzureKeyVaultSourceProvider().Create(Values(("VaultUri", "https://v.vault.azure.net/"), ("IncludeValues", " "), ("TenantId", " ")));
        Assert.False(defaults.Options.IncludeValues);
        Assert.False(defaults.Options.IncludeDisabled);
        Assert.True(defaults.TraceOptions.MatchByName);
        Assert.Null(defaults.Options.TenantId);
        Assert.Empty(defaults.Options.SecretNames);
    }

    [Theory]
    [InlineData(null, "VaultUri is required.")]
    [InlineData("  ", "VaultUri is required.")]
    [InlineData("not a url", "VaultUri must be an absolute https URL without credentials.")]
    [InlineData("http://v.vault.azure.net/", "VaultUri must be an absolute https URL without credentials.")]
    [InlineData("https://u:p@v.vault.azure.net/", "VaultUri must be an absolute https URL without credentials.")]
    public void Key_Vault_provider_rejects_a_bad_vault_address(string? uri, string message)
    {
        var values = uri is null ? Values() : Values(("VaultUri", uri));

        var ex = Assert.Throws<ArgumentException>(() => new AzureKeyVaultSourceProvider().Create(values));

        Assert.StartsWith(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Key_Vault_provider_rejects_bad_flags_and_a_half_set_service_principal()
    {
        var provider = new AzureKeyVaultSourceProvider();
        var ex = Assert.Throws<ArgumentException>(() => provider.Create(Values(("VaultUri", "https://v.vault.azure.net/"), ("IncludeValues", "yes"))));
        Assert.StartsWith("IncludeValues must be true or false.", ex.Message, StringComparison.Ordinal);

        ex = Assert.Throws<ArgumentException>(() => provider.Create(Values(("VaultUri", "https://v.vault.azure.net/"), ("ClientSecret", "never-printed"))));
        Assert.StartsWith("ClientSecret is set, but a client secret also needs TenantId and ClientId.", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("never-printed", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => provider.Create(null!));
    }

    [Fact]
    public async Task Key_Vault_source_from_the_provider_loads_like_any_source()
    {
        var source = (AzureKeyVaultSource)new AzureKeyVaultSourceProvider().Create(Values(("VaultUri", KeyVaultLoadTests.Vault)));
        var handler = new FakeAzureHandler().Respond("/secrets", KeyVaultLoadTests.Page(null, KeyVaultLoadTests.Item("Db--Password")));
        source.ClientOptions = handler.KeyVaultOptions();
        var direct = new AzureKeyVaultSource(new AzureKeyVaultOptions { VaultUri = new(KeyVaultLoadTests.Vault) }, credential: new FakeCredential()) { ClientOptions = handler.KeyVaultOptions() };

        var loaded = await direct.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Azure Key Vault contoso-prod", loaded.DisplayName);
        var entry = Assert.Single(loaded.Entries);
        Assert.Equal(new SourceEntry("Db--Password", new AzureKeyVaultSecretRef("contoso-prod", "Db--Password"), true, "content type text/plain", "contoso-prod"), entry);
        Assert.NotNull(source.TraceOptions);
        Assert.Throws<ArgumentNullException>(() => new AzureKeyVaultSource(null!));
    }

    [Fact]
    public void App_Configuration_provider_describes_its_settings()
    {
        var provider = new AzureAppConfigurationSourceProvider();

        Assert.Equal("azure-app-configuration", provider.Id);
        Assert.Equal(AzureAppConfigurationSourceProvider.ProviderId, provider.Id);
        Assert.Equal("Azure App Configuration", provider.DisplayName);
        Assert.Equal(SourceCategory.ConfigurationService, provider.Category);
        Assert.Equal(["Endpoint", "KeyFilter", "Labels", "TrimKeyPrefixes", "IncludeKeyVaultReferences", "IncludeFeatureFlags", "FeatureFlagLabels", "MatchByName", "TenantId", "ClientId", "ClientSecret", "ConnectionString"], provider.Settings.Select(s => s.Name));
        Assert.DoesNotContain(provider.Settings, s => s.Required);
        var secrets = provider.Settings.Where(s => s.Kind == SourceSettingKind.Secret).ToList();
        Assert.Equal(["ClientSecret", "ConnectionString"], secrets.Select(s => s.Name));
        Assert.Equal(["AZURE_CLIENT_SECRET"], secrets[0].EnvironmentVariables);
        Assert.Equal(["AZURE_APPCONFIG_CONNECTION_STRING"], secrets[1].EnvironmentVariables);
        Assert.Equal(SourceSettingKind.Lines, provider.Settings.Single(s => s.Name == "Labels").Kind);
        Assert.All(provider.Settings, s => Assert.False(string.IsNullOrWhiteSpace(s.Help)));
    }

    [Fact]
    public void App_Configuration_provider_creates_a_source_from_values()
    {
        var source = (AzureAppConfigurationSource)new AzureAppConfigurationSourceProvider().Create(Values(
            ("Endpoint", "https://contoso.azconfig.io"),
            ("KeyFilter", " Orders:* "),
            ("Labels", "base\nprod"),
            ("TrimKeyPrefixes", "Orders:"),
            ("IncludeKeyVaultReferences", "false"),
            ("IncludeFeatureFlags", "false"),
            ("FeatureFlagLabels", "flags\nprod"),
            ("MatchByName", "false"),
            ("TenantId", "t"),
            ("ClientId", "c"),
            ("ClientSecret", "s")));

        Assert.Equal("Azure App Configuration contoso", source.DisplayName);
        Assert.Equal("Orders:*", source.Options.KeyFilter);
        Assert.Equal(["base", "prod"], source.Options.Labels);
        Assert.Equal(["Orders:"], source.Options.TrimKeyPrefixes);
        Assert.False(source.Options.IncludeKeyVaultReferences);
        Assert.False(source.Options.IncludeFeatureFlags);
        Assert.Equal(["flags", "prod"], source.Options.FeatureFlagLabels);
        Assert.False(source.TraceOptions.MatchByName);
        Assert.Equal("s", source.Options.ClientSecret);
        Assert.Equal("t", source.Options.TenantId);
        Assert.Equal("c", source.Options.ClientId);

        var fromConnection = (AzureAppConfigurationSource)new AzureAppConfigurationSourceProvider().Create(Values(("ConnectionString", AppConfigurationLoadTests.ConnectionString)));
        Assert.Equal("Azure App Configuration contoso", fromConnection.DisplayName);
        Assert.Null(fromConnection.Options.Endpoint);
        Assert.True(fromConnection.Options.IncludeKeyVaultReferences);
        Assert.True(fromConnection.Options.IncludeFeatureFlags);
        Assert.Empty(fromConnection.Options.FeatureFlagLabels);
        Assert.True(fromConnection.TraceOptions.MatchByName);
    }

    [Theory]
    [InlineData(null, null, "Endpoint is not set.")]
    [InlineData("https://contoso.azconfig.io", AppConfigurationLoadTests.ConnectionString, "Both Endpoint and ConnectionString are set.")]
    [InlineData(null, "Id=x;Secret=" + AppConfigurationLoadTests.AccessKey, "ConnectionString must hold an https Endpoint=... part.")]
    [InlineData("http://contoso.azconfig.io", null, "Endpoint must be an absolute https URL without credentials.")]
    public void App_Configuration_provider_rejects_a_missing_or_bad_target(string? endpoint, string? connectionString, string message)
    {
        var ex = Assert.Throws<ArgumentException>(() => new AzureAppConfigurationSourceProvider().Create(Values(("Endpoint", endpoint), ("ConnectionString", connectionString))));

        Assert.StartsWith(message, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(AppConfigurationLoadTests.AccessKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void App_Configuration_provider_rejects_bad_flags_and_a_half_set_service_principal()
    {
        var provider = new AzureAppConfigurationSourceProvider();
        Assert.StartsWith("IncludeFeatureFlags must be true or false.", Assert.Throws<ArgumentException>(() => provider.Create(Values(("Endpoint", "https://c.azconfig.io"), ("IncludeFeatureFlags", "1")))).Message, StringComparison.Ordinal);
        Assert.StartsWith("ClientSecret is set", Assert.Throws<ArgumentException>(() => provider.Create(Values(("Endpoint", "https://c.azconfig.io"), ("ClientSecret", "x"), ("TenantId", "t")))).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => provider.Create(null!));
    }

    [Fact]
    public async Task App_Configuration_source_from_the_provider_loads_with_the_connection_string()
    {
        var source = (AzureAppConfigurationSource)new AzureAppConfigurationSourceProvider().Create(Values(("ConnectionString", AppConfigurationLoadTests.ConnectionString), ("IncludeFeatureFlags", "false")));
        var handler = new FakeAzureHandler { Challenge = false }.Respond(AppConfigurationLoadTests.KeysPath(), AppConfigurationLoadTests.Page(null, AppConfigurationLoadTests.Kv("A", "1")));
        source.ClientOptions = handler.AppConfigurationOptions();

        var loaded = await source.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new SourceEntry("A", new AzureAppConfigurationSettingRef("contoso", "A", null), false, null, "contoso, no label"), Assert.Single(loaded.Entries));
        Assert.Throws<ArgumentNullException>(() => new AzureAppConfigurationSource(null!));
    }
}
