using System.Net;
using System.Text;
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
    private readonly List<HttpResponseMessage> _sent = [];

    public const string SecretKey = "wJalrXUtnFEMI/K7MDENG/never-printed";

    private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body)>> _responses = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Func<int, string>> _generators = new(StringComparer.Ordinal);

    public List<(string Operation, string Body, Uri Uri)> Requests { get; } = [];

    /// <summary>The Authorization and X-Amz-Security-Token headers of each request.</summary>
    public List<string> SigningHeaders { get; } = [];

    /// <summary>Answers every call of an operation with a body made from the call's number, counting from 0.</summary>
    public FakeAws OnEach(string operation, Func<int, string> body)
    {
        _generators[operation] = body;
        return this;
    }

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

    /// <summary>Sends a source's requests here, with no retries.</summary>
    public void Attach(AwsSecretsManagerSource source)
    {
        source.HttpHandler = this;
        source.ConfigureClient = NoRetries;
    }

    /// <summary>Sends a source's requests here, with no retries.</summary>
    public void Attach(AwsParameterStoreSource source)
    {
        source.HttpHandler = this;
        source.ConfigureClient = NoRetries;
    }

    // Built the way ConfigFerret builds its own clients, so responses go through its guard.
    public AmazonSecretsManagerClient SecretsManager(AwsConnectionOptions? options = null) => AwsClients.CreateSecretsManager(options ?? Options, this, NoRetries);

    public AmazonSimpleSystemsManagementClient ParameterStore() => AwsClients.CreateParameterStore(Options, this, NoRetries);

    private static AwsSecretsManagerOptions Options => new() { Region = "eu-west-1", AccessKeyId = "AKIDEXAMPLE", SecretAccessKey = SecretKey };

    private static void NoRetries(ClientConfig config) => config.MaxErrorRetry = 0;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var target = request.Headers.TryGetValues("X-Amz-Target", out var values) ? values.First() : string.Empty;
        var operation = target[(target.IndexOf('.', StringComparison.Ordinal) + 1)..];
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Requests.Add((operation, body, request.RequestUri!));
        SigningHeaders.Add(string.Join(" ", request.Headers.Where(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("X-Amz-Security-Token", StringComparison.OrdinalIgnoreCase)).SelectMany(h => h.Value)));
        if (_generators.TryGetValue(operation, out var generate))
        {
            var generated = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(generate(Operations(operation).Count() - 1), Encoding.UTF8, "application/x-amz-json-1.1") };
            _sent.Add(generated);
            return generated;
        }

        var (status, text) = _responses.TryGetValue(operation, out var queue) && queue.Count > 0
            ? (queue.Count > 1 ? queue.Dequeue() : queue.Peek())
            : (HttpStatusCode.BadRequest, $$"""{"__type":"UnknownOperationException","message":"no fake response for {{operation}}"}""");
        var response = new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/x-amz-json-1.1") };
        if (!IsSuccess(status) && text.Contains("__type", StringComparison.Ordinal))
        {
            response.Headers.Add("x-amzn-RequestId", "fake-request");
        }

        _sent.Add(response);
        return response;
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sent.ForEach(r => r.Dispose());
        }

        base.Dispose(disposing);
    }
}
