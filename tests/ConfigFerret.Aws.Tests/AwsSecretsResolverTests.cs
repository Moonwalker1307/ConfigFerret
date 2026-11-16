using Kralizek.Extensions.Configuration.Internal;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Aws.Tests;

public class AwsSecretsResolverTests
{
    private const string DbArn = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:prod/orders/db-AbCdEf";
    private const string Password = "s3cr3t-pa55";

    private static readonly AwsSecrets WithValues = new(
    [
        new AwsSecret("prod/orders/db") { Arn = DbArn, Value = $$$"""{"password":"{{{Password}}}","host":"db.internal","pool":{"max":"20"}}""", ValueRead = true },
        new AwsSecret("prod/orders/Api__Key") { Value = "api-key-value", ValueRead = true },
        new AwsSecret("prod/orders/Cert") { IsBinary = true, ValueRead = true },
        new AwsSecret("DB_PASSWORD") { Value = Password, ValueRead = true },
        new AwsSecret("other/Db/Password") { Value = "x", ValueRead = true },
    ],
    location: "eu-west-1",
    namePrefix: "prod/orders/");

    private static readonly AwsSecrets NamesOnly = new([new AwsSecret("prod/orders/db") { Arn = DbArn }, new AwsSecret("DB_PASSWORD")], location: "eu-west-1");

