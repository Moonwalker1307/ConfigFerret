using System.Net;
using ConfigFerret.Shared;
using ConfigFerret.Tests.Shared;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Tests.Common;

/// <summary>
/// Tests for the helpers in src/Shared, which are compiled into both the GitHub and the Octopus package. This file is
/// compiled into the GitHub and Octopus test projects only, so each copy is tested.
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

        Assert.Null(CredentialGuard.FindAppSettingsFile(configuration, "A:Token"));

        var appsettings = folder.Write("APPSETTINGS.Staging.JSON", """{ "A": { "Token": "x" } }""");
        var withFile = new ConfigurationBuilder().AddJsonFile(other).AddJsonFile(appsettings).Build();
        Assert.Equal("APPSETTINGS.Staging.JSON", CredentialGuard.FindAppSettingsFile(withFile, "A:Token"));
    }

    [Fact]
    public void Looks_inside_chained_configuration_and_past_a_chain_without_one()
    {
        using var folder = new TempSettingsFolder();
        var clean = new ConfigurationBuilder().AddJsonFile(folder.Write("settings.json", """{ "A": { "Token": "x" } }""")).Build();
        var dirty = new ConfigurationBuilder().AddJsonFile(folder.Write("appsettings.json", """{ "A": { "Token": "x" } }""")).Build();

        var configuration = new ConfigurationBuilder().AddConfiguration(clean).AddConfiguration(dirty).Build();

        Assert.Equal("appsettings.json", CredentialGuard.FindAppSettingsFile(configuration, "A:Token"));
        Assert.Null(CredentialGuard.FindAppSettingsFile(configuration.GetSection("A"), "A:Token"));
        Assert.Null(CredentialGuard.FindAppSettingsFile(null, "A:Token"));
    }
}
