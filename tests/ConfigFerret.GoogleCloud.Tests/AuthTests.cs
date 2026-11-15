using System.Security.Cryptography;
using System.Text.Json;
using ConfigFerret.Tests.Shared;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;

namespace ConfigFerret.GoogleCloud.Tests;

public class AuthTests
{
    private const string KeyToken = "ya29.from_service_account_key";

    private static readonly Lazy<string> PrivateKey = new(() =>
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    });

    internal static string ServiceAccountKey(string type = "service_account", string? quotaProject = null) => JsonSerializer.Serialize(new Dictionary<string, string?>
    {
        ["type"] = type,
        ["project_id"] = "contoso-prod",
        ["private_key_id"] = "k1",
        ["private_key"] = PrivateKey.Value,
        ["client_email"] = "tracer@contoso-prod.iam.gserviceaccount.com",
        ["client_id"] = "1",
        ["token_uri"] = "https://oauth2.googleapis.com/token",
        ["quota_project_id"] = quotaProject,
    });

    /// <summary>Answers token requests from a fake handler, so a service account key can be used without a network.</summary>
    private sealed class FakeTokenFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
    }

    private sealed class FakeCredential(string token) : ICredential
    {
        public List<string?> Requested { get; } = [];

        public void Initialize(ConfigurableHttpClient httpClient)
        {
        }

        public Task<string> GetAccessTokenForRequestAsync(string? authUri = null, CancellationToken cancellationToken = default)
        {
            Requested.Add(authUri);
            return Task.FromResult(token);
        }
    }

    private static FakeHttpHandler Api() => new FakeHttpHandler().Respond(GcpSecretsTests.List, $$"""{"secrets":[{{GcpSecretsTests.Secret("A")}}]}""");

    private static async Task<(GcpSecrets Secrets, HttpRequestMessage Request)> Load(Action<GcpSecretManagerOptions> configure)
    {
        var api = Api();
        using var client = api.Client();
        var options = new GcpSecretManagerOptions { ProjectId = "contoso-prod" };
        configure(options);
        var secrets = await GcpSecrets.LoadAsync(options, client, TestContext.Current.CancellationToken);
        return (secrets, Assert.Single(api.Requests));
    }

    [Fact]
    public async Task Uses_a_credential_passed_from_code()
    {
        var credential = new FakeCredential("ya29.from_code");

        var (secrets, request) = await Load(o => o.Credential = credential);

        Assert.Single(secrets.Secrets);
        Assert.Equal("ya29.from_code", request.Headers.Authorization!.Parameter);
        Assert.Equal("https://secretmanager.googleapis.com/v1/", Assert.Single(credential.Requested));
    }

    [Fact]
    public async Task Prefers_the_access_token_over_other_credentials()
    {
        var credential = new FakeCredential("ya29.from_code");

        var (_, request) = await Load(o =>
        {
            o.AccessToken = " ya29.direct ";
            o.Credential = credential;
            o.CredentialsJson = "not even json";
        });

        Assert.Equal("ya29.direct", request.Headers.Authorization!.Parameter);
        Assert.Empty(credential.Requested);
    }

    [Fact]
    public async Task Refuses_a_credential_that_gives_no_token()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Load(o => o.Credential = new FakeCredential("")));

        Assert.Equal("The Google Cloud credential gave no access token.", ex.Message);
    }

    [Fact]
    public async Task Signs_in_with_service_account_key_content_and_sends_the_quota_project()
    {
        var tokens = new FakeHttpHandler().Respond("/token", $$"""{"access_token":"{{KeyToken}}","expires_in":3600,"token_type":"Bearer"}""");

        var (_, request) = await Load(o =>
        {
            o.CredentialsJson = ServiceAccountKey(quotaProject: "billing-project");
            o.TokenHttpClientFactory = new FakeTokenFactory(tokens);
        });

        Assert.Equal(KeyToken, request.Headers.Authorization!.Parameter);
        Assert.Equal("billing-project", Assert.Single(request.Headers.GetValues("x-goog-user-project")));
        var tokenRequest = Assert.Single(tokens.Requests);
        Assert.Equal(HttpMethod.Post, tokenRequest.Method);
        Assert.Equal("oauth2.googleapis.com", tokenRequest.RequestUri!.Host);
    }

    [Fact]
    public async Task Signs_in_with_a_service_account_key_file()
    {
        using var folder = new TempSettingsFolder();
        var path = folder.Write("key.json", ServiceAccountKey());
        var tokens = new FakeHttpHandler().Respond("/token", $$"""{"access_token":"{{KeyToken}}","expires_in":3600,"token_type":"Bearer"}""");

        var (_, request) = await Load(o =>
        {
            o.CredentialsFile = $" {path} ";
            o.TokenHttpClientFactory = new FakeTokenFactory(tokens);
        });

        Assert.Equal(KeyToken, request.Headers.Authorization!.Parameter);
        Assert.False(request.Headers.Contains("x-goog-user-project"));
    }

    [Fact]
    public async Task Reports_a_refused_token_request_without_the_key()
    {
        var tokens = new FakeHttpHandler().Respond("/token", """{"error":"invalid_grant","error_description":"Invalid JWT Signature."}""", System.Net.HttpStatusCode.BadRequest);
        var key = ServiceAccountKey();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Load(o =>
        {
            o.CredentialsJson = key;
            o.TokenHttpClientFactory = new FakeTokenFactory(tokens);
        }));

        Assert.StartsWith("Could not get a Google Cloud access token: ", ex.Message, StringComparison.Ordinal);
        Assert.Contains("invalid_grant", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{not json", "CredentialsJson is not valid JSON.")]
    [InlineData("[]", "CredentialsJson is not a service account key (its type must be service_account). Use Application Default Credentials for other credential types.")]
    [InlineData("""{"type":"external_account","credential_source":{"executable":{"command":"/bin/evil"}}}""", "CredentialsJson is not a service account key (its type must be service_account). Use Application Default Credentials for other credential types.")]
    [InlineData("""{"type":"authorized_user"}""", "CredentialsJson is not a service account key (its type must be service_account). Use Application Default Credentials for other credential types.")]
    [InlineData("""{"type":"service_account","client_email":"a@b","private_key":"secret-key-material"}""", "CredentialsJson is not a valid service account key: check its client_email and private_key.")]
    public async Task Refuses_key_content_that_is_not_a_usable_service_account_key(string json, string message)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Load(o => o.CredentialsJson = json));

        Assert.Equal(message, ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task Says_when_the_key_file_cannot_be_read()
    {
        var path = Path.Join(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Load(o => o.CredentialsFile = path));

        Assert.StartsWith($"Could not read the Google Cloud credentials file {path}: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Names_the_key_file_when_its_content_is_wrong()
    {
        using var folder = new TempSettingsFolder();
        var path = folder.Write("key.json", """{"type":"authorized_user","refresh_token":"secret"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Load(o => o.CredentialsFile = path));

        Assert.StartsWith($"The Google Cloud credentials file {path} is not a service account key", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh_token", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explains_how_to_sign_in_when_Application_Default_Credentials_are_missing()
    {
        var previous = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", Path.Join(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Load(_ => { }));

            Assert.StartsWith("No Google Cloud credential was found. Set AccessToken, CredentialsJson or CredentialsFile, set GOOGLE_APPLICATION_CREDENTIALS, or run gcloud auth application-default login.", ex.Message, StringComparison.Ordinal);
            Assert.NotNull(ex.InnerException);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", previous);
        }
    }
}
