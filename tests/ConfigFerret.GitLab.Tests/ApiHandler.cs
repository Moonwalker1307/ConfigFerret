using System.Net;
using System.Text;

namespace ConfigFerret.GitLab.Tests;

/// <summary>
/// Answers requests from canned responses keyed by path and query, with optional response headers, records every
/// request, and disposes every response it sent.
/// </summary>
internal sealed class ApiHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Body, (string Name, string Value)[] Headers)> _responses = new(StringComparer.Ordinal);
    private readonly List<HttpResponseMessage> _sent = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> Paths { get; } = [];

    public ApiHandler Respond(string pathAndQuery, string body, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers)
    {
        _responses[pathAndQuery] = (status, body, headers);
        return this;
    }

    public HttpClient Client() => new(this, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var key = request.RequestUri!.PathAndQuery;
        Paths.Add(key);
        var (status, body, headers) = _responses.TryGetValue(key, out var found) ? found : (HttpStatusCode.NotFound, """{"message":"404 Not Found"}""", []);
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        _sent.Add(response);
        return Task.FromResult(response);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var response in _sent)
            {
                response.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
