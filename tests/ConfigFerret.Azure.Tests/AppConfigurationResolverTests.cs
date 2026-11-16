using ConfigFerret.Sources;
using Microsoft.Extensions.Configuration;
using static ConfigFerret.Azure.Tests.TestLayers;

namespace ConfigFerret.Azure.Tests;

public class AppConfigurationResolverTests
{
    private static readonly Uri SecretId = new("https://contoso-prod.vault.azure.net/secrets/Db--Password");

    private static readonly AzureAppConfigurationSettings Settings = new(
        "contoso",
        [
            new("Orders:Db:Timeout", null, "contoso") { ConfigurationKey = "Db:Timeout", Value = "30" },
            new("Orders:Db:Timeout", "prod", "contoso") { ConfigurationKey = "Db:Timeout", Value = "60" },
            new("Db:Password", null, "contoso") { Kind = AzureAppConfigurationSettingKind.KeyVaultReference, SecretId = SecretId },
            new("Broken:Ref", null, "contoso") { Kind = AzureAppConfigurationSettingKind.KeyVaultReference },
            new(".appconfig.featureflag/Beta", null, "contoso") { Kind = AzureAppConfigurationSettingKind.FeatureFlag, ConfigurationKey = "FeatureManagement:Beta", Enabled = true, Filters = ["Microsoft.Percentage", "Microsoft.TimeWindow"] },
            new(".appconfig.featureflag/On", null, "contoso") { Kind = AzureAppConfigurationSettingKind.FeatureFlag, ConfigurationKey = "FeatureManagement:On", Enabled = true, Value = "True" },
            new(".appconfig.featureflag/Off", null, "contoso") { Kind = AzureAppConfigurationSettingKind.FeatureFlag, ConfigurationKey = "FeatureManagement:Off", Enabled = false, Value = "False" },
            new(".appconfig.featureflag/Odd", null, "contoso") { Kind = AzureAppConfigurationSettingKind.FeatureFlag, ConfigurationKey = "FeatureManagement:Odd" },
            new("FeatureManagement:Plain", null, "contoso") { Value = "x" },
            new("DB_HOST", null, "contoso") { Value = "db" },
        ],
        ["load warning"]);

