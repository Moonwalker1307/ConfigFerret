using Amazon.SimpleSystemsManagement;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Aws.Tests;

public class AwsParametersTests
{
    private const string Decrypted = "decrypted-pa55word";

    private static readonly AwsParameters Parameters = new(
        "/orders/prod/",
        [
            new("/orders/prod/Db/Timeout", AwsParameterType.String, "30", 3, Arn: "arn:aws:ssm:eu-west-1:123456789012:parameter/orders/prod/Db/Timeout"),
            new("/orders/prod/Db/Password", AwsParameterType.SecureString, Decrypted),
            new("/orders/prod/Api/Key", AwsParameterType.SecureString, null),
            new("/orders/prod/Hosts", AwsParameterType.StringList, "a.example,b.example"),
            new("/orders/prod/Json", AwsParameterType.String, """{"user":"u","nested":{"x":"1"}}"""),
            new("/orders/prod/", AwsParameterType.String, "root"),
            new("/ORDERS/PROD/Upper", AwsParameterType.String, "u"),
            new("/elsewhere/Thing", AwsParameterType.String, "t"),
        ],
        location: "eu-west-1");

    private static async Task<AwsParameters> Load(FakeAws aws, bool includeValues = false, bool recursive = true, string path = "/orders/prod/")
    {
        using var client = aws.ParameterStore();
        return await AwsParameters.LoadAsync(new AwsParameterStoreOptions { Path = path, IncludeValues = includeValues, Recursive = recursive }, client, TestContext.Current.CancellationToken);
    }

