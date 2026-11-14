using Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static ConfigFerret.Azure.Tests.TestLayers;

namespace ConfigFerret.Azure.Tests;

/// <summary>Edge cases found by mutation testing.</summary>
public class MutationGapTests
{
    [Fact]
    public void Name_and_provider_matches_are_marked_inferred()
    {
        var settings = new AzureAppConfigurationSettings("s", [new("DB_HOST", null, "s") { Value = "db" }]);
        var resolver = new AzureAppConfigurationResolver(settings, new AzureAppConfigurationTraceOptions());
        Assert.True(Assert.Single(resolver.Resolve(Env("DB_HOST"), new TraceDiagnostics())).IsInferred);
        Assert.True(Assert.Single(resolver.Resolve(Provider("AzureAppConfigurationProvider", "DB_HOST"), new TraceDiagnostics())).IsInferred);

        var secrets = new AzureKeyVaultSecrets("v", [new("db-password", "v")]);
        Assert.True(Assert.Single(new AzureKeyVaultResolver(secrets, new AzureKeyVaultTraceOptions()).Resolve(Env("db-password"), new TraceDiagnostics())).IsInferred);
    }

    [Fact]
    public void A_feature_flag_name_needs_something_after_FeatureManagement()
    {
        var settings = new AzureAppConfigurationSettings("s", [new(".appconfig.featureflag/X", null, "s") { Kind = AzureAppConfigurationSettingKind.FeatureFlag, ConfigurationKey = "FeatureManagement:" }]);
        var resolver = new AzureAppConfigurationResolver(settings, new AzureAppConfigurationTraceOptions());

        Assert.Empty(resolver.Resolve(Provider("AzureAppConfigurationProvider", "FeatureManagement::x"), new TraceDiagnostics()));
        Assert.Empty(resolver.Resolve(Provider("AzureAppConfigurationProvider", "FeatureManagement:Beta"), new TraceDiagnostics()));
    }

    [Fact]
    public void Only_plain_key_values_are_compared()
    {
        var flag = new AzureAppConfigurationSetting(".appconfig.featureflag/On", null, "s") { Kind = AzureAppConfigurationSettingKind.FeatureFlag, ConfigurationKey = "FeatureManagement:On", Enabled = true, Value = "True" };
        var reference = new AzureAppConfigurationSetting("Ref", null, "s") { Kind = AzureAppConfigurationSettingKind.KeyVaultReference, Value = "odd" };
        var resolver = new AzureAppConfigurationResolver(new AzureAppConfigurationSettings("s", [flag, reference]), new AzureAppConfigurationTraceOptions());

        Assert.DoesNotContain("differs", Assert.Single(resolver.Resolve(Provider("AzureAppConfigurationProvider", "FeatureManagement:On", "true"), new TraceDiagnostics())).Note, StringComparison.Ordinal);
        var step = Assert.Single(resolver.Resolve(Env("Ref", "other"), new TraceDiagnostics()));
        Assert.DoesNotContain("differs", step.Note, StringComparison.Ordinal);
        Assert.Null(step.Value);
    }

    [Fact]
    public void Snapshots_check_arguments_and_default_warnings()
    {
        Assert.Empty(new AzureAppConfigurationSettings("s", []).Warnings);
        Assert.Empty(new AzureKeyVaultSecrets("v", []).Warnings);
        Assert.Throws<ArgumentNullException>(() => new AzureAppConfigurationSettings("s", [new("k", null, "s")]).Find(null!));
        Assert.Throws<ArgumentNullException>(() => new AzureAppConfigurationSettings("s", [new("k", null, "s")]).FindByConfigurationKey(null!));
        Assert.Throws<ArgumentNullException>(() => new AzureKeyVaultSecrets("v", [new("k", "v")]).FindByConfigurationKey(null!));
        var options = new AzureAppConfigurationTraceOptions { MatchByName = false };
        var loaded = new AzureAppConfigurationSettings("s", [new("A", null, "s") { Value = "1" }]).ToLoadedSource(options);
        Assert.Empty(loaded.Resolver.Resolve(Env("A"), new TraceDiagnostics()));
    }

    [Fact]
    public async Task An_empty_label_from_the_service_means_no_label()
    {
        var handler = new FakeAzureHandler { Challenge = false }.Respond(AppConfigurationLoadTests.KeysPath(), AppConfigurationLoadTests.Page(null, AppConfigurationLoadTests.Kv("A", "1", label: "")));

        var settings = await AppConfigurationLoadTests.Load(handler, o => o.IncludeFeatureFlags = false);

        Assert.Null(Assert.Single(settings.Settings).Label);
    }

    [Fact]
    public async Task Keeps_the_listed_version_when_the_value_response_has_none()
    {
        var handler = new FakeAzureHandler()
            .Respond("/secrets", KeyVaultLoadTests.Page(null, KeyVaultLoadTests.Item("A")))
            .Respond("/secrets/A", """{"value":"x"}""");

        var secrets = await KeyVaultLoadTests.Load(handler, o => o.IncludeValues = true);

        Assert.Equal("x", secrets.Find("A")!.Value);
        Assert.Null(secrets.Find("A")!.Version);
    }

    [Fact]
    public void A_connection_string_endpoint_must_not_hold_credentials()
    {
        Assert.Throws<ArgumentException>(() => AzureAppConfigurationSettings.ReadEndpoint("Endpoint=https://u:p@c.azconfig.io;Id=x;Secret=y"));
        Assert.Equal(new Uri("https://c.azconfig.io"), AzureAppConfigurationSettings.ReadEndpoint(" endpoint = https://c.azconfig.io ;Id=x"));
    }

    [Fact]
    public void Failed_requests_are_described_by_status_and_short_error_code()
    {
        Assert.Equal("status 500", AzureAuth.Describe(new RequestFailedException(500, "boom")));
        Assert.Equal("status 403 (Forbidden)", AzureAuth.Describe(new RequestFailedException(403, "no", "Forbidden", null)));
        Assert.Equal($"status 400 ({new string('x', 100)}...)", AzureAuth.Describe(new RequestFailedException(400, "no", new string('x', 101), null)));
        Assert.Equal($"status 400 ({new string('x', 100)})", AzureAuth.Describe(new RequestFailedException(400, "no", new string('x', 100), null)));
    }

    [Fact]
    public void Multi_line_settings_ignore_a_missing_value()
    {
        Assert.Empty(AzureAuth.ReadLines(new Dictionary<string, string?> { ["Labels"] = null }, "Labels"));
        Assert.Empty(AzureAuth.ReadLines(new Dictionary<string, string?>(), "Labels"));
    }

    [Fact]
    public async Task Load_with_a_credential_checks_it()
    {
        var options = new AzureKeyVaultOptions { VaultUri = new(KeyVaultLoadTests.Vault) };
        await Assert.ThrowsAsync<ArgumentNullException>(() => AzureKeyVaultSecrets.LoadAsync(options, null!, new global::Azure.Security.KeyVault.Secrets.SecretClientOptions(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Registration_uses_the_section_path_in_messages()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration);
        services.AddConfigFerretAzureKeyVault(configuration);
        using var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AzureKeyVaultOptions>>().Value);
        Assert.StartsWith("VaultUri is not set.", ex.Failures.Single(), StringComparison.Ordinal);
    }
}
