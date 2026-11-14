using System.Net;
using System.Text;

namespace ConfigFerret.KubernetesCluster.Tests;

/// <summary>
/// Stands in for the Kubernetes API server: it sits in the client's handler chain and answers every request from canned
/// responses keyed by path, without calling the real handler. Unknown paths get a 404 Status, like the API server.
/// </summary>
internal sealed class FakeApiServer : DelegatingHandler
{
    private const string NotFound = """{"kind":"Status","apiVersion":"v1","status":"Failure","reason":"NotFound","code":404}""";

    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = new(StringComparer.Ordinal);

    public List<string> Paths { get; } = [];

    public List<string?> Authorizations { get; } = [];

    public List<string> Hosts { get; } = [];

    public FakeApiServer Respond(string path, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses[path] = (status, body);
        return this;
    }

    public FakeApiServer Fail(string path, HttpStatusCode status) =>
        Respond(path, $$"""{"kind":"Status","apiVersion":"v1","status":"Failure","code":{{(int)status}}}""", status);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Paths.Add(path);
        Hosts.Add(request.RequestUri.Host);
        Authorizations.Add(request.Headers.Authorization?.ToString());
        var (status, body) = _responses.TryGetValue(path, out var found) ? found : (HttpStatusCode.NotFound, NotFound);
        return Task.FromResult(new HttpResponseMessage(status)
        {
            RequestMessage = request,
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}
