using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;

namespace ConfigFerret.Desktop.Tests;

/// <summary>
/// A GitHub API that knows one user and serves the orders-repo test folder as the repository contoso/orders.
/// Records every request so tests can check the token is only sent where it should be.
/// </summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public const string Token = "ghp" + "_testtoken0000000000000000000000000";
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _json = new(StringComparer.Ordinal);

    public FakeGitHub()
    {
        Json("/user", """{"login":"ferret-fan"}""");
        Json("/user/repos?per_page=100&page=1&sort=pushed&affiliation=owner,collaborator,organization_member", """
            [
              {"full_name":"contoso/orders","description":"Orders API","default_branch":"main","private":true,"language":"C#","pushed_at":"2026-11-10T10:00:00Z"},
              {"full_name":"contoso/billing","description":"","default_branch":"main","private":false,"language":"Go","pushed_at":"2026-01-02T10:00:00Z"},
              {"full_name":"ferret-fan/dotfiles","description":"My dotfiles","default_branch":"trunk","private":false,"language":null,"pushed_at":null}
            ]
            """);
        Json("/repos/contoso/orders/environments?per_page=100", """{"total_count":2,"environments":[{"name":"staging"},{"name":"production"}]}""");
        const string repo = "/repos/contoso/orders";
        Json($"{repo}/actions/organization-variables?per_page=30&page=1", """{"total_count":0,"variables":[]}""");
        Json($"{repo}/actions/organization-secrets?per_page=100&page=1", """{"total_count":0,"secrets":[]}""");
        Json($"{repo}/actions/variables?per_page=30&page=1", """
            {"total_count":2,"variables":[
              {"name":"FEATURES_SEARCH","value":"true","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"},
              {"name":"LEGACY_ENDPOINT","value":"https://old.example.test","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}]}
            """);
        Json($"{repo}/actions/secrets?per_page=100&page=1", """{"total_count":1,"secrets":[{"name":"OLD_DEPLOY_KEY","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}]}""");
        Json($"{repo}/environments/production/variables?per_page=30&page=1", """{"total_count":0,"variables":[]}""");
        Json($"{repo}/environments/production/secrets?per_page=100&page=1", """{"total_count":0,"secrets":[]}""");
    }

    public List<HttpRequestMessage> Requests { get; } = [];

    public static string RepositoryFolder => Path.Join(AppContext.BaseDirectory, "TestData", "orders-repo");

    public FakeGitHub Json(string pathAndQuery, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _json[pathAndQuery] = (status, body);
        return this;
    }

    /// <summary>The orders-repo folder as a GitHub tarball: .tar.gz with everything under one top folder.</summary>
    public static byte[] Tarball(string folder, string topFolder = "contoso-orders-1a2b3c4/")
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, topFolder));
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
                tar.WriteEntry(file, topFolder + relative);
            }
        }

        return buffer.ToArray();
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        var key = request.RequestUri!.PathAndQuery;
        if (request.Headers.Authorization?.Parameter != Token)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"message":"Bad credentials"}""") });
        }

        if (key == "/repos/contoso/orders/tarball/main")
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Tarball(RepositoryFolder)) });
        }

        var (status, body) = _json.TryGetValue(key, out var found) ? found : (HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