    private static ProvenanceStep Env(string name, string? value = null) => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = $"environment variable {name}",
        Value = value,
        Reference = new EnvironmentVariableRef(name),
    };

    private static ProvenanceStep ProviderLayer(string key, string source = "Amazon.Extensions.Configuration.SystemsManager.SystemsManagerConfigurationProvider") => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = source,
        ConfigurationKey = key,
    };

    private static ProvenanceStep External(string key, string? provider = "aws", string? property = null, string? version = null, string? store = "ssm") => new()
    {
        Kind = LayerKind.KubernetesSecret,
        Source = "ExternalSecret",
        Reference = new ExternalSecretRef(provider, store, key, property, version),
    };

    private static IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, Action<AwsParametersTraceOptions>? configure = null, TraceDiagnostics? diagnostics = null, AwsParameters? parameters = null)
    {
        var options = new AwsParametersTraceOptions();
        configure?.Invoke(options);
        return new AwsParametersResolver(parameters ?? Parameters, options).Resolve(layer, diagnostics ?? new TraceDiagnostics());
    }

    [Fact]
    public async Task Reads_a_path_recursively_page_by_page_without_decrypting_by_default()
    {
        var aws = new FakeAws()
            .On("GetParametersByPath", """
                {"Parameters":[
                  {"Name":"/orders/prod/Db/Timeout","Type":"String","Value":"30","Version":3,"LastModifiedDate":1.6E9,"ARN":"arn:aws:ssm:eu-west-1:123456789012:parameter/orders/prod/Db/Timeout","DataType":"text"},
                  {"Name":"/orders/prod/Db/Password","Type":"SecureString","Value":"AQICAHciphertext"}
                ],"NextToken":"p2"}
                """)
            .On("GetParametersByPath", """{"Parameters":[{"Name":"/orders/prod/Hosts","Type":"StringList","Value":"a,b"},{"Name":"/orders/prod/Odd","Type":"Fancy","Value":"?"},{},{"Name":""},null]}""");

        var parameters = await Load(aws);

        Assert.Equal(["/orders/prod/Db/Timeout", "/orders/prod/Db/Password", "/orders/prod/Hosts", "/orders/prod/Odd"], parameters.Entries.Select(p => p.Name));
        var timeout = parameters.Entries[0];
        Assert.Equal(AwsParameterType.String, timeout.Type);
        Assert.Equal("30", timeout.Value);
        Assert.Equal(3, timeout.Version);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_600_000_000), timeout.LastModified);
        Assert.Equal("eu-west-1, account 123456789012", timeout.Location);
        Assert.False(timeout.IsSensitive);

        // The ciphertext is not kept.
        Assert.Null(parameters.Entries[1].Value);
        Assert.True(parameters.Entries[1].IsSensitive);
        Assert.Equal(["a", "b"], parameters.Entries[2].Items);
        Assert.Equal(AwsParameterType.Unknown, parameters.Entries[3].Type);
        Assert.Null(parameters.Entries[3].Value);
        Assert.True(parameters.Entries[3].IsSensitive);
        Assert.Equal("eu-west-1", parameters.Location);
        Assert.Equal("/orders/prod/", parameters.Path);

        var bodies = aws.Operations("GetParametersByPath").ToList();
        Assert.Equal("""{"MaxResults":10,"Path":"/orders/prod/","Recursive":true,"WithDecryption":false}""", bodies[0]);
        Assert.Contains("\"NextToken\":\"p2\"", bodies[1], StringComparison.Ordinal);
        Assert.Equal(2, bodies.Count);
    }

    [Fact]
    public async Task Decrypts_only_when_values_are_included()
    {
        var aws = new FakeAws().On("GetParametersByPath", """{"Parameters":[{"Name":"/p/S","Type":"SecureString","Value":"plain"}]}""");

        var parameters = await Load(aws, includeValues: true, recursive: false, path: " /p ");

        Assert.Equal("plain", Assert.Single(parameters.Entries).Value);
        Assert.Equal("""{"MaxResults":10,"Path":"/p","Recursive":false,"WithDecryption":true}""", aws.Requests[0].Body);
    }

    [Fact]
    public async Task Stops_when_a_next_token_repeats_and_reads_odd_responses()
    {
        var aws = new FakeAws()
            .On("GetParametersByPath", """{"Parameters":[{"Name":"/p/a","Type":"String","Value":"1"}],"NextToken":"t"}""")
            .On("GetParametersByPath", """{"Parameters":[{"Name":"/p/a","Type":"String","Value":"duplicate"}],"NextToken":"t"}""");

        var parameters = await Load(aws, path: "/p");

        Assert.Equal("1", Assert.Single(parameters.Entries).Value);
        Assert.Equal(2, aws.Requests.Count);
        Assert.Empty((await Load(new FakeAws().On("GetParametersByPath", "{}"), path: "/p")).Entries);
    }

    [Fact]
    public async Task Fails_when_AWS_refuses_and_checks_its_arguments()
    {
        var ex = await Assert.ThrowsAnyAsync<AmazonSimpleSystemsManagementException>(() => Load(new FakeAws().Fail("GetParametersByPath", "AccessDeniedException")));
        Assert.Equal("AccessDeniedException", ex.ErrorCode);
        Assert.DoesNotContain(FakeAws.SecretKey, ex.ToString(), StringComparison.Ordinal);

        await Assert.ThrowsAsync<ArgumentException>(() => Load(new FakeAws(), path: "orders/prod"));
        await Assert.ThrowsAsync<ArgumentException>(() => Load(new FakeAws(), path: " "));
        using var client = new FakeAws().ParameterStore();
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<ArgumentNullException>(() => AwsParameters.LoadAsync(null!, client, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => AwsParameters.LoadAsync(new AwsParameterStoreOptions { Path = "/p" }, client: null!, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => AwsParameters.LoadAsync(null!, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => AwsParameters.LoadAsync(new AwsParameterStoreOptions(), ct));
        Assert.Throws<ArgumentNullException>(() => new AwsParameters(null!, []));
        Assert.Throws<ArgumentNullException>(() => new AwsParameters("/p", null!));
        Assert.Throws<ArgumentNullException>(() => new AwsParameters("/p", [null!]));
        Assert.Throws<ArgumentNullException>(() => Parameters.Find(null!));
        Assert.Throws<ArgumentNullException>(() => Parameters.ToConfigurationKey(null!));
    }

    [Fact]
    public void Maps_names_to_keys_the_way_AddSystemsManager_does()
    {
        Assert.Equal("Db:Timeout", Parameters.ToConfigurationKey(Parameters.Entries[0]));
        Assert.Equal("Upper", Parameters.ToConfigurationKey(Parameters.Find("/ORDERS/PROD/Upper")!));
        Assert.Equal("elsewhere:Thing", Parameters.ToConfigurationKey(Parameters.Find("/elsewhere/Thing")!));
        Assert.Equal("Db:Timeout", AwsParameters.ToConfigurationKey("/orders/prod/Db/Timeout", "/orders/prod"));
        Assert.Equal("a__b", AwsParameters.ToConfigurationKey("/a__b", ""));
        Assert.Same(Parameters.Entries[0], Parameters.Find("arn:aws:ssm:eu-west-1:123456789012:parameter/orders/prod/Db/Timeout"));
        Assert.Null(Parameters.Find("/orders/prod/db/timeout"));
        Assert.Empty(new AwsParameter("/x", AwsParameterType.StringList, null).Items);
        Assert.Empty(new AwsParameter("/x", AwsParameterType.String, "a,b").Items);
    }

    [Fact]
    public void Matches_environment_variables_by_NET_key_and_by_name()
    {
        var step = Assert.Single(Resolve(Env("Db__Timeout", "30")));
        Assert.Equal(LayerKind.AwsParameterStore, step.Kind);
        Assert.Equal("AWS Parameter Store parameter /orders/prod/Db/Timeout", step.Source);
        Assert.Equal("30", step.Value);
        Assert.False(step.IsSensitive);
        Assert.True(step.IsInferred);
        Assert.Equal("matched by .NET key", step.Note);
        Assert.Equal("eu-west-1, account 123456789012", step.Location);
        Assert.Equal(new AwsParameterRef("/orders/prod/Db/Timeout"), step.Reference);

        var same = Assert.Single(Resolve(Env("/elsewhere/Thing")));
        Assert.Equal("matched by name", same.Note);
        Assert.Equal("eu-west-1", same.Location);

        Assert.Empty(Resolve(Env("Db__Timeout"), o => o.MatchByName = false));
        Assert.Empty(Resolve(Env("Nope")));
    }

    [Fact]
    public void Marks_SecureString_parameters_sensitive_and_says_when_not_decrypted()
    {
        var password = Assert.Single(Resolve(Env("Db__Password")));
        Assert.True(password.IsSensitive);
        Assert.Equal(Decrypted, password.Value);
        Assert.Equal("AWS Parameter Store SecureString parameter /orders/prod/Db/Password", password.Source);

        var key = Assert.Single(Resolve(Env("Api__Key", "seen")));
        Assert.Null(key.Value);
        Assert.Equal("matched by .NET key; the value was not decrypted (turn on IncludeValues to read it)", key.Note);
    }

    [Fact]
    public void Links_StringList_items_and_the_whole_list()
    {
        var item = Assert.Single(Resolve(ProviderLayer("Hosts:1")));
        Assert.Equal("b.example", item.Value);
        Assert.Equal("AWS Parameter Store StringList parameter /orders/prod/Hosts, item 1", item.Source);
        Assert.Equal("the key AddSystemsManager(\"/orders/prod/\") gives this parameter", item.Note);
        Assert.Equal(new AwsParameterRef("/orders/prod/Hosts"), item.Reference);

        Assert.Equal("a.example,b.example", Assert.Single(Resolve(Env("Hosts"))).Value);
        Assert.Empty(Resolve(ProviderLayer("Hosts:2")));
    }

    [Fact]
    public void Matches_keys_only_from_the_Systems_Manager_provider()
    {
        Assert.Single(Resolve(ProviderLayer("db:timeout")));
        Assert.Empty(Resolve(ProviderLayer("Db:Timeout", "Kralizek.Extensions.Configuration.Internal.SecretsManagerConfigurationProvider")));
        Assert.Empty(Resolve(ProviderLayer("Db:Timeout"), o => o.MatchConfigurationKeys = false));
        Assert.Empty(Resolve(ProviderLayer("Db:Timeout") with { Reference = new FileRef("/app/appsettings.json") }));
        Assert.Empty(Resolve(new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "SystemsManager" }));
    }

    [Fact]
    public void Links_external_secrets_that_name_a_parameter()
    {
        var step = Assert.Single(Resolve(External("/orders/prod/Db/Password", version: "4")));
        Assert.False(step.IsInferred);
        Assert.True(step.IsSensitive);
        Assert.Equal("external secret reference from store ssm; pinned to version 4, while ConfigFerret read the current version", step.Note);

        Assert.Single(Resolve(External("arn:aws:ssm:eu-west-1:123456789012:parameter/orders/prod/Db/Timeout", provider: null, store: null)));
        Assert.Equal("external secret reference", Assert.Single(Resolve(External("/orders/prod/Db/Timeout", store: null))).Note);
        Assert.Empty(Resolve(External("/orders/prod/Db/Timeout", provider: "vault")));
        Assert.Empty(Resolve(External("prod/orders/db")));
    }

    [Fact]
    public void Picks_a_JSON_property_of_an_external_secret_parameter()
    {
        var step = Assert.Single(Resolve(External("/orders/prod/Json", property: "nested.x")));
        Assert.Equal("1", step.Value);
        Assert.Equal("AWS Parameter Store parameter /orders/prod/Json, property nested:x", step.Source);

        var missing = Assert.Single(Resolve(External("/orders/prod/Json", property: "nope")));
        Assert.Null(missing.Value);
        Assert.Contains("the value has no JSON property nope", missing.Note, StringComparison.Ordinal);

        var encrypted = Assert.Single(Resolve(External("/orders/prod/Api/Key", property: "p")));
        Assert.DoesNotContain("no JSON property", encrypted.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Warns_about_missing_parameters_under_the_path_only()
    {
        var diagnostics = new TraceDiagnostics();

        Assert.Empty(Resolve(External("/orders/prod/Missing"), diagnostics: diagnostics));
        Assert.Empty(Resolve(External("/orders/prod/Missing2", store: null), diagnostics: diagnostics));
        Assert.Empty(Resolve(External("/orders/prod/Missing3", provider: null), diagnostics: diagnostics));
        Assert.Empty(Resolve(External("/other/Missing"), diagnostics: diagnostics));
        Assert.Empty(Resolve(Env("X"), o => o.Map("X", "/orders/prod/Nope"), diagnostics));
        Assert.Empty(Resolve(ProviderLayer("Y"), o => o.Map("Y", "/orders/prod/Nope"), diagnostics));

        Assert.Equal(
        [
            "An external secret reference (store ssm) points to AWS parameter /orders/prod/Missing, but no such parameter was loaded.",
            "An external secret reference points to AWS parameter /orders/prod/Missing2, but no such parameter was loaded.",
            "X is linked to AWS parameter /orders/prod/Nope, but no such parameter was loaded.",
            "Y is linked to AWS parameter /orders/prod/Nope, but no such parameter was loaded.",
        ], diagnostics.Warnings);
    }

    [Fact]
    public void Follows_explicit_links()
    {
        var step = Assert.Single(Resolve(Env("TIMEOUT"), o => o.Map("timeout", "/orders/prod/Db/Timeout")));
        Assert.False(step.IsInferred);
        Assert.Equal("linked explicitly", step.Note);
        Assert.Single(Resolve(ProviderLayer("Other:Key"), o => o.Links["Other:Key"] = " /orders/prod/Db/Timeout "));
        Assert.Single(Resolve(Env("Db__Timeout"), o => o.Links["Db__Timeout"] = " "));
        Assert.Throws<ArgumentException>(() => new AwsParametersTraceOptions().Map(" ", "/x"));
        Assert.Throws<ArgumentException>(() => new AwsParametersTraceOptions().Map("x", " "));
    }

    [Fact]
    public void Says_when_the_value_differs_without_printing_either_value()
    {
        var step = Assert.Single(Resolve(Env("Db__Timeout", "45")));

        Assert.Equal("matched by .NET key; the AWS value differs from the value seen here, so the deployment may be out of date or the value was transformed", step.Note);
        Assert.Equal("matched by .NET key", Assert.Single(Resolve(Env("Db__Timeout", "30"))).Note);
    }

    [Fact]
    public void Describes_unknown_types_and_adds_load_warnings_once()
    {
        var parameters = new AwsParameters("/p/", [new("/p/X", AwsParameterType.Unknown, "v")], ["partial"]);
        var resolver = new AwsParametersResolver(parameters, new AwsParametersTraceOptions());
        var diagnostics = new TraceDiagnostics();

        var step = Assert.Single(resolver.Resolve(Env("X"), diagnostics));
        resolver.Resolve(Env("X"), diagnostics);

        Assert.Equal("AWS Parameter Store parameter (unknown type) /p/X", step.Source);
        Assert.True(step.IsSensitive);
        Assert.Null(step.Location);
        Assert.Equal(["partial"], diagnostics.Warnings);
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(null!, diagnostics));
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(Env("X"), null!));
    }

    [Fact]
    public void Traces_a_provider_key_and_redacts_SecureString_values_end_to_end()
    {
        var configuration = new ConfigurationBuilder().Add(new FakeSystemsManagerSource(new() { ["Db:Password"] = Decrypted, ["Db:Timeout"] = "30" })).Build();

        var report = ConfigTracer.For(configuration).WithAwsParameterStore(Parameters).Trace();

        Assert.Equal("AWS Parameter Store parameter /orders/prod/Db/Timeout", Assert.Single(report.Find("Db:Timeout")!.EffectiveStep!.Origins).Source);
        Assert.True(report.Find("Db:Password")!.IsSensitive);
        Assert.DoesNotContain(Decrypted, report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Decrypted, report.ToJson(), StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => AwsTracerExtensions.WithAwsParameterStore(null!, Parameters));
        Assert.Throws<ArgumentNullException>(() => ConfigTracer.For(configuration).WithAwsParameterStore(null!));
    }
}

/// <summary>Stands in for Amazon's provider: its type name holds "SystemsManager", like the real one.</summary>
internal sealed class FakeSystemsManagerSource(Dictionary<string, string?> data) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new FakeSystemsManagerConfigurationProvider(data);
}

internal sealed class FakeSystemsManagerConfigurationProvider : ConfigurationProvider
{
    public FakeSystemsManagerConfigurationProvider(Dictionary<string, string?> data) => Data = new Dictionary<string, string?>(data, StringComparer.OrdinalIgnoreCase);
}
