using System.Net;
using System.Text;
using ConfigFerret.Sources;
using ConfigFerret.Tests.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConfigFerret.Aws.Tests;

/// <summary>Edge cases that pin down details the main tests leave open.</summary>
public class AwsEdgeCaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Signs_with_the_given_keys_and_session_token()
    {
        var aws = new FakeAws().On("ListSecrets", """{"SecretList":[]}""");
        using var client = aws.SecretsManager(new AwsSecretsManagerOptions { Region = "eu-west-1", AccessKeyId = "AKIDSIGN", SecretAccessKey = "s", SessionToken = "session-tok" });

        await AwsSecrets.LoadAsync(new AwsSecretsManagerOptions(), client, Ct);

        Assert.Contains("Credential=AKIDSIGN/", aws.SigningHeaders[0], StringComparison.Ordinal);
        Assert.Contains("session-tok", aws.SigningHeaders[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Credential=AKIDSIGN/", new FakeAws().SigningHeaders.FirstOrDefault() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signs_parameter_requests_with_the_given_keys()
    {
        var aws = new FakeAws().On("GetParametersByPath", "{}");
        using var client = aws.ParameterStore();

        await AwsParameters.LoadAsync(new AwsParameterStoreOptions { Path = "/p" }, client, Ct);

        Assert.Contains("Credential=AKIDEXAMPLE/", aws.SigningHeaders[0], StringComparison.Ordinal);
        Assert.DoesNotContain("X-Amz-Security-Token", aws.SigningHeaders[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stops_listing_after_the_page_limit_with_a_warning()
    {
        var aws = new FakeAws().OnEach("ListSecrets", i => $$"""{"SecretList":[{"Name":"s{{i}}"}],"NextToken":"t{{i}}"}""");
        using var client = aws.SecretsManager();

        var secrets = await AwsSecrets.LoadAsync(new AwsSecretsManagerOptions(), client, Ct);

        Assert.Equal(1000, secrets.Entries.Count);
        Assert.Equal(["Stopped listing AWS secrets after 1000 pages."], secrets.Warnings);
    }

    [Fact]
    public async Task Stops_reading_parameters_after_the_page_limit_with_a_warning()
    {
        var aws = new FakeAws().OnEach("GetParametersByPath", i => $$"""{"Parameters":[{"Name":"/p/{{i}}","Type":"String","Value":"v"}],"NextToken":"t{{i}}"}""");
        using var client = aws.ParameterStore();

        var parameters = await AwsParameters.LoadAsync(new AwsParameterStoreOptions { Path = "/p" }, client, Ct);

        Assert.Equal(5000, parameters.Entries.Count);
        Assert.Equal(["Stopped reading AWS parameters under /p after 5000 pages."], parameters.Warnings);
    }

    [Fact]
    public async Task Treats_empty_ARNs_and_descriptions_as_missing()
    {
        var aws = new FakeAws()
            .On("ListSecrets", """{"SecretList":[{"Name":"a","ARN":"","Description":""}]}""")
            .On("GetParametersByPath", """{"Parameters":[{"Name":"/p/a","Type":"String","Value":"v","ARN":""}]}""");
        using var secretsClient = aws.SecretsManager();
        using var parametersClient = aws.ParameterStore();

        var secret = Assert.Single((await AwsSecrets.LoadAsync(new AwsSecretsManagerOptions(), secretsClient, Ct)).Entries);
        var parameters = await AwsParameters.LoadAsync(new AwsParameterStoreOptions { Path = "/p" }, parametersClient, Ct);

        Assert.Null(secret.Arn);
        Assert.Null(secret.Description);
        Assert.False(secret.IsBinary);
        Assert.Null(Assert.Single(parameters.Entries).Arn);
        Assert.Null(parameters.Find(string.Empty));
        Assert.Null(new AwsParameters("/p", [new("/p/a", AwsParameterType.String, "v", Arn: "")]).Find(string.Empty));
    }

    [Fact]
    public async Task Reads_GetSecretValue_answers_with_both_a_string_and_binary_or_neither()
    {
        var aws = new FakeAws()
            .On("ListSecrets", """{"SecretList":[{"Name":"both"},{"Name":"neither"}]}""")
            .Fail("BatchGetSecretValue", "AccessDeniedException")
            .On("GetSecretValue", """{"Name":"both","SecretString":"text","SecretBinary":"AAEC"}""")
            .On("GetSecretValue", """{"Name":"neither"}""");
        using var client = aws.SecretsManager();

        var secrets = await AwsSecrets.LoadAsync(new AwsSecretsManagerOptions { IncludeValues = true }, client, Ct);

        Assert.Contains("\"SecretId\":\"both\"", aws.Operations("GetSecretValue").First(), StringComparison.Ordinal);
        Assert.False(secrets.Find("both")!.IsBinary);
        Assert.Equal("text", secrets.Find("both")!.Value);
        var neither = secrets.Find("neither")!;
        Assert.True(neither.ValueRead);
        Assert.False(neither.IsBinary);

        // Read but empty: no "not read" note.
        var step = Assert.Single(new AwsSecretsResolver(secrets, new AwsSecretsTraceOptions()).Resolve(
            new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "env", Reference = new EnvironmentVariableRef("neither") }, new TraceDiagnostics()));
        Assert.Equal("matched by name", step.Note);
    }

    [Fact]
    public void Finds_a_partial_ARN_only_for_ARNs()
    {
        var secrets = new AwsSecrets([new AwsSecret("odd") { Arn = "name-AbCdEf" }]);

        Assert.Null(secrets.Find("name"));
        Assert.Same(secrets.Entries[0], secrets.Find("name-AbCdEf"));
    }

    [Fact]
    public void Caps_JSON_properties_and_prefers_an_exact_dotted_name()
    {
        var big = "{" + string.Join(",", Enumerable.Range(0, 10_001).Select(i => $"\"p{i}\":1")) + "}";
        Assert.Equal(10_000, JsonSecret.Flatten(big)!.Count);

        var properties = JsonSecret.Flatten("""{"a.b":"literal","a":{"b":"nested"}}""");
        Assert.Equal("a.b", JsonSecret.FindProperty(properties, "a.b"));
        Assert.Equal("a:b", JsonSecret.FindProperty(properties, "a:b"));
        Assert.Null(JsonSecret.FindProperty(null, "a"));
        Assert.Null(JsonSecret.FindProperty(properties, "c"));
    }

    [Fact]
    public void Reads_links_with_an_empty_secret_name()
    {
        var options = new AwsSecretsTraceOptions();
        options.Links["X"] = "#p";
        options.Links["Y"] = "s#p#q";

        Assert.Equal((string.Empty, "p"), options.FindLink("X"));
        Assert.Equal(("s", "p#q"), options.FindLink("Y"));
        Assert.Null(options.FindLink(null));
        Assert.Null(options.FindLink("Z"));
        Assert.Equal("s#p", new AwsSecretsTraceOptions().Map("A", "s", "p").Links["A"]);
        Assert.Equal("s", new AwsSecretsTraceOptions().Map("A", "s").Links["A"]);
    }

    [Fact]
    public void Treats_an_empty_external_property_as_the_whole_value()
    {
        var secrets = new AwsSecrets([new AwsSecret("s") { Value = "v" }]);
        var parameters = new AwsParameters("/p/", [new("/p/a", AwsParameterType.String, "v")]);
        var layer = (string key) => new ProvenanceStep { Kind = LayerKind.KubernetesSecret, Source = "es", Reference = new ExternalSecretRef("aws", null, key, string.Empty) };

        Assert.Equal(new AwsSecretRef("s"), Assert.Single(new AwsSecretsResolver(secrets, new()).Resolve(layer("s"), new TraceDiagnostics())).Reference);
        var step = Assert.Single(new AwsParametersResolver(parameters, new()).Resolve(layer("/p/a"), new TraceDiagnostics()));
        Assert.Equal("v", step.Value);
        Assert.Equal("AWS Parameter Store parameter /p/a", step.Source);
    }

    [Fact]
    public void Links_external_secrets_to_parameters_only_for_slash_names_and_ssm_ARNs()
    {
        var parameters = new AwsParameters("/p/", [new("plain", AwsParameterType.String, "v"), new("/p/a", AwsParameterType.String, "v", Arn: "arn:aws:ssm:x:1:parameter/p/a")]);
        var resolver = new AwsParametersResolver(parameters, new());
        var layer = (string key) => new ProvenanceStep { Kind = LayerKind.KubernetesSecret, Source = "es", Reference = new ExternalSecretRef("aws", null, key) };

        Assert.Empty(resolver.Resolve(layer("plain"), new TraceDiagnostics()));
        Assert.Single(resolver.Resolve(layer("arn:aws:ssm:x:1:parameter/p/a"), new TraceDiagnostics()));
    }

    [Fact]
    public void Skips_a_parameter_whose_key_is_empty()
    {
        var parameters = new AwsParameters("/p/", [new("/p/", AwsParameterType.String, "root")]);
        var layer = new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "SystemsManagerConfigurationProvider", ConfigurationKey = string.Empty };

        Assert.Empty(new AwsParametersResolver(parameters, new()).Resolve(layer, new TraceDiagnostics()));
    }

    [Fact]
    public void Marks_exact_parameter_name_matches_inferred()
    {
        var parameters = new AwsParameters("/p/", [new("/q/a", AwsParameterType.String, "v")]);
        var layer = new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "env", Reference = new EnvironmentVariableRef("/q/a") };

        Assert.True(Assert.Single(new AwsParametersResolver(parameters, new()).Resolve(layer, new TraceDiagnostics())).IsInferred);
    }

    [Fact]
    public void Prefers_an_entry_ARN_location_over_the_snapshot_location()
    {
        var parameters = new AwsParameters("/p/", [new("/p/a", AwsParameterType.String, "v", Arn: "arn:aws:ssm:us-east-2:42:parameter/p/a"), new("/p/b", AwsParameterType.String, "v")], location: "eu-west-1");
        var secrets = new AwsSecrets([new AwsSecret("a") { Arn = "arn:aws:secretsmanager:us-east-2:42:secret:a-AbCdEf" }, new AwsSecret("b")], location: "eu-west-1");

        Assert.Equal(["us-east-2, account 42", "eu-west-1"], parameters.ToLoadedSource("p", new()).Entries.Select(e => e.Location));
        Assert.Equal(["us-east-2, account 42", "eu-west-1"], secrets.ToLoadedSource("s", new()).Entries.Select(e => e.Location));
    }

    [Fact]
    public void Uses_the_options_defaults()
    {
        Assert.True(new AwsParameterStoreOptions().Recursive);
        Assert.False(new AwsParameterStoreOptions().IncludeValues);
        Assert.Equal(string.Empty, new AwsParameterStoreOptions().Path);
        Assert.False(new AwsSecretsManagerOptions().IncludeValues);
        Assert.True(new AwsSecretsTraceOptions().MatchByName);
        Assert.True(new AwsSecretsTraceOptions().MatchConfigurationKeys);
        Assert.True(new AwsParametersTraceOptions().MatchByName);
        Assert.True(new AwsParametersTraceOptions().MatchConfigurationKeys);
    }

    [Fact]
    public void Applies_the_configure_callbacks()
    {
        const string prefix = "CFAWSEDGE_";
        Environment.SetEnvironmentVariable(prefix + "X", "1");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            var secrets = new AwsSecrets([new AwsSecret(prefix + "X")]);
            var parameters = new AwsParameters("/p/", [new(prefix + "X", AwsParameterType.String, "1")]);

            var matched = ConfigTracer.For(configuration).WithAwsSecretsManager(secrets).WithAwsParameterStore(parameters).Trace();
            var unmatched = ConfigTracer.For(configuration)
                .WithAwsSecretsManager(secrets, o => o.MatchByName = false)
                .WithAwsParameterStore(parameters, o => o.MatchByName = false)
                .Trace();

            Assert.Equal(2, matched.Find("X")!.EffectiveStep!.Origins.Count);
            Assert.Empty(unmatched.Find("X")!.EffectiveStep!.Origins);
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "X", null);
        }
    }

    [Fact]
    public void Gives_exact_messages_for_access_keys_and_the_first_problem_first()
    {
        var ex = Assert.Throws<ArgumentException>(() => AwsClients.CreateSecretsManager(new AwsSecretsManagerOptions { AccessKeyId = "A" }));
        Assert.Equal(
            "AccessKeyId and SecretAccessKey must be set together (and SessionToken only with both). Leave all of them unset to use the default AWS credential chain or a profile. (Parameter 'options')",
            ex.Message);

        var both = Assert.Throws<ArgumentException>(() => new AwsSecretsManagerProvider().Create(new Dictionary<string, string?> { ["ServiceUrl"] = "http://x.example", ["AccessKeyId"] = "A" }));
        Assert.StartsWith("ServiceUrl must use https", both.Message, StringComparison.Ordinal);
        Assert.Equal("p", ((AwsSecretsManagerSource)new AwsSecretsManagerProvider().Create(new Dictionary<string, string?> { ["Profile"] = "p" })).Options.Profile);
    }

    [Fact]
    public void Describes_where_requests_go()
    {
        Assert.Equal("https://x.example/", AwsClients.Describe(new Amazon.SecretsManager.AmazonSecretsManagerConfig { ServiceURL = "https://x.example/" }));

        // Without a ServiceURL the SDK falls back to the machine's region (AWS_REGION, a profile, or the EC2 instance
        // metadata on a cloud runner), and requests go there. Only a machine with no region shows AuthenticationRegion.
        var machineRegion = new Amazon.SecretsManager.AmazonSecretsManagerConfig().RegionEndpoint?.SystemName;
        Assert.Equal(machineRegion ?? "ap-south-1", AwsClients.Describe(new Amazon.SecretsManager.AmazonSecretsManagerConfig { AuthenticationRegion = "ap-south-1" }));
    }

    [Fact]
    public void Names_keys_without_a_section_path_for_both_sources()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ServiceUrl"] = "http://x.example/" }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddConfigFerretAwsSecretsManager(configuration);
        services.AddConfigFerretAwsParameterStore(configuration);
        using var provider = services.BuildServiceProvider();

        var secrets = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AwsSecretsManagerOptions>>().Value);
        var parameters = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AwsParameterStoreOptions>>().Value);

        Assert.StartsWith("ServiceUrl must use https", secrets.Failures.Single(), StringComparison.Ordinal);
        Assert.StartsWith("Path must be set", parameters.Failures.First(), StringComparison.Ordinal);
        Assert.StartsWith("ServiceUrl must use https", parameters.Failures.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public void Fails_validation_for_a_key_in_appsettings_without_a_section_path()
    {
        using var folder = new TempSettingsFolder();
        var file = folder.Write("appsettings.json", """{ "SecretAccessKey": "committed", "AccessKeyId": "A" }""");
        var configuration = new ConfigurationBuilder().AddJsonFile(file).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddConfigFerretAwsSecretsManager(configuration);
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AwsSecretsManagerOptions>>().Value);

        Assert.Equal(2, ex.Failures.Count());
        Assert.StartsWith("AccessKeyId is set in appsettings.json", ex.Failures.First(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SecretList")]
    [InlineData("Tags")]
    [InlineData("SecretValues")]
    [InlineData("Errors")]
    [InlineData("Parameters")]
    [InlineData("ExternalSecretRotationMetadata")]
    [InlineData("ReplicationStatus")]
    [InlineData("Filters")]
    [InlineData("RotationRules")]
    [InlineData("SecretVersionsToStages")]
    [InlineData("VersionStages")]
    [InlineData("Values")]
    public void Refuses_a_plain_value_in_place_of_each_known_list_or_structure(string member)
    {
        Assert.False(AwsResponseGuard.IsReadable(Encoding.UTF8.GetBytes($$"""{"{{member}}":"x"}""")));
        Assert.True(AwsResponseGuard.IsReadable(Encoding.UTF8.GetBytes($$"""{"{{member}}":null}""")));
    }

    [Fact]
    public async Task Passes_unreadable_error_bodies_through_for_the_SDK_to_report()
    {
        var inner = new FakeAws().On("ListSecrets", "<html>bad gateway</html>", HttpStatusCode.BadGateway);
        using var invoker = new HttpMessageInvoker(new AwsResponseGuard(inner, disposeInner: false));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://x.example/") { Content = new StringContent("{}") };
        request.Headers.Add("X-Amz-Target", "secretsmanager.ListSecrets");

        using var response = await invoker.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("<html>bad gateway</html>", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public void Disposes_the_inner_handler_only_when_it_owns_it()
    {
        var owned = new TrackingHandler();
        var borrowed = new TrackingHandler();

        new AwsResponseGuard(owned, disposeInner: true).Dispose();
        new AwsResponseGuard(borrowed, disposeInner: false).Dispose();
        new GuardedHttpClientFactory(borrowed).CreateHttpClient(new Amazon.SecretsManager.AmazonSecretsManagerConfig()).Dispose();

        Assert.True(owned.Disposed);
        Assert.False(borrowed.Disposed);
    }

    [Fact]
    public void Provider_settings_say_what_to_enter()
    {
        var expected = new Dictionary<string, (string Label, string Help)>
        {
            ["Region"] = ("Region", "For example eu-west-1. Empty uses the profile's or the environment's region."),
            ["Profile"] = ("Profile", "A named profile from the shared AWS config and credentials files. Empty uses the default credential chain."),
            ["ServiceUrl"] = ("Service URL", "Another endpoint, such as a VPC endpoint or LocalStack. Must use https except on loopback."),
            ["AccessKeyId"] = ("Access key id", "Only when not using a profile or the default credential chain."),
            ["SecretAccessKey"] = ("Secret access key", "The secret for the access key id."),
            ["SessionToken"] = ("Session token", "For temporary credentials."),
            ["NamePrefix"] = ("Name prefix", "Read only secrets whose name starts with this, for example prod/orders/."),
            ["KeyPrefix"] = ("Key prefix", "The part of secret names that is not part of the .NET key. Empty uses the name prefix."),
            ["Path"] = ("Path", "The path to read under, the same one passed to AddSystemsManager, for example /orders/prod/."),
            ["Recursive"] = ("Recursive", "Read the whole hierarchy under the path."),
        };
        var secrets = new AwsSecretsManagerProvider().Settings;
        var parameters = new AwsParameterStoreProvider().Settings;

        foreach (var setting in secrets.Concat(parameters).Where(s => expected.ContainsKey(s.Name)))
        {
            Assert.Equal(expected[setting.Name], (setting.Label, setting.Help!));
        }

        Assert.Equal(("Read values", "Also read secret values, to show JSON properties and compare values. Values are always redacted."), Pick(secrets, "IncludeValues"));
        Assert.Equal(("Decrypt SecureString values", "Also decrypt SecureString values. String values are always read. Values are always redacted."), Pick(parameters, "IncludeValues"));
        Assert.Equal(("Match by name", "Link environment variables and keys to secrets with the same name or .NET key."), Pick(secrets, "MatchByName"));
        Assert.Equal(("Match by name", "Link environment variables and keys to parameters with the same name or .NET key."), Pick(parameters, "MatchByName"));
        Assert.Equal(SourceSettingKind.Boolean, parameters.Single(s => s.Name == "Recursive").Kind);
        Assert.Equal("true", parameters.Single(s => s.Name == "Recursive").DefaultValue);
        Assert.Equal(SourceSettingKind.Text, parameters[0].Kind);
        Assert.True(parameters[0].Required);
    }

    private static (string, string) Pick(IReadOnlyList<SourceSetting> settings, string name)
    {
        var setting = settings.Single(s => s.Name == name);
        return (setting.Label, setting.Help!);
    }

    private sealed class TrackingHandler : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _sent = [];

        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            _sent.Add(response);
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            _sent.ForEach(r => r.Dispose());
            base.Dispose(disposing);
        }
    }
}
