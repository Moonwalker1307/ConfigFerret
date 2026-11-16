using System.Net;
using ConfigFerret.Shared;
using ConfigFerret.Tests.Shared;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Tests.Common;

/// <summary>
/// Tests for the helpers in src/Shared, which are compiled into every extension package that compiles src/Shared. This
/// file is compiled into the test project of each of those packages, so each copy is tested.
/// </summary>
public class JsonApiTests
{
    private static readonly Uri Api = new("https://api.example.com/v1/items");

    private static Task<System.Text.Json.JsonDocument> Get(FakeHttpHandler handler) =>
        JsonApi.GetAsync(handler.Client(), Api, h => h.Add("X-Test", "1"), "Example", TestContext.Current.CancellationToken);

    [Fact]
    public void Requires_an_absolute_address()
    {
        var ex = Assert.Throws<ArgumentException>(() => JsonApi.RequireSafeBaseAddress(null, allowUnencryptedHttp: false, "Address"));
        Assert.StartsWith("Address must be an absolute URL.", ex.Message, StringComparison.Ordinal);

        ex = Assert.Throws<ArgumentException>(() => JsonApi.RequireSafeBaseAddress(new Uri("api/v1", UriKind.Relative), allowUnencryptedHttp: true, "Address"));
        Assert.StartsWith("Address must be an absolute URL.", ex.Message, StringComparison.Ordinal);
        Assert.Equal("Address", ex.ParamName);
    }