    private static ProvenanceStep Env(string name, string? value = null) => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = $"environment variable {name}",
        Value = value,
        Reference = new EnvironmentVariableRef(name),
    };

    private static ProvenanceStep External(string key, string? provider = "aws", string? property = null, string? version = null, string? store = "aws-store") => new()
    {
        Kind = LayerKind.KubernetesSecret,
        Source = "ExternalSecret db",
        Reference = new ExternalSecretRef(provider, store, key, property, version),
    };

    private static ProvenanceStep ProviderLayer(string key, string source = "Kralizek.Extensions.Configuration.Internal.SecretsManagerConfigurationProvider", string? value = null) => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = source,
        Value = value,
        ConfigurationKey = key,
    };

    private static IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, AwsSecrets? secrets = null, Action<AwsSecretsTraceOptions>? configure = null, TraceDiagnostics? diagnostics = null)
    {
        var options = new AwsSecretsTraceOptions();
        configure?.Invoke(options);
        return new AwsSecretsResolver(secrets ?? WithValues, options).Resolve(layer, diagnostics ?? new TraceDiagnostics());
    }

    [Theory]
    [InlineData("aws")]
    [InlineData("AWS")]
    [InlineData(null)]
    public void Links_an_external_secret_property_with_certainty(string? provider)
    {
        var step = Assert.Single(Resolve(External("prod/orders/db", provider, "password")));

        Assert.Equal(LayerKind.AwsSecretsManager, step.Kind);
        Assert.Equal("AWS Secrets Manager secret prod/orders/db, property password", step.Source);
        Assert.Equal("eu-west-1, account 123456789012", step.Location);
        Assert.Equal(Password, step.Value);
        Assert.True(step.IsSensitive);
        Assert.False(step.IsInferred);
        Assert.Equal("external secret reference from store aws-store", step.Note);
        Assert.Equal(new AwsSecretRef("prod/orders/db", "password"), step.Reference);
    }

    [Fact]
    public void Links_an_external_secret_by_ARN_nested_property_and_version()
    {
        var step = Assert.Single(Resolve(External(DbArn, property: "pool.max", version: "v7", store: null)));

        Assert.Equal("20", step.Value);
        Assert.Equal(new AwsSecretRef("prod/orders/db", "pool:max"), step.Reference);
        Assert.Equal("external secret reference; pinned to version v7, while ConfigFerret read the current version", step.Note);
    }

    [Fact]
    public void Links_a_whole_external_secret_and_ignores_other_providers()
    {
        var step = Assert.Single(Resolve(External("prod/orders/Api__Key")));
        Assert.Equal("api-key-value", step.Value);
        Assert.Equal(new AwsSecretRef("prod/orders/Api__Key"), step.Reference);
        Assert.Equal("AWS Secrets Manager secret prod/orders/Api__Key", step.Source);

        Assert.Empty(Resolve(External("prod/orders/db", provider: "azurekv")));
    }

    [Fact]
    public void Warns_when_an_aws_external_secret_or_its_property_is_missing()
    {
        var diagnostics = new TraceDiagnostics();
        Assert.Empty(Resolve(External("prod/missing"), diagnostics: diagnostics));
        Assert.Empty(Resolve(External("prod/missing", store: null), diagnostics: diagnostics));
        Assert.Empty(Resolve(External("prod/missing", provider: null), diagnostics: diagnostics));
        Assert.Empty(Resolve(External("/orders/prod/Db"), diagnostics: diagnostics));
        Assert.Empty(Resolve(External("arn:aws:ssm:eu-west-1:1:parameter/x"), diagnostics: diagnostics));

        var step = Assert.Single(Resolve(External("prod/orders/db", property: "nope"), diagnostics: diagnostics));
        Assert.Null(step.Value);
        Assert.Equal(new AwsSecretRef("prod/orders/db", "nope"), step.Reference);
        Assert.Contains("the secret has no JSON property nope", step.Note, StringComparison.Ordinal);

        Assert.Equal(
        [
            "An external secret reference (store aws-store) points to AWS secret prod/missing, but no such secret was loaded.",
            "An external secret reference points to AWS secret prod/missing, but no such secret was loaded.",
            "AWS secret prod/orders/db has no JSON property nope.",
        ], diagnostics.Warnings);
    }

    [Fact]
    public void Says_when_values_were_not_read_and_property_cannot_be_checked()
    {
        var diagnostics = new TraceDiagnostics();
        var step = Assert.Single(Resolve(External("prod/orders/db", property: "password"), NamesOnly, diagnostics: diagnostics));

        Assert.Null(step.Value);
        Assert.Equal("external secret reference from store aws-store; the value was not read (turn on IncludeValues to read it)", step.Note);
        Assert.Empty(diagnostics.Warnings);
    }

    [Fact]
    public void Matches_an_environment_variable_with_the_same_name()
    {
        var step = Assert.Single(Resolve(Env("DB_PASSWORD", Password)));

        Assert.Equal("AWS Secrets Manager secret DB_PASSWORD", step.Source);
        Assert.True(step.IsInferred);
        Assert.Equal("matched by name", step.Note);
        Assert.Equal("eu-west-1", step.Location);
        Assert.Empty(Resolve(Env("DB_PASSWORD"), configure: o => o.MatchByName = false));
        Assert.Empty(Resolve(Env("db_password")));
    }

    [Fact]
    public void Matches_by_the_NET_key_after_the_prefix_for_names_and_JSON_properties()
    {
        var property = Assert.Single(Resolve(Env("db__Password")));
        Assert.Equal(new AwsSecretRef("prod/orders/db", "password"), property.Reference);
        Assert.Equal("matched by .NET key", property.Note);
        Assert.True(property.IsInferred);

        Assert.Equal(new AwsSecretRef("prod/orders/Api__Key"), Assert.Single(Resolve(Env("Api__Key"))).Reference);
        Assert.Equal(new AwsSecretRef("prod/orders/db", "pool:max"), Assert.Single(Resolve(new ProvenanceStep { Kind = LayerKind.KubernetesConfigMap, Source = "c", Reference = new ConfigMapKeyRef("cm", "db__pool__max") })).Reference);
        Assert.Equal(new AwsSecretRef("prod/orders/db"), Assert.Single(Resolve(Env("db"))).Reference);

        // other/Db/Password does not start with the prefix, so it has no .NET key.
        Assert.Empty(Resolve(Env("other__Db__Password")));
    }

    [Fact]
    public void Uses_an_explicit_key_prefix_over_the_name_prefix()
    {
        var step = Assert.Single(Resolve(Env("Db__Password"), configure: o => o.KeyPrefix = "OTHER/"));

        Assert.Equal(new AwsSecretRef("other/Db/Password"), step.Reference);
        Assert.Empty(Resolve(Env("Api__Key"), configure: o => o.KeyPrefix = "other/"));
    }

    [Fact]
    public void Maps_names_without_a_prefix()
    {
        var secrets = new AwsSecrets([new AwsSecret("Db/Password"), new AwsSecret("Db__Password"), new AwsSecret("prod/x")]);

        var steps = Resolve(Env("Db__Password"), secrets);

        // The exact name wins over the .NET key.
        Assert.Equal(new AwsSecretRef("Db__Password"), Assert.Single(steps).Reference);
        Assert.Equal(2, Resolve(new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "SecretsManager", ConfigurationKey = "Db:Password" }, secrets).Count);
        Assert.Equal("Db:Password", AwsSecretsResolver.ToKey("prod/orders/Db/Password", "prod/orders/"));
        Assert.Equal("Db:Password", AwsSecretsResolver.ToKey("Db__Password", null));
        Assert.Equal("Db:Password", AwsSecretsResolver.ToKey("/Db/Password/", ""));
        Assert.Null(AwsSecretsResolver.ToKey("x/Db", "prod/"));
    }

    [Fact]
    public void Matches_keys_set_by_a_Secrets_Manager_configuration_provider()
    {
        var step = Assert.Single(Resolve(ProviderLayer("Db:Password", value: Password)));
        Assert.Equal(new AwsSecretRef("prod/orders/db", "password"), step.Reference);
        Assert.True(step.IsInferred);
        Assert.Equal("the key this secret maps to in the Secrets Manager configuration provider", step.Note);

        Assert.Single(Resolve(ProviderLayer("Db:Password", "Amazon.Extensions.Configuration.SystemsManager.SystemsManagerConfigurationProvider")));
        Assert.Empty(Resolve(ProviderLayer("Db:Password", "in-memory collection")));
        Assert.Empty(Resolve(ProviderLayer("Db:Password"), configure: o => o.MatchConfigurationKeys = false));
        Assert.Empty(Resolve(ProviderLayer("Db:Nope")));
        Assert.Empty(Resolve(ProviderLayer("Db:Password") with { Reference = new FileRef("/app/appsettings.json") }));
        Assert.Empty(Resolve(new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "SecretsManager" }));
    }

    [Fact]
    public void Follows_explicit_links_by_layer_name_or_provider_key()
    {
        var step = Assert.Single(Resolve(Env("PG_PASS"), configure: o => o.Map("pg_pass", "prod/orders/db", "password")));
        Assert.Equal(new AwsSecretRef("prod/orders/db", "password"), step.Reference);
        Assert.False(step.IsInferred);
        Assert.Equal("linked explicitly", step.Note);

        Assert.Equal(new AwsSecretRef("prod/orders/db"), Assert.Single(Resolve(Env("PG"), configure: o => o.Links["PG"] = $" {DbArn} ")).Reference);
        Assert.Equal(new AwsSecretRef("prod/orders/db"), Assert.Single(Resolve(Env("PG"), configure: o => o.Links["PG"] = "prod/orders/db#")).Reference);
        Assert.Equal(new AwsSecretRef("prod/orders/db"), Assert.Single(Resolve(Env("PG"), configure: o => o.Map("PG", "prod/orders/db", ""))).Reference);
        Assert.Equal(new AwsSecretRef("prod/orders/db", "host"), Assert.Single(Resolve(ProviderLayer("Weird:Key"), configure: o => o.Links["Weird:Key"] = "prod/orders/db#host")).Reference);

        // A blank link is ignored, and name matching carries on.
        Assert.Equal(new AwsSecretRef("DB_PASSWORD"), Assert.Single(Resolve(Env("DB_PASSWORD"), configure: o => o.Links["DB_PASSWORD"] = " ")).Reference);
        Assert.Throws<ArgumentException>(() => new AwsSecretsTraceOptions().Map(" ", "x"));
        Assert.Throws<ArgumentException>(() => new AwsSecretsTraceOptions().Map("x", " "));
    }

    [Fact]
    public void Warns_when_an_explicit_link_points_nowhere()
    {
        var diagnostics = new TraceDiagnostics();

        Assert.Empty(Resolve(Env("PG"), configure: o => o.Map("PG", "nope"), diagnostics: diagnostics));
        Assert.Empty(Resolve(ProviderLayer("A:B"), configure: o => o.Map("A:B", "nope"), diagnostics: diagnostics));

        Assert.Equal(
            ["PG is linked to AWS secret nope, but no such secret was loaded.", "A:B is linked to AWS secret nope, but no such secret was loaded."],
            diagnostics.Warnings);
    }

    [Fact]
    public void Says_when_the_value_differs_without_printing_either_value()
    {
        var step = Assert.Single(Resolve(Env("DB_PASSWORD", "different-value")));
        Assert.Contains("differs", step.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("different-value", step.Note, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, step.Note, StringComparison.Ordinal);

        Assert.Equal("matched by name", Assert.Single(Resolve(Env("DB_PASSWORD", Password))).Note);
    }

    [Fact]
    public void Shows_binary_secrets_without_a_value()
    {
        var step = Assert.Single(Resolve(External("prod/orders/Cert")));

        Assert.Null(step.Value);
        Assert.Equal("external secret reference from store aws-store; binary secret, its value is not shown", step.Note);
    }

    [Fact]
    public void Ignores_layers_without_a_name()
    {
        Assert.Empty(Resolve(new ProvenanceStep { Kind = LayerKind.Helm, Source = "h", Reference = new HelmValueRef("db.password") }));
        Assert.Empty(Resolve(Env("UNKNOWN")));
    }

    [Fact]
    public void Adds_load_warnings_to_the_trace_once()
    {
        var secrets = new AwsSecrets([], ["could not read one value"]);
        var resolver = new AwsSecretsResolver(secrets, new AwsSecretsTraceOptions());
        var diagnostics = new TraceDiagnostics();

        resolver.Resolve(Env("A"), diagnostics);
        resolver.Resolve(Env("B"), new TraceDiagnostics());

        Assert.Equal(["could not read one value"], diagnostics.Warnings);
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(null!, diagnostics));
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(Env("A"), null!));
    }

    [Fact]
    public void Traces_an_external_secret_and_a_provider_key_and_redacts_values_end_to_end()
    {
        var configuration = new ConfigurationBuilder().Add(new FakeSecretsManagerSource(new() { ["Db:Password"] = Password })).Build();
        var tracer = ConfigTracer.For(configuration).WithAwsSecretsManager(WithValues);

        var report = tracer.Trace();

        var setting = report.Find("Db:Password")!;
        Assert.True(setting.IsSensitive);
        var origin = Assert.Single(setting.EffectiveStep!.Origins);
        Assert.Equal("AWS Secrets Manager secret prod/orders/db, property password", origin.Source);
        Assert.DoesNotContain(Password, report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, report.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, report.Explain("Db:Password"), StringComparison.Ordinal);
    }

    [Fact]
    public void Hides_a_provider_value_linked_to_a_secret_that_was_not_loaded()
    {
        const string value = "fake-orders-connection-value";
        var configuration = new ConfigurationBuilder().Add(new FakeSecretsManagerSource(new() { ["Orders:Connection"] = value })).Build();
        var tracer = ConfigTracer.For(configuration)
            .WithOptions(o => o.Environment = new Dictionary<string, string?>())
            .WithAwsSecretsManager(WithValues, o => o.Map("Orders:Connection", "prod/orders/missing"));

        var report = tracer.Trace();

        var setting = report.Find("Orders:Connection")!;
        Assert.Empty(setting.EffectiveStep!.Origins);
        Assert.Contains("Orders:Connection is linked to AWS secret prod/orders/missing, but no such secret was loaded.", report.Warnings);
        Assert.True(setting.IsSensitive);
        Assert.DoesNotContain(value, report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain(value, report.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain(value, report.Explain("Orders:Connection"), StringComparison.Ordinal);
    }

    [Fact]
    public void Checks_its_arguments()
    {
        var tracer = ConfigTracer.For(new ConfigurationBuilder().Build());
        Assert.Throws<ArgumentNullException>(() => AwsTracerExtensions.WithAwsSecretsManager(null!, WithValues));
        Assert.Throws<ArgumentNullException>(() => tracer.WithAwsSecretsManager(null!));
        Assert.Same(tracer, tracer.WithAwsSecretsManager(WithValues, o => o.MatchByName = false));
    }
}

/// <summary>Stands in for Kralizek's provider: it builds a provider with the real full type name.</summary>
internal sealed class FakeSecretsManagerSource(Dictionary<string, string?> data) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new SecretsManagerConfigurationProvider(data);
}
