using System.Net;
using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SimpleSystemsManagement;

namespace ConfigFerret.Aws.Tests;

/// <summary>
/// Stands in for AWS: answers each JSON API operation (by its X-Amz-Target) with queued responses, and records every
/// request. Clients reach it through <see cref="ClientConfig.HttpClientFactory"/>, so no test touches the network.
/// </summary>
internal sealed class FakeAws : HttpMessageHandler
{
    public const string SecretKey = "wJalrXUtnFEMI/K7MDENG/never-printed";

    private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body)>> _responses = new(StringComparer.Ordinal);

    public List<(string Operation, string Body, Uri Uri)> Requests { get; } = [];

    /// <summary>Queues a response for an operation such as <c>ListSecrets</c>. The last one queued repeats.</summary>
    public FakeAws On(string operation, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        if (!_responses.TryGetValue(operation, out var queue))
        {
            _responses[operation] = queue = new();
        }

        queue.Enqueue((status, body));
        return this;
    }

    public FakeAws Fail(string operation, string errorType, HttpStatusCode status = HttpStatusCode.BadRequest) =>
        On(operation, $$"""{"__type":"{{errorType}}","message":"refused by the fake"}""", status);

    public IEnumerable<string> Operations(string operation) => Requests.Where(r => r.Operation == operation).Select(r => r.Body);

    public void Configure(ClientConfig config)
    {
        config.HttpClientFactory = new Factory(this);
        config.MaxErrorRetry = 0;
    }

    public AmazonSecretsManagerClient SecretsManager()
    {
        var config = new AmazonSecretsManagerConfig { RegionEndpoint = RegionEndpoint.EUWest1 };
        Configure(config);
        return new AmazonSecretsManagerClient(new BasicAWSCredentials("AKIDEXAMPLE", SecretKey), config);
    }

    public AmazonSimpleSystemsManagementClient ParameterStore()
    {
        var config = new AmazonSimpleSystemsManagementConfig { RegionEndpoint = RegionEndpoint.EUWest1 };
        Configure(config);
        return new AmazonSimpleSystemsManagementClient(new BasicAWSCredentials("AKIDEXAMPLE", SecretKey), config);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var target = request.Headers.TryGetValues("X-Amz-Target", out var values) ? values.First() : string.Empty;
        var operation = target[(target.IndexOf('.', StringComparison.Ordinal) + 1)..];
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Requests.Add((operation, body, request.RequestUri!));
        var (status, text) = _responses.TryGetValue(operation, out var queue) && queue.Count > 0
            ? (queue.Count > 1 ? queue.Dequeue() : queue.Peek())
            : (HttpStatusCode.BadRequest, $$"""{"__type":"UnknownOperationException","message":"no fake response for {{operation}}"}""");
        var response = new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/x-amz-json-1.1") };
        if (!IsSuccess(status) && text.Contains("__type", StringComparison.Ordinal))
        {
            response.Headers.Add("x-amzn-RequestId", "fake-request");
        }

        return response;
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    private sealed class Factory(HttpMessageHandler handler) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(handler, disposeHandler: false);

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;
    }
}