    [Theory]
    [InlineData("https://example.com/api", false, "https://example.com/api/")]
    [InlineData("https://example.com/api/", false, "https://example.com/api/")]
    [InlineData("http://example.com/api/", true, "http://example.com/api/")]
    [InlineData("http://localhost:8080/", false, "http://localhost:8080/")]
    [InlineData("http://127.0.0.1/", false, "http://127.0.0.1/")]
    public void Accepts_https_and_allowed_or_loopback_http_with_a_trailing_slash(string address, bool allowHttp, string expected)
    {
        Assert.Equal(new Uri(expected), JsonApi.RequireSafeBaseAddress(new Uri(address), allowHttp, "Address"));
    }

    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("ftp://example.com/")]
    public void Refuses_a_scheme_that_sends_the_credential_in_clear_text(string address)
    {
        var ex = Assert.Throws<ArgumentException>(() => JsonApi.RequireSafeBaseAddress(new Uri(address), allowUnencryptedHttp: false, "Address"));
        Assert.StartsWith("Address must use https", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"a":[1,"x",{"b":null,"c":true}],"d":"\u00e9\ud83d\ude00"}""", true)]
    [InlineData("""{"a":"\ud800"}""", false)]
    [InlineData("""{"a":{"\udc00":1}}""", false)]
    [InlineData("""[["\ud800x"]]""", false)]
    public void RequireValidText_refuses_lone_surrogates_anywhere(string json, bool valid)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var ex = Record.Exception(() => JsonApi.RequireValidText(doc, "Example", Api));
        if (valid)
        {
            Assert.Null(ex);
        }
        else
        {
            var http = Assert.IsType<HttpRequestException>(ex);
            Assert.Equal("Example returned a response for /v1/items whose text is not valid UTF-16.", http.Message);
        }
    }

    [Fact]
    public void Refuses_ftp_even_when_http_is_allowed()
    {
        Assert.Throws<ArgumentException>(() => JsonApi.RequireSafeBaseAddress(new Uri("ftp://example.com/"), allowUnencryptedHttp: true, "Address"));
    }

    [Fact]
    public void Refuses_credentials_in_the_address()
    {
        var ex = Assert.Throws<ArgumentException>(() => JsonApi.RequireSafeBaseAddress(new Uri("https://user:secret@example.com/"), allowUnencryptedHttp: false, "Address"));
        Assert.StartsWith("Address must not hold credentials. Pass them in the options instead.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Creates_a_client_with_a_timeout()
    {
        using var client = JsonApi.CreateClient();

        Assert.Equal(TimeSpan.FromSeconds(100), client.Timeout);
    }

    // A query and a credential header, neither of which may show up in an error message.
    private static readonly Uri Paged = new("https://api.example.com/v1/items?page=2&per_page=30");

    private const string FakeToken = "fake-token-never-printed";

    private static Task<System.Text.Json.JsonDocument> Get(HttpClient client, CancellationToken cancellationToken) =>
        JsonApi.GetAsync(client, Paged, h => h.Add("X-Test-Token", FakeToken), "Example", cancellationToken);

    [Fact]
    public async Task Fails_with_the_client_timeout_when_the_body_stops_arriving()
    {
        using var handler = new ScriptedHandler(_ => Respond(HttpStatusCode.OK, new StalledStream()));
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(300) };

        // The timeout fires after 0.3 seconds, or a second on a busy machine. The minute only ends the test when it never fires.
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => Get(client, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));

        Assert.Equal("Example did not send a complete response for /v1/items within 0.3 seconds.", ex.Message);
        Assert.IsType<OperationCanceledException>(ex.InnerException, exactMatch: false);
    }

    [Fact]
    public async Task Fails_with_the_client_timeout_when_the_headers_do_not_arrive()
    {
        using var handler = new ScriptedHandler(NeverAnswer);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(300) };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => Get(client, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));

        Assert.Equal("Example did not send a complete response for /v1/items within 0.3 seconds.", ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Leaves_a_cancellation_by_the_caller_as_a_cancellation(bool headersArrive)
    {
        using var handler = new ScriptedHandler(headersArrive ? _ => Respond(HttpStatusCode.OK, new StalledStream()) : NeverAnswer);
        using var client = new HttpClient(handler);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Get(client, cancel.Token));
    }

    [Fact]
    public async Task Refuses_a_body_declared_larger_than_the_limit_without_reading_it()
    {
        var body = new SpacesStream(JsonApi.MaxResponseBytes + 1L);
        using var handler = new ScriptedHandler(_ => Respond(HttpStatusCode.OK, body, JsonApi.MaxResponseBytes + 1L));
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Get(client, TestContext.Current.CancellationToken));

        Assert.Equal("Example returned a response for /v1/items that is larger than 64 MB.", ex.Message);
        Assert.Equal(0, body.BytesRead);
    }

    [Fact]
    public async Task Stops_reading_a_body_without_a_length_at_the_limit()
    {
        const long length = JsonApi.MaxResponseBytes + (1024L * 1024);
        var body = new SpacesStream(length);
        using var handler = new ScriptedHandler(_ => Respond(HttpStatusCode.OK, body));
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Get(client, TestContext.Current.CancellationToken));

        Assert.Equal("Example returned a response for /v1/items that is larger than 64 MB.", ex.Message);
        Assert.InRange(body.BytesRead, JsonApi.MaxResponseBytes, length - 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(JsonApi.MaxResponseBytes + 1L)]
    public async Task Reads_only_the_start_of_an_error_body(long? declaredLength)
    {
        // An error answer is read only for its reason, so a large one is not read to the end, whatever it declares.
        var body = new SpacesStream(JsonApi.MaxResponseBytes + (1024L * 1024));
        using var handler = new ScriptedHandler(_ => Respond(HttpStatusCode.InternalServerError, body, declaredLength));
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Get(client, TestContext.Current.CancellationToken));

        Assert.Equal("Example returned 500 InternalServerError for /v1/items.", ex.Message);
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.InRange(body.BytesRead, 1, 1024 * 1024);
    }

    [Theory]
    [InlineData("the credential cf-fake-credential-0042 was revoked")]
    [InlineData("credential ending fake-cre is not valid")]
    [InlineData("CF-FAKE is not a known prefix")]
    public async Task Leaves_out_a_reason_that_repeats_part_of_the_credential(string reason)
    {
        var handler = new FakeHttpHandler().Respond("/v1/items", System.Text.Json.JsonSerializer.Serialize(new { message = reason }), HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => JsonApi.GetAsync(
            handler.Client(), Api, h => h.Add("X-Test", "1"), "Example", null, "cf-fake-credential-0042", TestContext.Current.CancellationToken));

        Assert.Equal("Example returned 401 Unauthorized for /v1/items.", ex.Message);
    }

    [Theory]
    [InlineData("Your token's lifetime is longer than your organization allows; see https://github.com/settings/tokens", true)]
    [InlineData("token github_pat_11AB was revoked", false)]
    [InlineData("token ending 9fkQ was revoked", false)]
    public async Task Compares_only_the_secret_part_of_a_token_with_a_public_prefix(string reason, bool shown)
    {
        // Every fine-grained GitHub token starts with github_pat_, so a reason that says github gives nothing away.
        const string token = "github_pat_11ABfake0credential9fkQ";
        var handler = new FakeHttpHandler().Respond("/v1/items", System.Text.Json.JsonSerializer.Serialize(new { message = reason }), HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => JsonApi.GetAsync(
            handler.Client(), Api, h => h.Add("X-Test", "1"), "Example", null, token, TestContext.Current.CancellationToken));

        Assert.Equal("Example returned 401 Unauthorized for /v1/items." + (shown ? " " + reason : string.Empty), ex.Message);
    }

    [Fact]
    public async Task Reads_the_body_in_the_charset_the_server_names()
    {
        // UTF-16 with no byte order mark, so only the charset says how to read it.
        using var handler = new ScriptedHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"name\":\"caf\u00e9\"}", System.Text.Encoding.Unicode, "application/json"),
        }));
        using var client = new HttpClient(handler);

        using var doc = await Get(client, TestContext.Current.CancellationToken);

        Assert.Equal("caf\u00e9", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Sends_a_user_agent_and_the_callers_headers()
    {
        var handler = new FakeHttpHandler().Respond("/v1/items", """{"ok":true}""");

        using var doc = await Get(handler);

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var request = Assert.Single(handler.Requests);
        var agent = Assert.Single(request.Headers.UserAgent);
        Assert.Equal("ConfigFerret", agent.Product?.Name);
        Assert.False(string.IsNullOrEmpty(agent.Product?.Version));
        Assert.Equal("1", Assert.Single(request.Headers.GetValues("X-Test")));
    }

    [Fact]
    public async Task Wraps_a_success_that_is_not_JSON()
    {
        var handler = new FakeHttpHandler().Respond("/v1/items", "<html>proxy login</html>");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Get(handler));

        Assert.Equal("Example returned a response that is not JSON for /v1/items.", ex.Message);
        Assert.IsType<System.Text.Json.JsonException>(ex.InnerException, exactMatch: false);
    }

    [Theory]
    [InlineData(299, false)]
    [InlineData(300, true)]
    [InlineData(399, true)]
    [InlineData(400, false)]
    public async Task Explains_redirects_only_for_3xx(int status, bool redirect)
    {
        var handler = new FakeHttpHandler().Respond("/v1/items", "", (HttpStatusCode)status);

        var ex = await Record.ExceptionAsync(() => Get(handler));

        if (status < 300)
        {
            // 299 is a success with an empty body, which is not JSON.
            Assert.DoesNotContain("Redirects", ex!.Message, StringComparison.Ordinal);
            return;
        }

        var http = Assert.IsType<HttpRequestException>(ex);
        Assert.Equal((HttpStatusCode)status, http.StatusCode);
        Assert.StartsWith($"Example returned {status} ", http.Message, StringComparison.Ordinal);
        Assert.Equal(redirect, http.Message.Contains("Redirects are not followed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"message":"Bad credentials"}""", "Example returned 401 Unauthorized for /v1/items. Bad credentials")]
    [InlineData("""{"ErrorMessage":"You must be logged in"}""", "Example returned 401 Unauthorized for /v1/items. You must be logged in")]
    [InlineData("""{"message":42}""", "Example returned 401 Unauthorized for /v1/items.")]
    [InlineData("""{"other":"x"}""", "Example returned 401 Unauthorized for /v1/items.")]
    [InlineData("""["message"]""", "Example returned 401 Unauthorized for /v1/items.")]
    [InlineData("<html>nope</html>", "Example returned 401 Unauthorized for /v1/items.")]
    [InlineData("""{"errors":["permission denied"]}""", "Example returned 401 Unauthorized for /v1/items. permission denied")]
    [InlineData("""{"errors":["a",1,"b","c","d"]}""", "Example returned 401 Unauthorized for /v1/items. a; b; c")]
    [InlineData("""{"errors":[]}""", "Example returned 401 Unauthorized for /v1/items.")]
    [InlineData("""{"errors":"x"}""", "Example returned 401 Unauthorized for /v1/items.")]
    [InlineData("""{"error":{"code":403,"message":"Permission denied.","status":"PERMISSION_DENIED"}}""", "Example returned 401 Unauthorized for /v1/items. Permission denied.")]
    [InlineData("""{"error":{"message":7}}""", "Example returned 401 Unauthorized for /v1/items.")]
    [InlineData("""{"error":"x"}""", "Example returned 401 Unauthorized for /v1/items. x")]
    [InlineData("""{"error":"invalid_token","error_description":"Token is expired. You can either do re-authorization or token refresh."}""", "Example returned 401 Unauthorized for /v1/items. invalid_token: Token is expired. You can either do re-authorization or token refresh.")]
    [InlineData("""{"error_description":"d","error":7}""", "Example returned 401 Unauthorized for /v1/items. d")]
    [InlineData("""{"message":"a\u001b[2Jb\u202ec\r\nd\u0000e\u2028f"}""", "Example returned 401 Unauthorized for /v1/items. a[2Jbcdef")]
    [InlineData("""{"message":"\u001b\r\n\u200b"}""", "Example returned 401 Unauthorized for /v1/items.")]
    [InlineData("""{"message":"\ud800"}""", "Example returned 401 Unauthorized for /v1/items.")]
    public async Task Adds_the_servers_error_message_when_there_is_one(string body, string expected)
    {
        var handler = new FakeHttpHandler().Respond("/v1/items", body, HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Get(handler));

        Assert.Equal(expected, ex.Message);
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(201, true)]
    public async Task Cuts_a_long_error_message(int length, bool cut)
    {
        var text = new string('x', length);
        var handler = new FakeHttpHandler().Respond("/v1/items", $$"""{"message":"{{text}}"}""", HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Get(handler));

        var expected = cut ? new string('x', 200) + "..." : text;
        Assert.Equal($"Example returned 400 BadRequest for /v1/items. {expected}", ex.Message);
    }

    [Fact]
    public void Builds_keys_and_environment_variable_names()
    {
        Assert.Equal("Token", CredentialGuard.Key("", "Token"));
        Assert.Equal("A:B:Token", CredentialGuard.Key("A:B", "Token"));
        Assert.Equal("A__B__Token", CredentialGuard.EnvironmentVariable("A:B:Token"));
    }

    [Fact]
    public void Says_where_to_move_a_credential_found_in_appsettings()
    {
        var message = CredentialGuard.InAppSettings("A:Token", "appsettings.json");

        Assert.Equal(
            "A:Token is set in appsettings.json. Credentials must not be in appsettings files, which are usually committed. "
            + "Remove it there and use user secrets in development, and an environment variable (A__Token) or a key vault elsewhere.",
            message);
    }

    [Fact]
    public void Returns_the_address_problem_without_the_parameter_suffix()
    {
        Assert.Null(CredentialGuard.CheckAddress(new Uri("https://example.com/"), allowUnencryptedHttp: false, "A:Address"));
        Assert.Equal("A:Address must be an absolute URL.", CredentialGuard.CheckAddress(null, allowUnencryptedHttp: false, "A:Address"));
    }

    [Fact]
    public void Finds_a_credential_in_an_appsettings_file_but_not_in_other_json_files()
    {
        using var folder = new TempSettingsFolder();
        var other = folder.Write("settings.json", """{ "A": { "Token": "x" } }""");
        var json = folder.Write("appsettings.txt", """{ "A": { "Token": "x" } }""");
        var blank = folder.Write("appsettings.Development.json", """{ "A": { "Token": " " } }""");
        var configuration = new ConfigurationBuilder().AddJsonFile(other).AddJsonFile(json).AddJsonFile(blank).Build();

        Assert.Null(CredentialGuard.CheckSource(configuration, "A:Token", "x"));

        var appsettings = folder.Write("APPSETTINGS.Staging.JSON", """{ "A": { "Token": "x" } }""");
        var withFile = new ConfigurationBuilder().AddJsonFile(other).AddJsonFile(appsettings).Build();
        Assert.Equal(CredentialGuard.InAppSettings("A:Token", "APPSETTINGS.Staging.JSON"), CredentialGuard.CheckSource(withFile, "A:Token", "x"));
    }

    [Fact]
    public void Looks_inside_chained_configuration_and_past_a_chain_without_one()
    {
        using var folder = new TempSettingsFolder();
        var clean = new ConfigurationBuilder().AddJsonFile(folder.Write("settings.json", """{ "A": { "Token": "x" } }""")).Build();
        var dirty = new ConfigurationBuilder().AddJsonFile(folder.Write("appsettings.json", """{ "A": { "Token": "x" } }""")).Build();

        var configuration = new ConfigurationBuilder().AddConfiguration(clean).AddConfiguration(dirty).Build();

        Assert.Equal(CredentialGuard.InAppSettings("A:Token", "appsettings.json"), CredentialGuard.CheckSource(configuration, "A:Token", "x"));
    }

    [Fact]
    public void Checks_a_section_through_the_root_it_reads_from()
    {
        using var folder = new TempSettingsFolder();
        var file = folder.Write("appsettings.json", """{ "A": { "Token": "fake-token" } }""");
        var fromFile = new ConfigurationBuilder().AddJsonFile(file).Build();
        using var manager = new ConfigurationManager();
        manager.AddJsonFile(file);
        var fromMemory = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["A:Token"] = "fake-token" }).Build();

        Assert.Equal(CredentialGuard.InAppSettings("A:Token", "appsettings.json"), CredentialGuard.CheckSource(fromFile.GetSection("A"), "A:Token", "fake-token"));
        Assert.Equal(CredentialGuard.InAppSettings("A:Token", "appsettings.json"), CredentialGuard.CheckSource(manager.GetSection("A"), "A:Token", "fake-token"));
        Assert.Null(CredentialGuard.CheckSource(fromMemory.GetSection("A"), "A:Token", "fake-token"));
    }

    [Fact]
    public void Refuses_a_credential_that_is_set_when_there_is_no_configuration_root()
    {
        var lone = new LoneSection("A", new Dictionary<string, string?> { ["Token"] = "fake-token" });

        Assert.Equal(CredentialGuard.NoRoot("A:Token"), CredentialGuard.CheckSource(lone, "A:Token", "fake-token"));
        Assert.Equal(CredentialGuard.NoRoot("A:Token"), CredentialGuard.CheckSource(null, "A:Token", "fake-token"));
        Assert.Null(CredentialGuard.CheckSource(lone, "A:Token", " "));
        Assert.Null(CredentialGuard.CheckSource(null, "A:Token", null));
        Assert.DoesNotContain("fake-token", CredentialGuard.NoRoot("A:Token"), StringComparison.Ordinal);
    }

    [Fact]
    public void Looks_inside_providers_that_wrap_another_one()
    {
        using var folder = new TempSettingsFolder();
        var file = folder.Write("appsettings.json", """{ "A": { "Token": "fake-token" } }""");
        var recorded = new ConfigurationBuilder().AddJsonFile(file);
        var reads = recorded.RecordReads();
        var wrapped = new ConfigurationBuilder().Add(new WrappingSource(new ConfigurationBuilder().AddJsonFile(file).Sources[0]));

        Assert.Equal(CredentialGuard.InAppSettings("A:Token", "appsettings.json"), CredentialGuard.CheckSource(recorded.Build(), "A:Token", "x"));
        Assert.Equal(CredentialGuard.InAppSettings("A:Token", "appsettings.json"), CredentialGuard.CheckSource(wrapped.Build(), "A:Token", "x"));
        Assert.False(reads.WasRead("A:Token"));
    }

    [Fact]
    public void Checks_a_chained_section_through_the_root_it_reads_from()
    {
        using var folder = new TempSettingsFolder();
        var file = folder.Write("appsettings.json", """{ "Outer": { "A": { "Token": "fake-token" } } }""");
        var fromFile = new ConfigurationBuilder().AddJsonFile(file).Build();
        using var manager = new ConfigurationManager();
        manager.AddJsonFile(file);
        var fromMemory = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Outer:A:Token"] = "fake-token" }).Build();

        Assert.Equal(
            CredentialGuard.InAppSettings("A:Token", "appsettings.json"),
            CredentialGuard.CheckSource(new ConfigurationBuilder().AddConfiguration(fromFile.GetSection("Outer")).Build(), "A:Token", "x"));
        Assert.Equal(
            CredentialGuard.InAppSettings("A:Token", "appsettings.json"),
            CredentialGuard.CheckSource(new ConfigurationBuilder().AddConfiguration(manager.GetSection("Outer")).Build(), "A:Token", "x"));
        Assert.Null(CredentialGuard.CheckSource(new ConfigurationBuilder().AddConfiguration(fromMemory.GetSection("Outer")).Build(), "A:Token", "x"));
    }

    [Fact]
    public void Refuses_a_credential_from_a_configuration_whose_sources_cannot_be_seen()
    {
        var lone = new LoneSection("Outer", new Dictionary<string, string?> { ["A:Token"] = "fake-token" });
        var chained = new ConfigurationBuilder().AddConfiguration(lone).Build();
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Own:Setting"] = "1" }).Build();
        var keeping = new ConfigurationBuilder().Add(new SettingsKeepingSource(settings.GetSection("Own"))).Build();

        var message = CredentialGuard.CheckSource(chained, "A:Token", "x");

        Assert.Equal(CredentialGuard.CannotLookInto("A:Token", nameof(ChainedConfigurationProvider)), message);
        Assert.DoesNotContain("fake-token", message, StringComparison.Ordinal);
        Assert.Null(CredentialGuard.CheckSource(chained, "A:Other", "x"));
        Assert.Equal(CredentialGuard.CannotLookInto("A:Token", nameof(SettingsKeepingProvider)), CredentialGuard.CheckSource(keeping, "A:Token", "x"));
    }

    [Fact]
    public void Refuses_a_credential_from_a_chained_section_that_keeps_a_root_it_does_not_read_from()
    {
        using var folder = new TempSettingsFolder();
        var host = new ConfigurationBuilder().AddJsonFile(folder.Write("appsettings.json", """{ "Outer": { "A": { "Token": "fake-token" } } }""")).Build();
        var defaults = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Unrelated"] = "1" }).Build();
        var configuration = new ConfigurationBuilder().AddConfiguration(new FallbackSection(host.GetSection("Outer"), defaults)).Build();

        Assert.Equal(CredentialGuard.CannotLookInto("A:Token", nameof(ChainedConfigurationProvider)), CredentialGuard.CheckSource(configuration, "A:Token", "x"));
    }

    [Fact]
    public void Finds_the_root_only_of_a_root_or_a_section_that_GetSection_made()
    {
        var root = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["X:Y"] = "1" }).Build();
        using var manager = new ConfigurationManager();
        var defaults = new ConfigurationBuilder().Build();

        Assert.Same(root, CredentialGuard.RootOf(root));
        Assert.Same(root, CredentialGuard.RootOf(root.GetSection("X")));
        Assert.Same(root, CredentialGuard.RootOf(root.GetSection("X").GetSection("Y")));
        Assert.Same(manager, CredentialGuard.RootOf(manager.GetSection("X")));
        Assert.Null(CredentialGuard.RootOf(new FallbackSection(root.GetSection("X"), defaults)));
        Assert.Null(CredentialGuard.RootOf(new LoneSection("X", [])));
        Assert.Null(CredentialGuard.RootOf(null));
    }

    // A section of a type of its own, which keeps no configuration root.
    private sealed class LoneSection(string path, Dictionary<string, string?> values) : IConfigurationSection
    {
        public string? this[string key]
        {
            get => values.GetValueOrDefault(key);
            set => values[key] = value;
        }

        public string Key => path.Split(':')[^1];

        public string Path => path;

        public string? Value { get; set; }

        public IEnumerable<IConfigurationSection> GetChildren() => [];

        public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() => new Microsoft.Extensions.Primitives.CancellationChangeToken(CancellationToken.None);

        public IConfigurationSection GetSection(string key) => new LoneSection($"{path}:{key}", []);
    }

    // Keeps a section for its own settings and serves the credential, as an in-house provider might.
    private sealed class SettingsKeepingSource(IConfiguration settings) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => new SettingsKeepingProvider(settings);
    }

    private sealed class SettingsKeepingProvider(IConfiguration settings) : ConfigurationProvider
    {
        private readonly IConfiguration _settings = settings;

        public override void Load() => Data["A:Token"] = _settings["Setting"] is null ? null : "fake-token";
    }

    // Keeps the provider it wraps in a private field, as a decorator from another library might.
    private sealed class WrappingSource(IConfigurationSource inner) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => new WrappingProvider(inner.Build(builder));
    }

    private sealed class WrappingProvider(IConfigurationProvider inner) : IConfigurationProvider
    {
        private readonly IConfigurationProvider _inner = inner;

        public bool TryGet(string key, out string? value) => _inner.TryGet(key, out value);

        public void Set(string key, string? value) => _inner.Set(key, value);

        public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() => _inner.GetReloadToken();

        public void Load() => _inner.Load();

        public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath) => _inner.GetChildKeys(earlierKeys, parentPath);
    }

    [Fact]
    public async Task Hands_a_successful_response_to_the_callback_before_parsing_it()
    {
        var handler = new FakeHttpHandler().Respond(Api.PathAndQuery, """{"ok":true}""");
        using var client = handler.Client();
        HttpStatusCode? seen = null;

        using var doc = await JsonApi.GetAsync(client, Api, _ => { }, "Example", r => seen = r.StatusCode, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, seen);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Does_not_call_the_callback_for_a_failure_and_lets_it_reject_a_success()
    {
        var failing = new FakeHttpHandler().Respond(Api.PathAndQuery, "{}", HttpStatusCode.Forbidden);
        using var failingClient = failing.Client();
        var called = false;
        await Assert.ThrowsAsync<HttpRequestException>(() => JsonApi.GetAsync(failingClient, Api, _ => { }, "Example", _ => called = true, TestContext.Current.CancellationToken));
        Assert.False(called);

        var ok = new FakeHttpHandler().Respond(Api.PathAndQuery, "not json");
        using var okClient = ok.Client();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => JsonApi.GetAsync(okClient, Api, _ => { }, "Example", _ => throw new HttpRequestException("rejected"), TestContext.Current.CancellationToken));
        Assert.Equal("rejected", ex.Message);
    }

    private static Task<HttpResponseMessage> Respond(HttpStatusCode status, Stream body, long? declaredLength = null)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.ContentLength = declaredLength;
        return Task.FromResult(new HttpResponseMessage(status) { Content = content });
    }

    // A server that accepts the request and never sends the headers, so only a timeout or a cancellation ends it.
    private static async Task<HttpResponseMessage> NeverAnswer(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("Unreachable: the delay only ends by cancellation.");
    }

    private sealed class ScriptedHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(cancellationToken);
    }

    // A body whose bytes stop coming after the headers: a read ends only by cancellation.
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // A body of spaces made as it is read, with no length the client can see, that counts the bytes read.
    private sealed class SpacesStream(long length) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, length - BytesRead);
            buffer[..count].Fill((byte)' ');
            BytesRead += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
