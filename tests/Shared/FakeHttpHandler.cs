using System.Net;
using System.Text;

namespace ConfigFerret.Tests.Shared;

/// <summary>Answers requests from canned responses keyed by path and query, and records every request.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = new(StringComparer.Ordinal);

    public List<HttpRequestMessage> Requests { get; } = [];

    public FakeHttpHandler Respond(string pathAndQuery, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses[pathAndQuery] = (status, body);
        return this;
    }

    public HttpClient Client() => new(this, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var key = request.RequestUri!.PathAndQuery;
        var (status, body) = _responses.TryGetValue(key, out var found) ? found : (HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
