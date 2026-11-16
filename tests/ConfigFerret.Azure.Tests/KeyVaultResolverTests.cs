using Azure.Extensions.AspNetCore.Configuration.Secrets;
using ConfigFerret.Sources;
using Microsoft.Extensions.Configuration;
using static ConfigFerret.Azure.Tests.TestLayers;

namespace ConfigFerret.Azure.Tests;

public class KeyVaultResolverTests
{
    private const string Value = KeyVaultLoadTests.SecretValue;
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly AzureKeyVaultSecrets Secrets = new(
        "contoso-prod",
        [
            new("Db--Password", "contoso-prod") { Value = Value, Version = "v2" },
            new("db-password", "contoso-prod"),
            new("Api--Key", "contoso-prod", Enabled: false),
            new("Old--Token", "contoso-prod") { ExpiresOn = Now.AddDays(-1) },
            new("Future--Token", "contoso-prod") { NotBefore = Now.AddDays(1) },
            new("Valid--Token", "contoso-prod") { ExpiresOn = Now.AddDays(1), NotBefore = Now.AddDays(-1) },
        ],
        ["load warning"]);

    private static IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, Action<AzureKeyVaultTraceOptions>? configure = null, TraceDiagnostics? diagnostics = null, AzureKeyVaultSecrets? secrets = null)
    {
        var options = new AzureKeyVaultTraceOptions { Clock = new FixedClock(Now) };
        configure?.Invoke(options);
        return new AzureKeyVaultResolver(secrets ?? Secrets, options).Resolve(layer, diagnostics ?? new TraceDiagnostics());
    }

    [Fact]
    public void Links_an_environment_variable_to_the_secret_for_the_same_configuration_key()
    {
        var step = Assert.Single(Resolve(Env("DB__PASSWORD", Value)));

        Assert.Equal(LayerKind.AzureKeyVault, step.Kind);
        Assert.Equal("Azure Key Vault secret Db--Password (contoso-prod)", step.Source);
        Assert.Equal("version v2", step.Location);
        Assert.Equal(Value, step.Value);
        Assert.True(step.IsSensitive);
        Assert.True(step.IsInferred);
        Assert.Equal("matched by configuration key", step.Note);
        Assert.Equal(new AzureKeyVaultSecretRef("contoso-prod", "Db--Password"), step.Reference);
    }

    [Fact]
    public void Prefers_an_exact_name_and_matches_Kubernetes_layers()
    {
        var step = Assert.Single(Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesSecret, Source = "s", Reference = new SecretKeyRef("db", "DB-PASSWORD") }));
        Assert.Equal("Azure Key Vault secret db-password (contoso-prod)", step.Source);
        Assert.Equal("matched by name; value not read", step.Note);
        Assert.Null(step.Location);

        Assert.Single(Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesConfigMap, Source = "c", Reference = new ConfigMapKeyRef("cm", "Db--Password") }));
        Assert.Single(Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesWorkload, Source = "w", Reference = new WorkloadEnvRef("api", "app", "Db__Password") }));
        Assert.Empty(Resolve(Env("Nothing")));
        Assert.Empty(Resolve(new ProvenanceStep { Kind = LayerKind.Helm, Source = "h", Reference = new HelmValueRef("db.password") }));
    }

    [Fact]
    public void Says_when_the_vault_value_differs_without_printing_either()
    {
        var step = Assert.Single(Resolve(Env("Db__Password", "something-else")));

        Assert.Equal("matched by configuration key; the vault's current value differs from the value seen here, so the app may need a restart or a reload", step.Note);
        Assert.DoesNotContain("something-else", step.Note, StringComparison.Ordinal);
        Assert.DoesNotContain(Value, step.Note, StringComparison.Ordinal);
        Assert.Equal("matched by configuration key", Assert.Single(Resolve(Env("Db__Password"))).Note);
    }

    [Fact]
    public void Does_not_match_by_name_when_turned_off()
    {
        Assert.Empty(Resolve(Env("Db__Password"), o => o.MatchByName = false));
        Assert.Empty(Resolve(Env("db-password"), o => o.MatchByName = false));
    }

    [Fact]
    public void Follows_explicit_links_and_warns_when_the_secret_is_missing()
    {
        var step = Assert.Single(Resolve(Env("DB_PASS"), o => o.Map("DB_PASS", "Db--Password")));
        Assert.False(step.IsInferred);
        Assert.Equal(string.Empty, step.Note);

        var diagnostics = new TraceDiagnostics();
        Assert.Equal("Azure Key Vault secret Missing (contoso-prod)", Assert.Single(Resolve(Env("DB_PASS"), o => o.Links["DB_PASS"] = " Missing ", diagnostics)).Source);
        Assert.Contains("DB_PASS is linked to Azure Key Vault secret Missing, but no such secret was loaded from contoso-prod.", diagnostics.Warnings);

        // A blank link falls back to matching by name.
        Assert.Single(Resolve(Env("Db__Password"), o => o.Links["Db__Password"] = " "));
        Assert.Throws<ArgumentException>(() => new AzureKeyVaultTraceOptions().Map(" ", "x"));
        Assert.Throws<ArgumentException>(() => new AzureKeyVaultTraceOptions().Map("x", " "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("azurekv")]
    [InlineData("AzureKV")]
    public void Links_an_external_secret_for_Azure_or_an_unknown_store_as_certain(string? provider)
    {
        var step = Assert.Single(Resolve(External(provider, "Db--Password")));

        Assert.False(step.IsInferred);
        Assert.Equal("named by an external secret reference through store azure-store", step.Note);
    }

    [Fact]
    public void Reads_external_secret_details()
    {
        Assert.Empty(Resolve(External("aws", "Db--Password")));
        Assert.Empty(Resolve(External(null, "key/Db--Password")));
        Assert.Empty(Resolve(External(null, "Missing")));
        Assert.Single(Resolve(External("azurekv", "secret/db--password")));
        Assert.Equal("named by an external secret reference; value not read", Assert.Single(Resolve(External(null, "db-password", store: null))).Note);

        var pinned = Assert.Single(Resolve(External(null, "Db--Password", version: "v1")));
        Assert.Contains("pinned to version v1, but the current version is v2", pinned.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("pinned", Assert.Single(Resolve(External(null, "Db--Password", version: "V2"))).Note, StringComparison.Ordinal);
        Assert.DoesNotContain("pinned", Assert.Single(Resolve(External(null, "db-password", version: "v1"))).Note, StringComparison.Ordinal);

        // A property of a JSON secret is not comparable to the whole value.
        var property = Assert.Single(Resolve(External(null, "Db--Password", property: "password") with { Value = "other" }));
        Assert.Equal("named by an external secret reference through store azure-store; reads property password of the secret's JSON value", property.Note);
        var whole = Assert.Single(Resolve(External(null, "Db--Password") with { Value = "other" }));
        Assert.Contains("differs", whole.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Links_an_App_Configuration_Key_Vault_reference_to_its_secret()
    {
        static ProvenanceStep Ref(string uri) => new()
        {
            Kind = LayerKind.AzureAppConfiguration,
            Source = "ref",
            Reference = new AzureAppConfigurationSettingRef("contoso", "Db:Password", null, new Uri(uri)),
        };

        var step = Assert.Single(Resolve(Ref("https://CONTOSO-PROD.vault.azure.net/secrets/Db--Password")));
        Assert.False(step.IsInferred);
        Assert.Equal("named by an App Configuration Key Vault reference", step.Note);

        Assert.Contains("the reference pins version old, but the current version is v2", Assert.Single(Resolve(Ref("https://contoso-prod.vault.azure.net/secrets/Db--Password/old"))).Note, StringComparison.Ordinal);
        Assert.DoesNotContain("pins", Assert.Single(Resolve(Ref("https://contoso-prod.vault.azure.net/secrets/Db--Password/v2"))).Note, StringComparison.Ordinal);
        Assert.DoesNotContain("pins", Assert.Single(Resolve(Ref("https://contoso-prod.vault.azure.net/secrets/db-password/v9"))).Note, StringComparison.Ordinal);
        Assert.Empty(Resolve(Ref("https://other.vault.azure.net/secrets/Db--Password")));
        Assert.Empty(Resolve(Ref("https://contoso-prod.vault.azure.net/keys/Db--Password")));
        Assert.Empty(Resolve(Ref("https://contoso-prod.vault.azure.net/secrets")));
        Assert.Empty(Resolve(Ref("https://contoso-prod.vault.azure.net/secrets/Db--Password/v2/extra")));
        Assert.Empty(Resolve(Ref("https://contoso-prod.vault.azure.net/secrets/Missing")));
        Assert.Empty(Resolve(new ProvenanceStep { Kind = LayerKind.AzureAppConfiguration, Source = "kv", Reference = new AzureAppConfigurationSettingRef("contoso", "Db__Password", null) }));
    }

    [Fact]
    public void Links_a_value_from_the_Key_Vault_configuration_provider_by_key()
    {
        var step = Assert.Single(Resolve(Provider("AzureKeyVaultConfigurationProvider", "db:password", Value)));

        Assert.True(step.IsInferred);
        Assert.Equal("read by the Key Vault configuration provider (matched by key)", step.Note);
        Assert.Empty(Resolve(Provider("in-memory collection", "db:password")));
        Assert.Empty(Resolve(new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "AzureKeyVaultConfigurationProvider" }));
    }

    [Fact]
    public void Hides_a_value_from_the_Key_Vault_provider_when_no_loaded_secret_maps_to_its_key()
    {
        var diagnostics = new TraceDiagnostics();
        var step = Assert.Single(Resolve(Provider("AzureKeyVaultConfigurationProvider", "Storage:Account1", "fake-marker-value"), diagnostics: diagnostics));

        Assert.Equal(LayerKind.AzureKeyVault, step.Kind);
        Assert.Equal("Azure Key Vault secret not in the snapshot of contoso-prod", step.Source);
        Assert.Equal("read by the Key Vault configuration provider, but no loaded secret maps to this key", step.Note);
        Assert.Null(step.Value);
        Assert.Null(step.Reference);
        Assert.True(step.IsSensitive);
        Assert.True(step.IsInferred);
        Assert.Equal(["load warning"], diagnostics.Warnings);

        // A layer that is already hidden, as the core does for the provider types it knows, gets no step.
        Assert.Empty(Resolve(Provider("AzureKeyVaultConfigurationProvider", "Storage:Account1", "fake-marker-value") with { IsSensitive = true }));
    }

    [Fact]
    public void Hides_a_value_from_the_Key_Vault_provider_without_linking_it_when_provider_matching_is_off()
    {
        var step = Assert.Single(Resolve(Provider("CachedAzureKeyVaultProvider", "db:password", Value), o => o.MatchConfigurationProvider = false));

        Assert.Equal(LayerKind.AzureKeyVault, step.Kind);
        Assert.Equal("Azure Key Vault secret, not linked", step.Source);
        Assert.Equal("read by the Key Vault configuration provider; MatchConfigurationProvider is off, so no secret is linked", step.Note);
        Assert.Null(step.Value);
        Assert.Null(step.Reference);
        Assert.True(step.IsSensitive);
        Assert.True(step.IsInferred);
        Assert.Empty(Resolve(Provider("AzureKeyVaultConfigurationProvider", "db:password", Value) with { IsSensitive = true }, o => o.MatchConfigurationProvider = false));
        Assert.Empty(Resolve(Provider("in-memory collection", "db:password", Value), o => o.MatchConfigurationProvider = false));
    }

    [Fact]
    public void Hides_a_value_linked_to_a_secret_that_was_not_loaded()
    {
        var step = Assert.Single(Resolve(Env("STORAGE_ACCOUNT", "fake-marker-value"), o => o.Map("STORAGE_ACCOUNT", "storage-connection")));

        Assert.Equal(LayerKind.AzureKeyVault, step.Kind);
        Assert.Equal("Azure Key Vault secret storage-connection (contoso-prod)", step.Source);
        Assert.Equal("linked explicitly, but not in the snapshot of the vault", step.Note);
        Assert.Null(step.Value);
        Assert.Equal(new AzureKeyVaultSecretRef("contoso-prod", "storage-connection"), step.Reference);
        Assert.True(step.IsSensitive);
        Assert.False(step.IsInferred);
    }

    [Fact]
    public void Warns_about_a_matched_secret_that_cannot_be_read()
    {
        var diagnostics = new TraceDiagnostics();
        Resolve(Env("Api__Key"), diagnostics: diagnostics);
        Resolve(Provider("AzureKeyVaultConfigurationProvider", "Old:Token"), diagnostics: diagnostics);
        Resolve(External(null, "Future--Token"), diagnostics: diagnostics);
        Resolve(Env("Valid__Token"), diagnostics: diagnostics);

        Assert.Equal(
        [
            "load warning",
            "Azure Key Vault secret Api--Key (contoso-prod), linked to Api__Key, is disabled, so it cannot be read.",
            "Azure Key Vault secret Old--Token (contoso-prod), linked to Old:Token, expired on 2026-05-31, so it cannot be read.",
            "Azure Key Vault secret Future--Token (contoso-prod), linked to Future--Token, is not valid until 2026-06-02, so it cannot be read yet.",
        ], diagnostics.Warnings);
        Assert.Equal("matched by configuration key; disabled; value not read", Assert.Single(Resolve(Env("Api__Key"))).Note);
    }

    [Fact]
    public void Names_the_layer_by_its_source_when_it_has_no_name()
    {
        var secrets = new AzureKeyVaultSecrets("v", [new("A", "v", Enabled: false)]);
        var diagnostics = new TraceDiagnostics();
        var layer = new ProvenanceStep { Kind = LayerKind.AzureAppConfiguration, Source = "the ref", Reference = new AzureAppConfigurationSettingRef("s", "k", null, new Uri("https://v.vault.azure.net/secrets/A")) };

        Resolve(layer, diagnostics: diagnostics, secrets: secrets);

        Assert.Equal(["Azure Key Vault secret A (v), linked to the ref, is disabled, so it cannot be read."], diagnostics.Warnings);
    }

    [Fact]
    public void Passes_load_warnings_on_once()
    {
        var resolver = new AzureKeyVaultResolver(Secrets, new AzureKeyVaultTraceOptions());
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
    public void Default_clock_is_the_system_clock()
    {
        Assert.Same(TimeProvider.System, new AzureKeyVaultTraceOptions().Clock);
        Assert.True(new AzureKeyVaultTraceOptions().MatchByName);
        Assert.True(new AzureKeyVaultTraceOptions().MatchConfigurationProvider);
    }

    [Fact]
    public void Traces_end_to_end_redacts_the_secret_and_lists_unused_secrets()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Plain"] = "1" })
            .Add(new AzureKeyVaultConfigurationProvider(new() { ["Db:Password"] = Value }))
            .Build();
        var loaded = Secrets.ToLoadedSource();
        var tracer = ConfigTracer.For(configuration).WithOptions(o => o.Environment = new Dictionary<string, string?>()).WithSource(loaded);

        var report = tracer.Trace();

        var setting = report.Find("Db:Password")!;
        Assert.True(setting.IsSensitive);
        var origin = Assert.Single(setting.EffectiveStep!.Origins);
        Assert.Equal("Azure Key Vault secret Db--Password (contoso-prod)", origin.Source);
        Assert.True(origin.IsEffective);
        Assert.DoesNotContain(Value, report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Value, report.ToJson(), StringComparison.Ordinal);
        Assert.Contains("Azure Key Vault secret Db--Password", report.ToText(), StringComparison.Ordinal);
        Assert.Contains("load warning", report.Warnings);

        Assert.Equal("Azure Key Vault contoso-prod", loaded.DisplayName);
        var unused = loaded.FindUnused(report);
        Assert.Equal(["Api--Key", "db-password", "Future--Token", "Old--Token", "Valid--Token"], unused.Select(e => e.Name));
        Assert.All(loaded.Entries, e => Assert.True(e.IsSensitive));
        Assert.All(loaded.Entries, e => Assert.Equal("contoso-prod", e.Location));
        Assert.Equal("disabled", loaded.Entries.Single(e => e.Name == "Api--Key").Detail);
    }

    [Fact]
    public void Hides_a_value_from_the_Key_Vault_provider_whose_secret_was_not_loaded()
    {
        const string connection = "DefaultEndpointsProtocol=https;AccountName=fake;AccountKey=FAKE-ACCOUNT-KEY==";
        var configuration = new ConfigurationBuilder()
            .Add(new AzureKeyVaultConfigurationProvider(new() { ["Storage:Account1"] = connection }))
            .Build();

        var report = ConfigTracer.For(configuration).WithOptions(o => o.Environment = new Dictionary<string, string?>()).WithAzureKeyVault(Secrets).Trace();

        var setting = report.Find("Storage:Account1")!;
        Assert.Empty(setting.EffectiveStep!.Origins);
        Assert.True(setting.IsSensitive);
        Assert.DoesNotContain(connection, report.Explain("Storage:Account1"), StringComparison.Ordinal);
        Assert.DoesNotContain(connection, report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain(connection, report.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Hides_a_value_from_an_unknown_Key_Vault_provider_whose_secret_was_not_loaded()
    {
        const string connection = "DefaultEndpointsProtocol=https;AccountName=fake;AccountKey=FAKE-ACCOUNT-KEY==";
        var configuration = new ConfigurationBuilder()
            .Add(new CachedAzureKeyVaultProvider(new() { ["Storage:Account1"] = connection }))
            .Build();

        var report = ConfigTracer.For(configuration).WithOptions(o => o.Environment = new Dictionary<string, string?>()).WithAzureKeyVault(Secrets).Trace();

        var setting = report.Find("Storage:Account1")!;
        Assert.Equal("CachedAzureKeyVaultProvider", setting.EffectiveStep!.Source);
        Assert.True(setting.IsSensitive);
        Assert.True(setting.EffectiveStep.IsSensitive);
        Assert.DoesNotContain(connection, report.Explain("Storage:Account1"), StringComparison.Ordinal);
        Assert.DoesNotContain(connection, report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain(connection, report.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Hides_a_value_from_an_unknown_Key_Vault_provider_when_provider_matching_is_off()
    {
        var configuration = new ConfigurationBuilder()
            .Add(new CachedAzureKeyVaultProvider(new() { ["Storage:Account1"] = "fake-marker-value" }))
            .Build();
        var secrets = new AzureKeyVaultSecrets("contoso-prod", [new("Storage--Account1", "contoso-prod")]);

        var report = ConfigTracer.For(configuration).WithOptions(o => o.Environment = new Dictionary<string, string?>())
            .WithAzureKeyVault(secrets, o => o.MatchConfigurationProvider = false)
            .Trace();

        Assert.True(report.Find("Storage:Account1")!.IsSensitive);
        Assert.DoesNotContain("fake-marker-value", report.Explain("Storage:Account1"), StringComparison.Ordinal);
        Assert.DoesNotContain("fake-marker-value", report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain("fake-marker-value", report.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Names_the_vault_that_holds_the_secret_when_another_vault_is_traced_too()
    {
        var other = new AzureKeyVaultSecrets("contoso-other", [new("Api--Key", "contoso-other")]);
        var configuration = new ConfigurationBuilder()
            .Add(new AzureKeyVaultConfigurationProvider(new() { ["Db:Password"] = Value }))
            .Build();

        var report = ConfigTracer.For(configuration).WithOptions(o => o.Environment = new Dictionary<string, string?>())
            .WithAzureKeyVault(Secrets)
            .WithAzureKeyVault(other)
            .Trace();

        var setting = report.Find("Db:Password")!;
        Assert.Equal("Azure Key Vault secret Db--Password (contoso-prod)", Assert.Single(setting.EffectiveStep!.Origins).Source);
        Assert.Contains("Azure Key Vault secret Db--Password (contoso-prod)", setting.Story, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso-other", report.Explain("Db:Password"), StringComparison.Ordinal);
    }

    [Fact]
    public void Hides_an_environment_variable_linked_to_a_secret_that_was_not_loaded()
    {
        const string connection = "DefaultEndpointsProtocol=https;AccountName=fake;AccountKey=FAKE-ACCOUNT-KEY==";
        var variables = new Dictionary<string, string?> { ["STORAGE_ACCOUNT"] = connection };
        var configuration = new ConfigurationBuilder().Add(new FakeEnvironmentVariables(variables)).Build();

        var report = ConfigTracer.For(configuration)
            .WithOptions(o => o.Environment = variables)
            .WithAzureKeyVault(Secrets, o => o.Map("STORAGE_ACCOUNT", "storage-connection"))
            .Trace();

        var setting = report.Find("STORAGE_ACCOUNT")!;
        Assert.Equal("environment variable STORAGE_ACCOUNT", setting.EffectiveStep!.Source);
        Assert.True(setting.IsSensitive);
        Assert.Contains("STORAGE_ACCOUNT is linked to Azure Key Vault secret storage-connection, but no such secret was loaded from contoso-prod.", report.Warnings);
        Assert.Contains("Azure Key Vault secret storage-connection (contoso-prod)", report.Explain("STORAGE_ACCOUNT"), StringComparison.Ordinal);
        Assert.DoesNotContain(connection, report.Explain("STORAGE_ACCOUNT"), StringComparison.Ordinal);
        Assert.DoesNotContain(connection, report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain(connection, report.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void WithAzureKeyVault_adds_the_resolver_with_options()
    {
        var configuration = new ConfigurationBuilder().Add(new AzureKeyVaultConfigurationProvider(new() { ["Db:Password"] = Value })).Build();

        var report = ConfigTracer.For(configuration).WithAzureKeyVault(Secrets, o => o.MatchConfigurationProvider = false).Trace();
        Assert.Empty(report.Find("Db:Password")!.EffectiveStep!.Origins);

        Assert.Single(ConfigTracer.For(configuration).WithAzureKeyVault(Secrets).Trace().Find("Db:Password")!.EffectiveStep!.Origins);
        Assert.Throws<ArgumentNullException>(() => AzureTracerExtensions.WithAzureKeyVault(null!, Secrets));
        Assert.Throws<ArgumentNullException>(() => ConfigTracer.For(configuration).WithAzureKeyVault(null!));
    }
}
