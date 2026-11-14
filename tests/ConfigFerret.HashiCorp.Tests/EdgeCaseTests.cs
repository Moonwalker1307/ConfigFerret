using System.Net;
using System.Text;
using ConfigFerret.Sources;
using ConfigFerret.Tests.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ConfigFerret.HashiCorp.Tests;

/// <summary>Boundaries and odd inputs for both sources.</summary>
public class EdgeCaseTests
{
    private const string Mounts = "/v1/sys/mounts/secret";

    private static VaultOptions Vault(Action<VaultOptions>? configure = null)
    {
        var options = new VaultOptions { Address = new Uri("https://vault.example.com/"), Token = "t" };
        configure?.Invoke(options);
        return options;
    }

    private static async Task<VaultSecrets> LoadVault(FakeHttpHandler handler, Action<VaultOptions>? configure = null)
    {
        using var client = handler.Client();
        return await VaultSecrets.LoadAsync(Vault(configure), client, TestContext.Current.CancellationToken);
    }

    private static ProvenanceStep EnvStep(string name) => new() { Kind = LayerKind.DotNetProvider, Source = "env " + name, Reference = new EnvironmentVariableRef(name) };

    [Theory]
    [InlineData("""{"data":{"options":{"version":"1"}}}""", 1)]
    [InlineData("""{"type":"kv","options":{"version":"1"},"data":{"x":1}}""", 1)]
    [InlineData("""{"type":"kv","options":{"version":""}}""", 1)]
    [InlineData("""{"type":"generic","options":{"version":"2"}}""", 2)]
    public async Task Detects_the_version_from_odd_mount_shapes_without_warnings(string mount, int expected)
    {
        var handler = new FakeHttpHandler().Respond(Mounts, mount);

        var vault = await LoadVault(handler);

        Assert.Empty(vault.Warnings);
        Assert.Equal(expected == 2 ? "/v1/secret/metadata/?list=true" : "/v1/secret/?list=true", handler.Requests[^1].RequestUri!.PathAndQuery);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData((HttpStatusCode)499)]
    public async Task Treats_a_client_error_on_detection_as_not_allowed(HttpStatusCode status)
    {
        var handler = new FakeHttpHandler().Respond(Mounts, "{}", status).Respond("/v1/sys/internal/ui/mounts/secret", "{}", status);

        var vault = await LoadVault(handler);

        Assert.Contains("version 2 is assumed", Assert.Single(vault.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fails_on_a_server_error_during_detection()
    {
        var handler = new FakeHttpHandler().Respond(Mounts, "{}", HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => LoadVault(handler));

        Assert.StartsWith("HashiCorp Vault returned 500", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accepts_the_smallest_caps_and_warns_once()
    {
        var handler = new LoopingHandler();
        using var client = new HttpClient(handler);
        var ct = TestContext.Current.CancellationToken;

        var vault = await VaultSecrets.LoadAsync(Vault(o => { o.KvVersion = 2; o.MaxDepth = 0; o.MaxSecrets = 1; o.MaxRequests = 3; }), client, ct);

        Assert.Single(vault.Secrets);
        Assert.Single(vault.Warnings, w => w.StartsWith("Did not list", StringComparison.Ordinal));

        var requests = new LoopingHandler();
        using var requestsClient = new HttpClient(requests);
        var capped = await VaultSecrets.LoadAsync(Vault(o => { o.KvVersion = 2; o.MaxRequests = 7; }), requestsClient, ct);
        Assert.Equal(7, requests.Count);
        Assert.Single(capped.Warnings, w => w.Contains("MaxRequests", StringComparison.Ordinal));

        using var secretsClient = new HttpClient(new LoopingHandler());
        var secrets = await VaultSecrets.LoadAsync(Vault(o => { o.KvVersion = 2; o.MaxSecrets = 3; }), secretsClient, ct);
        Assert.Single(secrets.Warnings);
        Assert.Single(secrets.Warnings, w => w.Contains("MaxSecrets", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Accepts_long_keys_up_to_the_limit_valid_surrogate_pairs_and_a_tilde_in_the_token()
    {
        var longest = new string('k', 1024);
        var handler = new FakeHttpHandler()
            .Respond("/v1/secret/metadata/?list=true", $$$"""{"data":{"keys":["{{{longest}}}","😀"]}}""")
            .Respond("/v1/secret/metadata/" + longest, """{"data":{}}""")
            .Respond("/v1/secret/metadata/%F0%9F%98%80", """{"data":{}}""");

        var vault = await LoadVault(handler, o =>
        {
            o.KvVersion = 2;
            o.Token = "a~b";
            o.Paths.Add(null!);
        });

        Assert.Equal([longest, "😀"], vault.Secrets.Select(s => s.Path));
        Assert.Empty(vault.Warnings);
    }

    [Fact]
    public async Task Skips_a_configured_path_with_a_lone_surrogate()
    {
        var vault = await LoadVault(new FakeHttpHandler(), o =>
        {
            o.KvVersion = 2;
            o.Paths.Add("a\ud800");
        });

        Assert.Single(vault.Warnings);
    }

    [Fact]
    public async Task Names_the_option_in_argument_errors()
    {
        using var client = new FakeHttpHandler().Client();
        var ct = TestContext.Current.CancellationToken;

        Assert.StartsWith("Mount must be", (await Assert.ThrowsAsync<ArgumentException>(() => VaultSecrets.LoadAsync(Vault(o => o.Mount = null!), client, ct))).Message, StringComparison.Ordinal);
        Assert.StartsWith("KvVersion must be", (await Assert.ThrowsAsync<ArgumentException>(() => VaultSecrets.LoadAsync(Vault(o => o.KvVersion = 0), client, ct))).Message, StringComparison.Ordinal);
        Assert.StartsWith("MaxDepth must be", (await Assert.ThrowsAsync<ArgumentException>(() => VaultSecrets.LoadAsync(Vault(o => o.MaxRequests = 0), client, ct))).Message, StringComparison.Ordinal);
        Assert.StartsWith("Token holds characters", (await Assert.ThrowsAsync<ArgumentException>(() => VaultSecrets.LoadAsync(Vault(o => o.Token = "\u007f"), client, ct))).Message, StringComparison.Ordinal);

        var consul = new ConsulOptions();
        Assert.StartsWith("Prefix must be", (await Assert.ThrowsAsync<ArgumentException>(() => ConsulKeyValues.LoadAsync(new ConsulOptions { Prefix = ".." }, client, ct))).Message, StringComparison.Ordinal);
        Assert.StartsWith("MaxKeys must be", (await Assert.ThrowsAsync<ArgumentException>(() => ConsulKeyValues.LoadAsync(new ConsulOptions { MaxKeys = 0 }, client, ct))).Message, StringComparison.Ordinal);
        Assert.StartsWith("Token holds characters", (await Assert.ThrowsAsync<ArgumentException>(() => ConsulKeyValues.LoadAsync(new ConsulOptions { Token = "ä" }, client, ct))).Message, StringComparison.Ordinal);
        Assert.Equal(10000, consul.MaxKeys);
    }

    [Fact]
    public async Task Marks_a_destroyed_version_deleted_and_a_missing_versions_map_not_deleted()
    {
        var handler = new FakeHttpHandler()
            .Respond("/v1/secret/metadata/?list=true", """{"data":{"keys":["gone","plain"]}}""")
            .Respond("/v1/secret/metadata/gone", """{"data":{"current_version":2,"versions":{"2":{"deletion_time":"","destroyed":true}}}}""")
            .Respond("/v1/secret/metadata/plain", """{"data":{"current_version":2}}""");

        var vault = await LoadVault(handler, o => o.KvVersion = 2);

        Assert.True(vault.Find("gone")!.IsDeleted);
        Assert.False(vault.Find("plain")!.IsDeleted);
    }

    [Fact]
    public async Task Prefers_metadata_times_skips_empty_keys_and_skips_missing_secrets()
    {
        var handler = new FakeHttpHandler()
            .Respond("/v1/secret/metadata/?list=true", """{"data":{"keys":["db","missing"]}}""")
            .Respond("/v1/secret/metadata/db", """{"data":{"current_version":3,"updated_time":"2026-05-05T00:00:00Z"}}""")
            .Respond("/v1/secret/data/db", """{"data":{"data":{"":"x","a":"b"},"metadata":{"version":9,"created_time":"2020-01-01T00:00:00Z"}}}""");

        var vault = await LoadVault(handler, o =>
        {
            o.KvVersion = 2;
            o.IncludeValues = true;
        });

        var db = Assert.Single(vault.Secrets);
        Assert.Equal(3, db.Version);
        Assert.Equal(new DateTimeOffset(2026, 5, 5, 0, 0, 0, TimeSpan.Zero), db.UpdatedTime);
        Assert.Equal(["a"], db.Data!.Keys);
    }

    [Fact]
    public async Task Names_what_it_skipped_when_the_token_cannot_read_metadata_or_data()
    {
        var handler = new FakeHttpHandler()
            .Respond("/v1/secret/metadata/db", """{"errors":["permission denied"]}""", HttpStatusCode.Forbidden)
            .Respond("/v1/secret/data/db", """{"errors":["permission denied"]}""", HttpStatusCode.Forbidden);

        var vault = await LoadVault(handler, o =>
        {
            o.KvVersion = 2;
            o.IncludeValues = true;
            o.Paths.Add("db");
        });

        Assert.Empty(vault.Secrets);
        Assert.StartsWith("Skipped the metadata of db in HashiCorp Vault: ", vault.Warnings[0], StringComparison.Ordinal);
        Assert.StartsWith("Skipped the data of db in HashiCorp Vault: ", vault.Warnings[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Does_not_take_a_KV1_folder_read_twice_to_be_a_secret()
    {
        var handler = new FakeHttpHandler().Respond("/v1/secret/orders/?list=true", """{"data":{"keys":["db"]}}""");

        var vault = await LoadVault(handler, o =>
        {
            o.KvVersion = 1;
            o.Paths.Add("orders");
            o.Paths.Add("orders");
        });

        Assert.Equal(["orders/db"], vault.Secrets.Select(s => s.Path));

        var single = await LoadVault(new FakeHttpHandler(), o =>
        {
            o.KvVersion = 1;
            o.Paths.Add("orders/db");
        });
        Assert.Equal(string.Empty, single.Secrets[0].RelativePath);
    }

    [Fact]
    public void Vault_resolver_handles_section_and_external_edge_cases()
    {
        var secrets = new VaultSecrets(
        [
            new("p/Db", "Db"),
            new("data/x", "data/x", new Dictionary<string, string?> { ["k"] = "1" }),
            new("x", "x", new Dictionary<string, string?> { ["k"] = "2", ["j"] = "3" }),
        ]);
        var resolver = new VaultResolver(secrets, new VaultTraceOptions { StripPrefixes = { "Y" } });
        var diagnostics = new TraceDiagnostics();
        static ProvenanceStep External(string key, string? property = null) => new() { Kind = LayerKind.KubernetesSecret, Source = "es", Reference = new ExternalSecretRef("vault", null, key, property) };

        Assert.Empty(resolver.Resolve(EnvStep("Zz"), diagnostics));
        Assert.True(Assert.Single(resolver.Resolve(EnvStep("Db__Timeout"), diagnostics)).IsInferred);
        Assert.Empty(resolver.Resolve(EnvStep("XDb__Timeout"), diagnostics));
        Assert.Single(resolver.Resolve(EnvStep("YDb__Timeout"), diagnostics));
        Assert.Equal(2, resolver.Resolve(External("x", ""), diagnostics).Count);
        Assert.Equal("1", Assert.Single(resolver.Resolve(External("secret/data/x", "k"), diagnostics)).Value);
        Assert.Equal("2", Assert.Single(resolver.Resolve(External("secret/x", "k"), diagnostics)).Value);
        Assert.Empty(resolver.Resolve(External("secret/abcdex", "k"), diagnostics));
        Assert.Contains(diagnostics.Warnings, w => w.Contains("secret/abcdex", StringComparison.Ordinal));
    }

    [Fact]
    public void Trace_options_store_links_with_and_without_keys()
    {
        var vault = new VaultTraceOptions().Map("A", "p").Map("B", "p", "k");
        Assert.Equal("p", vault.Links["A"]);
        Assert.Equal("p#k", vault.Links["B"]);
        vault.Links["C"] = "#k";
        Assert.Equal((string.Empty, "k"), vault.FindMapping("C"));
        Assert.Null(vault.FindMapping("missing"));

        var consul = new ConsulTraceOptions().Map("A", "k").Map("B", "k", "J:P");
        Assert.Equal("k", consul.Links["A"]);
        Assert.Equal("k#J:P", consul.Links["B"]);
        consul.Links["C"] = "#J";
        Assert.Equal((string.Empty, "J"), consul.FindMapping("C"));
        Assert.Null(consul.FindMapping("missing"));
    }

    [Fact]
    public async Task Consul_handles_blank_and_empty_JSON_values_and_a_null_prefix()
    {
        var handler = new FakeHttpHandler().Respond("/v1/kv/?recurse=true", $$"""
            [
              {"Key":"blank","Value":"{{B64("   ")}}"},
              {"Key":"Empty","Value":"{{B64("{}")}}"},
              {"Key":"","Value":"{{B64("{}")}}"}
            ]
            """);
        using var client = handler.Client();

        var consul = await ConsulKeyValues.LoadAsync(new ConsulOptions { Prefix = null!, ExpandJson = true }, client, TestContext.Current.CancellationToken);

        Assert.Equal(["blank", "Empty"], consul.Entries.Select(e => e.ConfigurationKey));
        Assert.Equal("   ", consul.Entries[0].Value);
        Assert.Null(consul.Entries[1].JsonPath);
        Assert.Null(consul.Entries[1].Value);
    }

    [Fact]
    public async Task Consul_expands_a_root_JSON_value_that_is_empty_to_nothing()
    {
        var handler = new FakeHttpHandler().Respond("/v1/kv/app?recurse=true", $$"""[{"Key":"app","Value":"{{B64("[]")}}"}]""");
        using var client = handler.Client();

        var consul = await ConsulKeyValues.LoadAsync(new ConsulOptions { Prefix = "app", ExpandJson = true }, client, TestContext.Current.CancellationToken);

        Assert.Empty(consul.Entries);
    }

    [Fact]
    public async Task Consul_names_itself_in_errors()
    {
        var handler = new FakeHttpHandler().Respond("/v1/kv/?recurse=true", "denied", HttpStatusCode.Forbidden);
        using var client = handler.Client();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => ConsulKeyValues.LoadAsync(new ConsulOptions(), client, TestContext.Current.CancellationToken));

        Assert.StartsWith("Consul returned 403", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validators_accept_the_smallest_caps()
    {
        Assert.Same(ValidateOptionsResult.Success, new VaultOptionsValidator("", null).Validate(null, Vault(o => { o.MaxDepth = 0; o.MaxSecrets = 1; o.MaxRequests = 1; })));
        Assert.Same(ValidateOptionsResult.Success, new ConsulOptionsValidator("", null).Validate(null, new ConsulOptions { MaxKeys = 1, Token = "t" }));
        Assert.Same(ValidateOptionsResult.Success, new ConsulOptionsValidator("", null).Validate(null, new ConsulOptions { AllowNoToken = true }));
    }

    [Fact]
    public void WithConsul_applies_the_configure_callback()
    {
        var values = new ConsulKeyValues([new("k", "Db:Timeout", "1")]);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Timeout"] = "1" }).Build();

        var tracer = ConfigTracer.For(configuration).WithConsul(values, o => o.MatchByName = false);

        Assert.NotNull(tracer.Trace().Find("Db:Timeout"));
    }

    [Theory]
    [InlineData(typeof(VaultSourceProvider))]
    [InlineData(typeof(ConsulSourceProvider))]
    public void Providers_label_and_explain_every_setting(Type type)
    {
        var provider = (IConfigSourceProvider)Activator.CreateInstance(type)!;

        Assert.All(provider.Settings, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Label));
            Assert.False(string.IsNullOrWhiteSpace(s.Help));
            Assert.EndsWith(".", s.Help, StringComparison.Ordinal);
            Assert.Equal(s.Kind == SourceSettingKind.Boolean, s.DefaultValue is "true" or "false");
        });
        Assert.Equal(["Address"], provider.Settings.Where(s => s.Required && s.Kind != SourceSettingKind.Secret).Select(s => s.Name));
        Assert.Equal(SourceSettingKind.Url, provider.Settings[0].Kind);
    }

    [Fact]
    public void Providers_describe_their_settings_in_order_with_defaults()
    {
        Assert.Equal(
            ["Address|Address|Url|", "Token|Token|Secret|", "Namespace|Namespace|Text|", "Mount|KV mount|Text|secret", "KvVersion|KV version|Text|", "Paths|Paths|Lines|", "Recursive|List sub-folders|Boolean|true", "IncludeValues|Read secret values|Boolean|false", "MatchByName|Match by name|Boolean|true", "StripPrefixes|Strip prefixes|Lines|", "AllowUnencryptedHttp|Allow plain http|Boolean|false"],
            new VaultSourceProvider().Settings.Select(s => $"{s.Name}|{s.Label}|{s.Kind}|{s.DefaultValue}"));
        Assert.Equal(
            ["Address|Address|Url|http://localhost:8500/", "Token|ACL token|Secret|", "Datacenter|Datacenter|Text|", "Prefix|Key prefix|Text|", "ExpandJson|Expand JSON values|Boolean|false", "IsSensitive|Values are secret|Boolean|false", "MatchByName|Match by name|Boolean|true", "StripPrefixes|Strip prefixes|Lines|", "AllowUnencryptedHttp|Allow plain http|Boolean|false"],
            new ConsulSourceProvider().Settings.Select(s => $"{s.Name}|{s.Label}|{s.Kind}|{s.DefaultValue}"));
        Assert.Contains("sys/mounts", new VaultSourceProvider().Settings[4].Help, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Address", null, "Address is required.")]
    [InlineData("Address", "relative", "Address must be an absolute URL.")]
    [InlineData("Token", null, "Token is required.")]
    [InlineData("KvVersion", "9", "KvVersion must be 1 or 2, or empty to detect it.")]
    [InlineData("IncludeValues", "maybe", "IncludeValues must be true or false.")]
    public void Provider_errors_name_the_setting(string name, string? value, string message)
    {
        var values = new Dictionary<string, string?> { ["Address"] = "https://v/", ["Token"] = "t", [name] = value };

        var ex = Assert.Throws<ArgumentException>(() => new VaultSourceProvider().Create(values));

        Assert.StartsWith(message, ex.Message, StringComparison.Ordinal);
    }

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    /// <summary>Every folder holds a folder named a/ and a secret named s; counts requests.</summary>
    private sealed class LoopingHandler : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _sent = [];

        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            var body = request.RequestUri!.Query.Contains("list=true", StringComparison.Ordinal) ? """{"data":{"keys":["a/","s"]}}""" : """{"data":{"current_version":1}}""";
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            _sent.Add(response);
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _sent.ForEach(r => r.Dispose());
            }

            base.Dispose(disposing);
        }
    }
}
