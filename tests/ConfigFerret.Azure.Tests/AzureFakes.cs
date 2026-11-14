using System.Net;
using System.Text;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.AppConfiguration;
using Azure.Security.KeyVault.Secrets;

namespace ConfigFerret.Azure.Tests;

/// <summary>A credential that hands out a fixed token, so no test signs in anywhere.</summary>
internal sealed class FakeCredential : TokenCredential
{
    public const string Token = "fake-access-token-never-printed";

    public List<string[]> Scopes { get; } = [];

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        Scopes.Add(requestContext.Scopes);
        return new AccessToken(Token, DateTimeOffset.UtcNow.AddHours(1));
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(GetToken(requestContext, cancellationToken));
}

/// <summary>
/// Answers Azure SDK requests from canned responses keyed by path and query (without api-version, parameters in ordinal order), and records every
/// request. Requests without a bearer token get Key Vault's 401 challenge, the way a real vault answers them.
/// </summary>
internal sealed class FakeAzureHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body, Dictionary<string, string>? Headers)>> _responses = new(StringComparer.Ordinal);

    public List<HttpRequestMessage> Requests { get; } = [];

    public bool Challenge { get; set; } = true;

    /// <summary>Adds a response for the path and query (without api-version). Several responses for one key are sent in turn; the last one repeats.</summary>
    public FakeAzureHandler Respond(string pathAndQuery, string body, HttpStatusCode status = HttpStatusCode.OK, Dictionary<string, string>? headers = null)
    {
        if (!_responses.TryGetValue(pathAndQuery, out var queue))
        {
            _responses[pathAndQuery] = queue = new();
        }

        queue.Enqueue((status, body, headers));
        return this;
    }

    public SecretClientOptions KeyVaultOptions()
    {
        var options = new SecretClientOptions { Transport = new HttpClientTransport(new HttpClient(this, disposeHandler: false)) };
        options.Retry.MaxRetries = 0;
        return options;
    }

    public ConfigurationClientOptions AppConfigurationOptions()
    {
        var options = new ConfigurationClientOptions { Transport = new HttpClientTransport(new HttpClient(this, disposeHandler: false)) };
        options.Retry.MaxRetries = 0;
        return options;
    }

    /// <summary>The path and query of every request that carried a token, without api-version.</summary>
    public List<string> Paths => Requests.Where(r => r.Headers.Authorization is not null).Select(r => Key(r.RequestUri!)).ToList();

    internal static string Key(Uri uri)
    {
        var query = string.Join("&", uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.StartsWith("api-version=", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        return query.Length == 0 ? uri.AbsolutePath : $"{uri.AbsolutePath}?{query}";
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (Challenge && request.Headers.Authorization is null && request.Headers.All(h => h.Key != "x-ms-content-sha256"))
        {
            var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(string.Empty) };
            challenge.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer authorization=\"https://login.microsoftonline.com/72f988bf-86f1-41af-91ab-2d7cd011db47\", resource=\"https://vault.azure.net\"");
            return Task.FromResult(challenge);
        }

        var key = Key(request.RequestUri!);
        var (status, body, headers) = (HttpStatusCode.NotFound, """{"error":{"code":"NotFound","message":"Not found"}}""", (Dictionary<string, string>?)null);
        if (_responses.TryGetValue(key, out var queue) && queue.Count > 0)
        {
            (status, body, headers) = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
        }

        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers ?? [])
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return Task.FromResult(response);
    }
}