    private static IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, Action<AzureAppConfigurationTraceOptions>? configure = null, TraceDiagnostics? diagnostics = null)
    {
        var options = new AzureAppConfigurationTraceOptions();
        configure?.Invoke(options);
        return new AzureAppConfigurationResolver(Settings, options).Resolve(layer, diagnostics ?? new TraceDiagnostics());
    }

    [Fact]
    public void Names_a_feature_flag_by_its_key_when_it_has_no_FeatureManagement_prefix()
    {
        var settings = new AzureAppConfigurationSettings("contoso",
        [
            new("Beta", null, "contoso") { Kind = AzureAppConfigurationSettingKind.FeatureFlag, Enabled = true },
            new(".appconfig.featureflag/Gamma", null, "contoso") { Kind = AzureAppConfigurationSettingKind.FeatureFlag, ConfigurationKey = "featuremanagement:Gamma", Enabled = false },
        ]);
        var resolver = new AzureAppConfigurationResolver(settings, new AzureAppConfigurationTraceOptions());

        var beta = Assert.Single(resolver.Resolve(Provider("AzureAppConfigurationProvider", "Beta", "True"), new TraceDiagnostics()));
        var gamma = Assert.Single(resolver.Resolve(Provider("AzureAppConfigurationProvider", "FeatureManagement:Gamma", "False"), new TraceDiagnostics()));

        Assert.Equal("Azure App Configuration feature flag Beta (contoso)", beta.Source);
        Assert.Equal("Azure App Configuration feature flag Gamma (contoso)", gamma.Source);
    }

    [Fact]
    public void Links_an_environment_variable_to_every_label_lowest_first()
    {
        var chain = Resolve(Env("DB__TIMEOUT", "60"));

        Assert.Equal(["30", "60"], chain.Select(s => s.Value));
        Assert.All(chain, s => Assert.Equal(LayerKind.AzureAppConfiguration, s.Kind));
        Assert.All(chain, s => Assert.True(s.IsInferred));
        Assert.All(chain, s => Assert.False(s.IsSensitive));
        Assert.Equal("Azure App Configuration key Orders:Db:Timeout (contoso)", chain[1].Source);
        Assert.Equal(["no label", "label prod"], chain.Select(s => s.Location));
        Assert.Equal("matched by configuration key", chain[1].Note);
        Assert.Equal("matched by configuration key; the store's value differs from the value seen here, so the app may need a refresh or the value was transformed", chain[0].Note);
        Assert.Equal(new AzureAppConfigurationSettingRef("contoso", "Orders:Db:Timeout", "prod"), chain[1].Reference);
    }

    [Fact]
    public void Prefers_an_exact_key_and_matches_Kubernetes_layers()
    {
        var step = Assert.Single(Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesConfigMap, Source = "c", Reference = new ConfigMapKeyRef("cm", "db_host") }));
        Assert.Equal("matched by name", step.Note);
        Assert.Equal("db", step.Value);
        Assert.Equal(2, Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesWorkload, Source = "w", Reference = new WorkloadEnvRef("api", "app", "orders:db:timeout") }).Count);
        Assert.Empty(Resolve(Env("Nothing")));
        Assert.Empty(Resolve(new ProvenanceStep { Kind = LayerKind.Helm, Source = "h", Reference = new HelmValueRef("db") }));
        Assert.Empty(Resolve(External(null, "DB_HOST")));
        Assert.Empty(Resolve(Env("DB__TIMEOUT"), o => o.MatchByName = false));
    }

    [Fact]
    public void Shows_a_Key_Vault_reference_as_sensitive_with_the_secret_uri_in_the_note()
    {
        var step = Assert.Single(Resolve(Env("Db__Password", "s3cret")));

        Assert.True(step.IsSensitive);
        Assert.Null(step.Value);
        Assert.Equal("Azure App Configuration Key Vault reference Db:Password (contoso)", step.Source);
        Assert.Equal($"matched by configuration key; Key Vault reference to {SecretId}; the secret is not read here", step.Note);
        Assert.Equal(new AzureAppConfigurationSettingRef("contoso", "Db:Password", null, SecretId), step.Reference);
        Assert.Equal("matched by name; Key Vault reference with no valid secret URI", Assert.Single(Resolve(Env("Broken:Ref", "x"))).Note);
    }

    [Fact]
    public void Links_values_from_the_App_Configuration_provider_including_feature_flags()
    {
        var chain = Resolve(Provider("AzureAppConfigurationProvider", "db:timeout", "60"));
        Assert.Equal(2, chain.Count);
        Assert.Equal("read by the App Configuration provider (matched by key)", chain[1].Note);

        var on = Assert.Single(Resolve(Provider("AzureAppConfigurationProvider", "FeatureManagement:On", "True")));
        Assert.Equal("Azure App Configuration feature flag On (contoso)", on.Source);
        Assert.Equal("read by the App Configuration provider (matched by key); on", on.Note);
        Assert.Equal("True", on.Value);
        Assert.Equal("read by the App Configuration provider (matched by key); off", Assert.Single(Resolve(Provider("AzureAppConfigurationProvider", "FeatureManagement:Off"))).Note);
        Assert.Equal("read by the App Configuration provider (matched by key); could not be read", Assert.Single(Resolve(Provider("AzureAppConfigurationProvider", "FeatureManagement:Odd"))).Note);

        var filtered = Assert.Single(Resolve(Provider("AzureAppConfigurationProvider", "featuremanagement:beta:EnabledFor:0:Name", "Microsoft.Percentage")));
        Assert.Equal("read by the App Configuration provider (matched by key); on for filters Microsoft.Percentage, Microsoft.TimeWindow", filtered.Note);
        Assert.Empty(Resolve(Provider("AzureAppConfigurationProvider", "FeatureManagement:Plain:Sub")));
        Assert.Empty(Resolve(Provider("AzureAppConfigurationProvider", "FeatureManagement::x")));
        Assert.Empty(Resolve(Provider("AzureAppConfigurationProvider", "FeatureManagement:Nope:x")));
        Assert.Empty(Resolve(Provider("AzureAppConfigurationProvider", "Other:Beta:x")));
        Assert.Empty(Resolve(Provider("AzureAppConfigurationProvider", "Db:Timeout"), o => o.MatchConfigurationProvider = false));
        Assert.Empty(Resolve(Provider("in-memory collection", "Db:Timeout")));
        Assert.Empty(Resolve(new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "AzureAppConfigurationProvider" }));
    }

    [Fact]
    public void Follows_explicit_links_and_warns_when_the_key_is_missing()
    {
        var chain = Resolve(Env("TIMEOUT"), o => o.Map("TIMEOUT", "Orders:Db:Timeout"));
        Assert.Equal(2, chain.Count);
        Assert.All(chain, s => Assert.False(s.IsInferred));
        Assert.Null(chain[0].Note);

        var diagnostics = new TraceDiagnostics();
        Assert.Empty(Resolve(Env("TIMEOUT"), o => o.Links["TIMEOUT"] = " Missing ", diagnostics));
        Assert.Contains("TIMEOUT is linked to Azure App Configuration key Missing, but no such key-value was loaded from contoso.", diagnostics.Warnings);
        Assert.Single(Resolve(Env("DB_HOST"), o => o.Links["DB_HOST"] = ""));
        Assert.Throws<ArgumentException>(() => new AzureAppConfigurationTraceOptions().Map("", "x"));
        Assert.Throws<ArgumentException>(() => new AzureAppConfigurationTraceOptions().Map("x", ""));
    }

    [Fact]
    public void Passes_load_warnings_on_once()
    {
        var resolver = new AzureAppConfigurationResolver(Settings, new AzureAppConfigurationTraceOptions());
        var first = new TraceDiagnostics();
        var second = new TraceDiagnostics();
        resolver.Resolve(Env("x"), first);
        resolver.Resolve(Env("x"), second);

        Assert.Equal(["load warning"], first.Warnings);
        Assert.Empty(second.Warnings);
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(null!, first));
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(Env("x"), null!));
    }

    [Fact]
    public void Traces_through_App_Configuration_to_Key_Vault_and_redacts_the_secret()
    {
        const string secret = KeyVaultLoadTests.SecretValue;
        var configuration = new ConfigurationBuilder()
            .Add(new AzureAppConfigurationProvider(new() { ["Db:Password"] = secret, ["Db:Timeout"] = "60" }))
            .Build();
        var appConfig = Settings.ToLoadedSource();
        var vault = new AzureKeyVaultSecrets("contoso-prod", [new("Db--Password", "contoso-prod") { Value = secret }]).ToLoadedSource(new AzureKeyVaultTraceOptions { MatchByName = false });
        var report = ConfigTracer.For(configuration).WithSource(appConfig).WithSource(vault).Trace();

        var password = report.Find("Db:Password")!;
        Assert.True(password.IsSensitive);
        var reference = Assert.Single(password.EffectiveStep!.Origins);
        Assert.Equal(LayerKind.AzureAppConfiguration, reference.Kind);
        var kv = Assert.Single(reference.Origins);
        Assert.Equal("Azure Key Vault secret Db--Password (contoso-prod)", kv.Source);
        Assert.False(kv.IsInferred);
        Assert.DoesNotContain(secret, report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, report.ToJson(), StringComparison.Ordinal);

        var timeout = report.Find("Db:Timeout")!.EffectiveStep!.Origins;
        Assert.Equal([false, true], timeout.Select(s => s.IsEffective));
        Assert.Equal("Azure App Configuration contoso", appConfig.DisplayName);
        Assert.Empty(vault.FindUnused(report));

        var unused = appConfig.FindUnused(report);
        Assert.Contains(unused, e => e.Name == "DB_HOST" && e.Location == "contoso, no label" && !e.IsSensitive);
        Assert.Contains(unused, e => e.Name == "Broken:Ref" && e.IsSensitive && e.Detail == "Key Vault reference");
        Assert.DoesNotContain(unused, e => e.Name == "Db:Password");
        Assert.Contains(appConfig.Entries, e => e.Location == "contoso, label prod");
    }

    [Fact]
    public void WithAzureAppConfiguration_adds_the_resolver_with_options()
    {
        var configuration = new ConfigurationBuilder().Add(new AzureAppConfigurationProvider(new() { ["Db:Timeout"] = "60" })).Build();

        Assert.Empty(ConfigTracer.For(configuration).WithAzureAppConfiguration(Settings, o => o.MatchConfigurationProvider = false).Trace().Find("Db:Timeout")!.EffectiveStep!.Origins);
        Assert.Equal(2, ConfigTracer.For(configuration).WithAzureAppConfiguration(Settings).Trace().Find("Db:Timeout")!.EffectiveStep!.Origins.Count);
        Assert.Throws<ArgumentNullException>(() => AzureTracerExtensions.WithAzureAppConfiguration(null!, Settings));
        Assert.Throws<ArgumentNullException>(() => ConfigTracer.For(configuration).WithAzureAppConfiguration(null!));
    }

    [Fact]
    public void Defaults()
    {
        var options = new AzureAppConfigurationTraceOptions();
        Assert.True(options.MatchByName);
        Assert.True(options.MatchConfigurationProvider);
        var load = new AzureAppConfigurationOptions();
        Assert.True(load.IncludeKeyVaultReferences);
        Assert.True(load.IncludeFeatureFlags);
        Assert.Null(load.KeyFilter);
        Assert.Empty(load.Labels);
    }
}
