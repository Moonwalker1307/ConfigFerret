using Azure.Core;
using Azure.Identity;
using ConfigFerret.Tests.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConfigFerret.Azure.Tests;

public class ServiceCollectionTests
{
    private const string ClientSecret = "client-secret-never-printed";

    private static IConfigurationRoot Configuration(Dictionary<string, string?> settings, Dictionary<string, string?>? secrets = null, Action<IConfigurationBuilder>? more = null)
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(settings);
        more?.Invoke(builder);
        if (secrets is not null)
        {
            builder.AddInMemoryCollection(secrets);
        }

        return builder.Build();
    }

    private static ServiceProvider Services(IConfigurationRoot configuration, Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddConfigFerretAzureKeyVault(configuration.GetSection("ConfigFerret:AzureKeyVault"));
        services.AddConfigFerretAzureAppConfiguration(configuration.GetSection("ConfigFerret:AzureAppConfiguration"));
        more?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static readonly Dictionary<string, string?> Valid = new()
    {
        ["ConfigFerret:AzureKeyVault:VaultUri"] = KeyVaultLoadTests.Vault,
        ["ConfigFerret:AzureKeyVault:SecretNames:0"] = "Db--*",
        ["ConfigFerret:AzureKeyVault:IncludeValues"] = "true",
        ["ConfigFerret:AzureKeyVault:IncludeDisabled"] = "true",
        ["ConfigFerret:AzureKeyVault:TenantId"] = "tenant",
        ["ConfigFerret:AzureKeyVault:ClientId"] = "client",
        ["ConfigFerret:AzureKeyVault:MatchByName"] = "false",
        ["ConfigFerret:AzureKeyVault:MatchConfigurationProvider"] = "false",
        ["ConfigFerret:AzureKeyVault:Links:DB_PASS"] = "Db--Password",
        ["ConfigFerret:AzureAppConfiguration:Endpoint"] = AppConfigurationLoadTests.Endpoint,
        ["ConfigFerret:AzureAppConfiguration:KeyFilter"] = "Orders:*",
        ["ConfigFerret:AzureAppConfiguration:Labels:0"] = "base",
        ["ConfigFerret:AzureAppConfiguration:Labels:1"] = "prod",
        ["ConfigFerret:AzureAppConfiguration:TrimKeyPrefixes:0"] = "Orders:",
        ["ConfigFerret:AzureAppConfiguration:IncludeKeyVaultReferences"] = "false",
        ["ConfigFerret:AzureAppConfiguration:IncludeFeatureFlags"] = "false",
        ["ConfigFerret:AzureAppConfiguration:MatchByName"] = "false",
        ["ConfigFerret:AzureAppConfiguration:Links:TIMEOUT"] = "Orders:Db:Timeout",
    };

    private static string Json(params (string Section, string Key, string Value)[] entries) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ConfigFerret"] = entries.GroupBy(e => e.Section).ToDictionary(g => g.Key, g => g.ToDictionary(e => e.Key, e => e.Value)),
        });

    private static AzureKeyVaultOptions KeyVault(ServiceProvider provider) => provider.GetRequiredService<IOptions<AzureKeyVaultOptions>>().Value;

    private static AzureAppConfigurationOptions AppConfiguration(ServiceProvider provider) => provider.GetRequiredService<IOptions<AzureAppConfigurationOptions>>().Value;

    private static Dictionary<string, string?> With(params (string Key, string? Value)[] changes)
    {
        var settings = new Dictionary<string, string?>(Valid);
        foreach (var (key, value) in changes)
        {
            settings[key] = value;
        }

        return settings;
    }

    [Fact]
    public void Binds_both_sources_from_their_sections()
    {
        using var provider = Services(Configuration(Valid, new() { ["ConfigFerret:AzureKeyVault:ClientSecret"] = ClientSecret }));

        var kv = provider.GetRequiredService<AzureKeyVaultSource>();
        Assert.Equal(new Uri(KeyVaultLoadTests.Vault), kv.Options.VaultUri);
        Assert.Equal(["Db--*"], kv.Options.SecretNames);
        Assert.True(kv.Options.IncludeValues);
        Assert.True(kv.Options.IncludeDisabled);
        Assert.Equal(ClientSecret, kv.Options.ClientSecret);
        Assert.False(kv.TraceOptions.MatchByName);
        Assert.False(kv.TraceOptions.MatchConfigurationProvider);
        Assert.Equal("Db--Password", kv.TraceOptions.Links["db_pass"]);
        Assert.Equal("Azure Key Vault contoso-prod", kv.DisplayName);
        Assert.IsType<ClientSecretCredential>(kv.Credential);

        var ac = provider.GetRequiredService<AzureAppConfigurationSource>();
        Assert.Equal(new Uri(AppConfigurationLoadTests.Endpoint), ac.Options.Endpoint);
        Assert.Equal("Orders:*", ac.Options.KeyFilter);
        Assert.Equal(["base", "prod"], ac.Options.Labels);
        Assert.Equal(["Orders:"], ac.Options.TrimKeyPrefixes);
        Assert.False(ac.Options.IncludeKeyVaultReferences);
        Assert.False(ac.Options.IncludeFeatureFlags);
        Assert.False(ac.TraceOptions.MatchByName);
        Assert.Equal("Orders:Db:Timeout", ac.TraceOptions.Links["TIMEOUT"]);
        Assert.Equal("Azure App Configuration contoso", ac.DisplayName);

        KeyVault(provider);
        AppConfiguration(provider);
    }

    [Fact]
    public void Fails_at_startup_without_an_address()
    {
        using var provider = Services(Configuration(new()));

        var kv = Assert.Throws<OptionsValidationException>(() => KeyVault(provider));
        Assert.Equal(["ConfigFerret:AzureKeyVault:VaultUri is not set. Set it to the vault address, for example https://contoso.vault.azure.net/ in appsettings.json."], kv.Failures);
        var ac = Assert.Throws<OptionsValidationException>(() => AppConfiguration(provider));
        Assert.Equal(["ConfigFerret:AzureAppConfiguration:Endpoint is not set. Set it to the store address, for example https://contoso.azconfig.io in appsettings.json, or set ConfigFerret:AzureAppConfiguration:ConnectionString outside appsettings files."], ac.Failures);
        Assert.Equal("Azure Key Vault", new AzureKeyVaultSource(new AzureKeyVaultOptions()).DisplayName);
        Assert.Equal("Azure Key Vault", new AzureKeyVaultSource(new AzureKeyVaultOptions { VaultUri = new("x", UriKind.Relative) }).DisplayName);
        Assert.Equal("Azure App Configuration", new AzureAppConfigurationSource(new AzureAppConfigurationOptions()).DisplayName);
    }

    [Theory]
    [InlineData("http://contoso.vault.azure.net/")]
    [InlineData("https://u:p@contoso.vault.azure.net/")]
    [InlineData("relative/path")]
    public void Fails_at_startup_when_the_vault_address_is_not_https(string uri)
    {
        using var provider = Services(Configuration(With(("ConfigFerret:AzureKeyVault:VaultUri", uri))));

        var ex = Assert.Throws<OptionsValidationException>(() => KeyVault(provider));
        Assert.Equal(["ConfigFerret:AzureKeyVault:VaultUri must be an absolute https URL without credentials."], ex.Failures);
    }

    [Theory]
    [InlineData("http://contoso.azconfig.io")]
    [InlineData("https://u:p@contoso.azconfig.io")]
    [InlineData("relative")]
    public void Fails_at_startup_when_the_endpoint_is_not_https(string uri)
    {
        using var provider = Services(Configuration(With(("ConfigFerret:AzureAppConfiguration:Endpoint", uri))));

        var ex = Assert.Throws<OptionsValidationException>(() => AppConfiguration(provider));
        Assert.Equal(["ConfigFerret:AzureAppConfiguration:Endpoint must be an absolute https URL without credentials."], ex.Failures);
    }

    [Fact]
    public void Fails_at_startup_when_a_client_secret_is_in_an_appsettings_file_or_incomplete()
    {
        using var folder = new TempSettingsFolder();
        var file = folder.Write("appsettings.Production.json", Json(("AzureKeyVault", "ClientSecret", ClientSecret), ("AzureAppConfiguration", "ClientSecret", ClientSecret)));
        var settings = With(("ConfigFerret:AzureKeyVault:TenantId", null), ("ConfigFerret:AzureAppConfiguration:ClientId", " "));
        using var provider = Services(Configuration(settings, more: b => b.AddJsonFile(file)));

        var kv = Assert.Throws<OptionsValidationException>(() => KeyVault(provider));
        Assert.Equal(2, kv.Failures.Count());
        Assert.StartsWith("ConfigFerret:AzureKeyVault:ClientSecret is set in appsettings.Production.json.", kv.Failures.First(), StringComparison.Ordinal);
        Assert.Equal("ConfigFerret:AzureKeyVault:ClientSecret is set, but a client secret also needs TenantId and ClientId.", kv.Failures.Last());
        var ac = Assert.Throws<OptionsValidationException>(() => AppConfiguration(provider));
        Assert.Equal(2, ac.Failures.Count());
        Assert.DoesNotContain(ClientSecret, kv.Message + ac.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Accepts_a_client_secret_from_user_secrets_or_the_environment()
    {
        using var folder = new TempSettingsFolder();
        var secrets = folder.Write("secrets.json", Json(("AzureKeyVault", "ClientSecret", ClientSecret)));
        using var provider = Services(Configuration(Valid, more: b => b.AddJsonFile(secrets)));

        Assert.Equal(ClientSecret, KeyVault(provider).ClientSecret);
    }

    [Fact]
    public void Fails_at_startup_when_the_connection_string_is_in_an_appsettings_file()
    {
        using var folder = new TempSettingsFolder();
        var file = folder.Write("appsettings.json", Json(("AzureAppConfiguration", "ConnectionString", AppConfigurationLoadTests.ConnectionString)));
        using var provider = Services(Configuration(With(("ConfigFerret:AzureAppConfiguration:Endpoint", null)), more: b => b.AddJsonFile(file)));

        var ex = Assert.Throws<OptionsValidationException>(() => AppConfiguration(provider));

        Assert.StartsWith("ConfigFerret:AzureAppConfiguration:ConnectionString is set in appsettings.json.", Assert.Single(ex.Failures), StringComparison.Ordinal);
        Assert.DoesNotContain(AppConfigurationLoadTests.AccessKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Checks_the_connection_string_and_refuses_both_targets()
    {
        using var both = Services(Configuration(Valid, new() { ["ConfigFerret:AzureAppConfiguration:ConnectionString"] = AppConfigurationLoadTests.ConnectionString }));
        Assert.Equal(["Both ConfigFerret:AzureAppConfiguration:Endpoint and ConfigFerret:AzureAppConfiguration:ConnectionString are set. Set only one."], Assert.Throws<OptionsValidationException>(() => AppConfiguration(both)).Failures);

        using var bad = Services(Configuration(With(("ConfigFerret:AzureAppConfiguration:Endpoint", null)), new() { ["ConfigFerret:AzureAppConfiguration:ConnectionString"] = "Id=x;Secret=" + AppConfigurationLoadTests.AccessKey }));
        var ex = Assert.Throws<OptionsValidationException>(() => AppConfiguration(bad));
        Assert.Equal(["ConfigFerret:AzureAppConfiguration:ConnectionString must hold an https Endpoint=... part."], ex.Failures);
        Assert.Equal("Azure App Configuration", new AzureAppConfigurationSource(new AzureAppConfigurationOptions { ConnectionString = "Id=x" }).DisplayName);
        Assert.Equal("Azure App Configuration", new AzureAppConfigurationSource(new AzureAppConfigurationOptions { Endpoint = new("x", UriKind.Relative) }).DisplayName);

        using var good = Services(Configuration(With(("ConfigFerret:AzureAppConfiguration:Endpoint", null)), new() { ["ConfigFerret:AzureAppConfiguration:ConnectionString"] = AppConfigurationLoadTests.ConnectionString }));
        Assert.Equal(AppConfigurationLoadTests.ConnectionString, AppConfiguration(good).ConnectionString);
        Assert.Equal("Azure App Configuration contoso", good.GetRequiredService<AzureAppConfigurationSource>().DisplayName);
    }

    [Fact]
    public void Validators_check_their_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new AzureKeyVaultOptionsValidator("", null).Validate(null, null!));
        Assert.Throws<ArgumentNullException>(() => new AzureAppConfigurationOptionsValidator("", null).Validate(null, null!));
        Assert.True(new AzureKeyVaultOptionsValidator("", null).Validate(null, new AzureKeyVaultOptions { VaultUri = new(KeyVaultLoadTests.Vault) }).Succeeded);
        var unnamed = new AzureKeyVaultOptionsValidator("", null).Validate(null, new AzureKeyVaultOptions());
        Assert.StartsWith("VaultUri is not set.", unnamed.FailureMessage, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddConfigFerretAzureKeyVault(null!));
        Assert.Throws<ArgumentNullException>(() => AzureServiceCollectionExtensions.AddConfigFerretAzureKeyVault(null!, new ConfigurationBuilder().Build()));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddConfigFerretAzureAppConfiguration(null!));
        Assert.Throws<ArgumentNullException>(() => AzureServiceCollectionExtensions.AddConfigFerretAzureAppConfiguration(null!, new ConfigurationBuilder().Build()));
    }

    [Fact]
    public async Task Loads_App_Configuration_with_a_connection_string_alone_although_a_credential_is_registered()
    {
        using var provider = Services(
            Configuration(With(("ConfigFerret:AzureAppConfiguration:Endpoint", null)), new() { ["ConfigFerret:AzureAppConfiguration:ConnectionString"] = AppConfigurationLoadTests.ConnectionString }),
            s => s.AddSingleton<TokenCredential>(new FakeCredential()));
        var ac = provider.GetRequiredService<AzureAppConfigurationSource>();
        var handler = new FakeAzureHandler { Challenge = false }
            .Respond(AppConfigurationLoadTests.KeysPath("base", "Orders%3A%2A"), AppConfigurationLoadTests.Page(null, AppConfigurationLoadTests.Kv("Orders:Db:Timeout", "30", "base")))
            .Respond(AppConfigurationLoadTests.KeysPath("prod", "Orders%3A%2A"), AppConfigurationLoadTests.Page(null));
        ac.ClientOptions = handler.AppConfigurationOptions();

        var settings = await ac.LoadSettingsAsync(TestContext.Current.CancellationToken);

        Assert.Equal("contoso", settings.Store);
        Assert.Equal("30", Assert.Single(settings.FindByConfigurationKey("Db:Timeout")).Value);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.StartsWith("HMAC-SHA256", r.Headers.Authorization!.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Loads_through_dependency_injection_with_a_registered_credential()
    {
        var credential = new FakeCredential();
        using var provider = Services(Configuration(Valid), s => s.AddSingleton<TokenCredential>(credential));
        var kv = provider.GetRequiredService<AzureKeyVaultSource>();
        Assert.Same(credential, kv.Credential);
        var kvHandler = new FakeAzureHandler()
            .Respond("/secrets", KeyVaultLoadTests.Page(null, KeyVaultLoadTests.Item("Db--Password"), KeyVaultLoadTests.Item("Other")))
            .Respond("/secrets/Db--Password", KeyVaultLoadTests.Value("Db--Password"));
        kv.ClientOptions = kvHandler.KeyVaultOptions();

        var ac = provider.GetRequiredService<AzureAppConfigurationSource>();
        var acHandler = new FakeAzureHandler { Challenge = false }
            .Respond(AppConfigurationLoadTests.KeysPath("base", "Orders%3A%2A"), AppConfigurationLoadTests.Page(null, AppConfigurationLoadTests.Kv("Orders:Db:Timeout", "30", "base")))
            .Respond(AppConfigurationLoadTests.KeysPath("prod", "Orders%3A%2A"), AppConfigurationLoadTests.Page(null, AppConfigurationLoadTests.Kv("Orders:Db:Timeout", "60", "prod")));
        ac.ClientOptions = acHandler.AppConfigurationOptions();

        var configuration = new ConfigurationBuilder().AddEnvironmentVariables("CFAZDI_NONE_").Build();
        var tracer = ConfigTracer.For(configuration);
        Assert.Same(tracer, await tracer.WithAzureKeyVaultAsync(provider, TestContext.Current.CancellationToken));
        Assert.Same(tracer, await tracer.WithAzureAppConfigurationAsync(provider, TestContext.Current.CancellationToken));
        Assert.Equal(["/secrets", "/secrets/Db--Password"], kvHandler.Paths);
        Assert.Equal(2, acHandler.Paths.Count);

        var loadedKv = await ((Sources.IConfigSource)kv).LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["Db--Password"], loadedKv.Entries.Select(e => e.Name));
        var loadedAc = await ac.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["Orders:Db:Timeout", "Orders:Db:Timeout"], loadedAc.Entries.Select(e => e.Name));
        Assert.Equal(new AzureAppConfigurationSettingRef("contoso", "Orders:Db:Timeout", "prod"), loadedAc.Entries[1].Reference);
    }

    [Fact]
    public async Task The_async_extensions_need_the_registration()
    {
        using var empty = new ServiceCollection().BuildServiceProvider();
        var tracer = ConfigTracer.For(new ConfigurationBuilder().Build());
        var ct = TestContext.Current.CancellationToken;

        var kv = await Assert.ThrowsAsync<InvalidOperationException>(() => tracer.WithAzureKeyVaultAsync(empty, ct));
        Assert.Contains("AddConfigFerretAzureKeyVault", kv.Message, StringComparison.Ordinal);
        var ac = await Assert.ThrowsAsync<InvalidOperationException>(() => tracer.WithAzureAppConfigurationAsync(empty, ct));
        Assert.Contains("AddConfigFerretAzureAppConfiguration", ac.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentNullException>(() => AzureTracerExtensions.WithAzureKeyVaultAsync(null!, empty, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => tracer.WithAzureKeyVaultAsync(null!, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => AzureTracerExtensions.WithAzureAppConfigurationAsync(null!, empty, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => tracer.WithAzureAppConfigurationAsync(null!, ct));
    }

    [Fact]
    public void Without_a_secret_signs_in_with_DefaultAzureCredential()
    {
        Assert.IsType<DefaultAzureCredential>(AzureAuth.Create(null, null, null));
        Assert.IsType<DefaultAzureCredential>(AzureAuth.Create(" tenant ", "client", " "));
        Assert.IsType<ClientSecretCredential>(AzureAuth.Create("tenant", "client", "secret"));
        Assert.Throws<ArgumentException>(() => AzureAuth.Create("tenant", null, "secret"));
        Assert.Throws<ArgumentException>(() => AzureAuth.Create(" ", "client", "secret"));
        Assert.Null(AzureAuth.Check("t", "c", "s", "k"));
        Assert.Null(AzureAuth.Check(null, null, " ", "k"));
        Assert.NotNull(AzureAuth.Check("t", null, "s", "k"));
        Assert.NotNull(AzureAuth.Check(null, "c", "s", "k"));
        Assert.IsType<DefaultAzureCredential>(new AzureKeyVaultSource(new AzureKeyVaultOptions()).Credential);
    }
}
