using ConfigFerret.Sources;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.HashiCorp.Tests;

public class VaultResolverTests
{
    private static readonly DateTimeOffset Updated = new(2026, 10, 1, 8, 45, 0, TimeSpan.Zero);

    // orders/prod is the configured path: the root secret holds Db__Timeout, the db secret below it holds password.
    private static readonly VaultSecrets WithValues = new(
    [
        new("orders/prod", "", new Dictionary<string, string?> { ["Db__Timeout"] = "30", ["Feature:Flag"] = "on" }, 5, Updated),
        new("orders/prod/db", "db", new Dictionary<string, string?> { ["password"] = "hunter2", ["port"] = "5432" }, 3, Updated),
        new("orders/prod/empty", "empty", new Dictionary<string, string?>()),
    ],
    location: "vault.example.com:8200");

    private static readonly VaultSecrets NamesOnly = new(
    [
        new("orders/prod/Db", "Db", Version: 2, UpdatedTime: Updated),
        new("orders/prod/Api/Key", "Api/Key", IsDeleted: true),
        new("orders/prod", ""),
    ]);

    private static ProvenanceStep EnvStep(string name, string? value = null) => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = $"environment variable {name}",
        Value = value,
        Reference = new EnvironmentVariableRef(name),
    };

    private static ProvenanceStep External(string? provider, string key, string? property = null, string? version = null, string? store = "vault-backend") => new()
    {
        Kind = LayerKind.KubernetesSecret,
        Source = "ExternalSecret db",
        Reference = new ExternalSecretRef(provider, store, key, property, version),
    };

    private static IReadOnlyList<ProvenanceStep> Resolve(VaultSecrets secrets, ProvenanceStep step, Action<VaultTraceOptions>? configure = null, TraceDiagnostics? diagnostics = null)
    {
        var options = new VaultTraceOptions();
        configure?.Invoke(options);
        return new VaultResolver(secrets, options).Resolve(step, diagnostics ?? new TraceDiagnostics());
    }

    [Theory]
    [InlineData("vault")]
    [InlineData("Vault")]
    [InlineData(null)]
    public void Links_an_external_secret_property_to_the_key_for_certain(string? provider)
    {
        var step = Assert.Single(Resolve(WithValues, External(provider, "orders/prod/db", "password")));

        Assert.Equal(LayerKind.HashiCorpVault, step.Kind);
        Assert.Equal("HashiCorp Vault secret secret/orders/prod/db key password", step.Source);
        Assert.Equal("vault.example.com:8200", step.Location);
        Assert.Equal("hunter2", step.Value);
        Assert.True(step.IsSensitive);
        Assert.False(step.IsInferred);
        Assert.Equal("read through secret store vault-backend; version 3, updated 2026-10-01 08:45:00Z", step.Note);
        Assert.Equal(new VaultSecretRef("secret", "orders/prod/db", "password"), step.Reference);
    }

    [Theory]
    [InlineData("secret/data/orders/prod/db")]
    [InlineData("/secret/orders/prod/db")]
    [InlineData("orders/prod/db/")]
    public void Finds_CSI_and_mount_prefixed_paths(string path)
    {
        Assert.Single(Resolve(WithValues, External("vault", path, "password", store: null)));
    }

    [Fact]
    public void Ignores_other_providers_and_warns_only_for_explicit_vault_references()
    {
        var diagnostics = new TraceDiagnostics();

        Assert.Empty(Resolve(WithValues, External("aws", "orders/prod/db", "password"), diagnostics: diagnostics));
        Assert.Empty(Resolve(WithValues, External(null, "missing", "password"), diagnostics: diagnostics));
        Assert.Empty(diagnostics.Warnings);
        Assert.Empty(Resolve(WithValues, External("vault", "secret/data/missing"), diagnostics: diagnostics));
        Assert.Empty(Resolve(WithValues, External("vault", "secretx/orders/prod/db"), diagnostics: diagnostics));
        Assert.Equal("ExternalSecret db reads HashiCorp Vault secret secret/data/missing, which was not loaded from mount secret.", diagnostics.Warnings[0]);
    }

    [Fact]
    public void Links_every_key_when_an_external_secret_takes_the_whole_secret_and_notes_a_pinned_version()
    {
        var steps = Resolve(WithValues, External("vault", "orders/prod/db", version: "2", store: null));

        Assert.Equal(["password", "port"], steps.Select(s => ((VaultSecretRef)s.Reference!).Key));
        Assert.All(steps, s => Assert.StartsWith("read by an external secret reference; pinned to version 2, while the current version is 3", s.Note, StringComparison.Ordinal));

        var same = Resolve(WithValues, External("vault", "orders/prod/db", version: "3"))[0];
        Assert.DoesNotContain("pinned", same.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Warns_when_an_external_secret_reads_a_key_the_secret_lacks()
    {
        var diagnostics = new TraceDiagnostics();

        Assert.Empty(Resolve(WithValues, External("vault", "orders/prod/db", "user"), diagnostics: diagnostics));

        Assert.Equal("ExternalSecret db reads key user of HashiCorp Vault secret orders/prod/db, but the secret has no such key.", Assert.Single(diagnostics.Warnings));
    }

    [Fact]
    public void Links_to_the_whole_secret_when_values_were_not_read()
    {
        var step = Assert.Single(Resolve(NamesOnly, External("vault", "orders/prod/Db", "password")));

        Assert.Equal("HashiCorp Vault secret secret/orders/prod/Db key password", step.Source);
        Assert.Null(step.Value);
        Assert.Equal(new VaultSecretRef("secret", "orders/prod/Db"), step.Reference);
        Assert.Equal("read through secret store vault-backend; key password was not checked, because secret values were not read (IncludeValues is off); version 2, updated 2026-10-01 08:45:00Z", step.Note);
    }

    [Fact]
    public void Says_a_key_is_missing_from_a_secret_with_no_keys()
    {
        var step = Assert.Single(Resolve(WithValues, External("vault", "orders/prod/empty", "password")));

        Assert.Contains("the secret has no keys, so key password is missing", step.Note, StringComparison.Ordinal);
        Assert.Single(Resolve(WithValues, External("vault", "orders/prod/empty")));
    }

    [Theory]
    [InlineData("Db__Timeout", "orders/prod", "Db__Timeout")]
    [InlineData("db__timeout", "orders/prod", "Db__Timeout")]
    [InlineData("Feature__Flag", "orders/prod", "Feature:Flag")]
    [InlineData("db__password", "orders/prod/db", "password")]
    public void Matches_keys_by_configuration_key(string name, string path, string key)
    {
        var step = Assert.Single(Resolve(WithValues, EnvStep(name)));

        Assert.Equal(new VaultSecretRef("secret", path, key), step.Reference);
        Assert.True(step.IsInferred);
        Assert.StartsWith("matched by name", step.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Matches_Kubernetes_layers_and_strips_prefixes()
    {
        Assert.Single(Resolve(WithValues, new ProvenanceStep { Kind = LayerKind.KubernetesWorkload, Source = "w", Reference = new WorkloadEnvRef("api", "app", "Db__Timeout") }));
        Assert.Single(Resolve(WithValues, new ProvenanceStep { Kind = LayerKind.KubernetesConfigMap, Source = "c", Reference = new ConfigMapKeyRef("cm", "Db__Timeout") }));
        Assert.Single(Resolve(WithValues, new ProvenanceStep { Kind = LayerKind.KubernetesSecret, Source = "s", Reference = new SecretKeyRef("sec", "db__password") }));

        Assert.Empty(Resolve(WithValues, EnvStep("ORDERS_Db__Timeout")));
        Assert.Single(Resolve(WithValues, EnvStep("ORDERS_Db__Timeout"), o => o.StripPrefixes.Add("orders_")));
        Assert.Empty(Resolve(WithValues, EnvStep("ORDERS_"), o => o.StripPrefixes.Add("ORDERS_")));
    }

    [Fact]
    public void Matches_a_layer_from_a_Vault_configuration_provider_by_its_key()
    {
        var provider = new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "VaultSharp.Extensions.Configuration.VaultConfigurationProvider", ConfigurationKey = "Db:Timeout" };
        var other = provider with { Source = "SomeOtherProvider" };

        Assert.Single(Resolve(WithValues, provider));
        Assert.Empty(Resolve(WithValues, other));
        Assert.Empty(Resolve(WithValues, provider with { ConfigurationKey = null }));
    }

    [Fact]
    public void Hides_a_value_from_a_Vault_configuration_provider_whose_secret_was_not_loaded()
    {
        const string value = "fake-orders-connection-value";
        var configuration = new ConfigurationBuilder()
            .Add(new VaultSharp.Extensions.Configuration.VaultConfigurationProvider(new() { ["Orders:Connection"] = value }))
            .Build();

        var report = ConfigTracer.For(configuration).WithOptions(o => o.Environment = new Dictionary<string, string?>()).WithVault(WithValues).Trace();

        var setting = report.Find("Orders:Connection")!;
        Assert.Equal("VaultConfigurationProvider", setting.EffectiveStep!.Source);
        Assert.Empty(setting.EffectiveStep.Origins);
        Assert.True(setting.IsSensitive);
        Assert.DoesNotContain(value, report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain(value, report.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain(value, report.Explain("Orders:Connection"), StringComparison.Ordinal);
    }

    [Fact]
    public void Matches_whole_secrets_and_sections_when_values_were_not_read()
    {
        var exact = Assert.Single(Resolve(NamesOnly, EnvStep("Api__Key")));
        Assert.Equal(new VaultSecretRef("secret", "orders/prod/Api/Key"), exact.Reference);
        Assert.Equal("matched by name; the current version is deleted", exact.Note);

        var section = Assert.Single(Resolve(NamesOnly, EnvStep("Db__Timeout")));
        Assert.Equal(new VaultSecretRef("secret", "orders/prod/Db"), section.Reference);
        Assert.Equal("HashiCorp Vault secret secret/orders/prod/Db", section.Source);
        Assert.StartsWith("matched by name: the secret may hold Timeout, but its keys were not read (IncludeValues is off)", section.Note, StringComparison.Ordinal);

        // The configured path itself would match every key, so it is never matched as a section.
        Assert.Empty(Resolve(NamesOnly, EnvStep("Other__Key")));
        Assert.Empty(Resolve(NamesOnly, EnvStep("Dbx__Timeout")));
    }

    [Fact]
    public void Prefers_the_longest_section()
    {
        var secrets = new VaultSecrets([new("p/Db", "Db"), new("p/Db/Pool", "Db/Pool")]);

        var step = Assert.Single(Resolve(secrets, EnvStep("Db__Pool__Size")));

        Assert.Equal("p/Db/Pool", ((VaultSecretRef)step.Reference!).Path);
    }

    [Fact]
    public void Does_not_match_by_name_when_turned_off()
    {
        Assert.Empty(Resolve(WithValues, EnvStep("Db__Timeout"), o => o.MatchByName = false));
        Assert.Empty(Resolve(WithValues, new ProvenanceStep { Kind = LayerKind.Helm, Source = "h", Reference = new HelmValueRef("db") }));
    }

    [Fact]
    public void Follows_explicit_links_and_warns_about_missing_ones()
    {
        var diagnostics = new TraceDiagnostics();

        var step = Assert.Single(Resolve(WithValues, EnvStep("DB_PASS"), o => o.Map("DB_PASS", "orders/prod/db", "password")));
        Assert.False(step.IsInferred);
        Assert.StartsWith("linked explicitly", step.Note, StringComparison.Ordinal);

        Assert.Equal(2, Resolve(WithValues, EnvStep("DB"), o => o.Map("DB", "orders/prod/db")).Count);
        Assert.Equal(2, Resolve(WithValues, EnvStep("DB"), o => o.Links["DB"] = "orders/prod/db#").Count);
        Assert.Single(Resolve(WithValues, EnvStep("Db__Timeout"), o => o.Links["Db__Timeout"] = " "));

        Assert.Empty(Resolve(WithValues, EnvStep("X"), o => o.Map("X", "orders/missing"), diagnostics));
        Assert.Equal("X is linked to HashiCorp Vault secret orders/missing, but no such secret was loaded.", Assert.Single(diagnostics.Warnings));
        Assert.Empty(Resolve(WithValues, EnvStep("X"), o => o.Map("X", "orders/prod/db", "nope"), diagnostics));
        Assert.Equal("X reads key nope of HashiCorp Vault secret orders/prod/db, but the secret has no such key.", diagnostics.Warnings[1]);
    }

    [Fact]
    public void Says_when_the_value_differs_without_printing_either_value()
    {
        var step = Assert.Single(Resolve(WithValues, EnvStep("Db__Timeout", "99")));
        Assert.Contains("differs", step.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("99", step.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("30", step.Note, StringComparison.Ordinal);

        Assert.DoesNotContain("differs", Assert.Single(Resolve(WithValues, EnvStep("Db__Timeout", "30"))).Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Adds_the_load_warnings_once()
    {
        var secrets = new VaultSecrets([], warnings: ["could not list"]);
        var resolver = new VaultResolver(secrets, new VaultTraceOptions());
        var diagnostics = new TraceDiagnostics();

        resolver.Resolve(EnvStep("A"), diagnostics);
        resolver.Resolve(EnvStep("B"), diagnostics);

        Assert.Equal(["could not list"], diagnostics.Warnings);
    }

    [Fact]
    public void Lists_entries_per_key_or_per_secret_with_matching_references()
    {
        var entries = new VaultResolver(WithValues, new VaultTraceOptions()).Entries().ToList();

        Assert.Equal(["orders/prod#Db__Timeout", "orders/prod#Feature:Flag", "orders/prod/db#password", "orders/prod/db#port", "orders/prod/empty"], entries.Select(e => e.Name));
        Assert.All(entries, e => Assert.True(e.IsSensitive));
        Assert.All(entries, e => Assert.Equal("mount secret at vault.example.com:8200", e.Location));
        Assert.Equal("version 3, updated 2026-10-01 08:45:00Z", entries[2].Detail);
        Assert.Null(entries[4].Detail);
        Assert.Equal(new VaultSecretRef("secret", "orders/prod/empty"), entries[4].Reference);
    }

    [Fact]
    public void Checks_its_arguments()
    {
        var resolver = new VaultResolver(WithValues, new VaultTraceOptions());

        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(null!, new TraceDiagnostics()));
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(EnvStep("A"), null!));
        Assert.Throws<ArgumentNullException>(() => ConfigTracer.For(new ConfigurationBuilder().Build()).WithVault(null!));
        Assert.Throws<ArgumentNullException>(() => HashiCorpTracerExtensions.WithVault(null!, WithValues));
        Assert.Throws<ArgumentException>(() => new VaultTraceOptions().Map(" ", "p"));
        Assert.Throws<ArgumentException>(() => new VaultTraceOptions().Map("n", " "));
    }

    [Fact]
    public void Traces_an_environment_variable_to_Vault_and_redacts_the_value_end_to_end()
    {
        var prefix = $"CFVAULT{Guid.NewGuid():N}_";
        Environment.SetEnvironmentVariable(prefix + "Db__Password", "hunter2");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            var secrets = new VaultSecrets([new("orders/prod", "", new Dictionary<string, string?> { ["Db__Password"] = "hunter2", ["Unused"] = "x" })]);

            var tracer = ConfigTracer.For(configuration)
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + "Db__Password"] = "hunter2" })
                .WithVault(secrets, o => o.StripPrefixes.Add(prefix));
            var report = tracer.Trace();

            var password = report.Find("Db:Password")!;
            Assert.Equal($"set in environment variable {prefix}Db__Password from HashiCorp Vault secret secret/orders/prod key Db__Password", password.Story);
            Assert.True(password.IsSensitive);
            Assert.DoesNotContain("hunter2", report.ToJson(), StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", report.ToText(), StringComparison.Ordinal);

            var loaded = new LoadedConfigSource("vault", new VaultResolver(secrets, new VaultTraceOptions { StripPrefixes = { prefix } }), new VaultResolver(secrets, new()).Entries());
            var report2 = ConfigTracer.For(configuration)
                .WithOptions(o => o.Environment = new Dictionary<string, string?> { [prefix + "Db__Password"] = "hunter2" })
                .WithSource(loaded)
                .Trace();
            Assert.Equal(["orders/prod#Unused"], loaded.FindUnused(report2).Select(e => e.Name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "Db__Password", null);
        }
    }
}
