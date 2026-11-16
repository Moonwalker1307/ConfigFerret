using System.Net;
using ConfigFerret.Desktop.Services;
using ConfigFerret.Tests.Shared;

namespace ConfigFerret.Desktop.Tests;

public class OctopusAccountTests
{
    private const string ApiKey = "API-ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Spaces = """
        [
          {"Id":"Spaces-1","Name":"Default","IsDefault":true},
          {"Id":"Spaces-2","Name":"Platform","IsDefault":false}
        ]
        """;

    [Fact]
    public async Task Lists_the_projects_of_the_default_space_sorted_by_name()
    {
        var handler = Handler()
            .Respond("/api/Spaces-1/projects/all", """
                [
                  {"Name":"payments","Slug":"payments"},
                  {"Name":"Orders","Slug":"orders-api"},
                  {"Name":"Billing","Slug":"billing"},
                  {"Slug":"nameless"}
                ]
                """);

        var projects = await Account(handler).ListProjectsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Billing", "Orders", "payments"], projects.Select(p => p.Name));
        Assert.Equal("orders-api", projects[1].Slug);
        Assert.Equal("Orders", projects[1].ToString());
    }

    [Theory]
    [InlineData("Platform")]
    [InlineData("platform")]
    [InlineData("Spaces-2")]
    [InlineData("  Platform  ")]
    public async Task Picks_a_named_space_by_name_or_id(string space)
    {
        var handler = Handler().Respond("/api/Spaces-2/projects/all", """[{"Name":"Edge","Slug":"edge"}]""");

        var projects = await Account(handler, space).ListProjectsAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Edge", Assert.Single(projects).Name);
    }

    [Fact]
    public async Task Fails_when_the_named_space_does_not_exist()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Account(Handler(), "Nowhere").ListProjectsAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Nowhere", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fails_when_there_is_no_default_space()
    {
        var handler = new FakeHttpHandler().Respond("/api/spaces/all", """[{"Id":"Spaces-2","Name":"Platform","IsDefault":false}]""");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Account(handler).ListProjectsAsync(TestContext.Current.CancellationToken));

        Assert.Contains("default space", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sends_the_api_key_in_the_header_and_never_in_the_address()
    {
        var handler = Handler().Respond("/api/Spaces-1/projects/all", "[]");

        await Account(handler).ListProjectsAsync(TestContext.Current.CancellationToken);

        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(ApiKey, Assert.Single(r.Headers.GetValues("X-Octopus-ApiKey")));
            Assert.DoesNotContain(ApiKey, r.RequestUri!.ToString(), StringComparison.Ordinal);
            Assert.Null(r.Headers.Authorization);
        });
    }

    [Fact]
    public async Task Looks_the_space_up_once()
    {
        var handler = Handler()
            .Respond("/api/Spaces-1/projects/all", "[]")
            .Respond("/api/Spaces-1/environments/all", """[{"Name":"Development"},{"Name":"Production"}]""");
        var account = Account(handler);

        await account.ListProjectsAsync(TestContext.Current.CancellationToken);
        var environments = await account.ListEnvironmentsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Development", "Production"], environments);
        Assert.Single(handler.Requests, r => r.RequestUri!.AbsolutePath == "/api/spaces/all");
    }

    [Theory]
    [InlineData("http://octopus.example.test/")]
    [InlineData("ftp://octopus.example.test/")]
    public void Refuses_a_server_address_that_is_not_https(string address)
    {
        var ex = Assert.Throws<ArgumentException>(() => new OctopusAccount(new HttpClient(), new Uri(address), ApiKey, null));

        Assert.DoesNotContain(ApiKey, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost:8065")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("https://octopus.example.test")]
    public void Accepts_https_and_loopback_addresses(string address)
    {
        _ = new OctopusAccount(new HttpClient(), new Uri(address), ApiKey, null);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "refused")]
    [InlineData(HttpStatusCode.Forbidden, "refused")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task Error_messages_never_contain_the_api_key(HttpStatusCode status, string expected)
    {
        var handler = new FakeHttpHandler().Respond("/api/spaces/all", $"{{\"ErrorMessage\":\"bad {ApiKey}\"}}", status);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Account(handler).ListProjectsAsync(TestContext.Current.CancellationToken));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_apps_http_client_does_not_carry_the_api_key_through_a_redirect()
    {
        // Two loopback servers: the "Octopus" one redirects to the other, which must never see the request.
        using var octopus = new Loopback(_ => (HttpStatusCode.Redirect, null));
        using var elsewhere = new Loopback(_ => (HttpStatusCode.OK, Spaces));
        octopus.RedirectTo = new Uri(elsewhere.Address, "api/spaces/all");
        using var http = new HttpClient(ConfigFerret.Desktop.ViewModels.MainWindowViewModel.CreateHttpHandler());
        var account = new OctopusAccount(http, octopus.Address, ApiKey, null);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => account.ListProjectsAsync(TestContext.Current.CancellationToken));

        Assert.DoesNotContain(ApiKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, octopus.Requests);
        Assert.Equal(0, elsewhere.Requests);
    }

    private sealed class Loopback : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Func<string, (HttpStatusCode Status, string? Body)> _respond;
        private readonly Task _loop;

        public Loopback(Func<string, (HttpStatusCode Status, string? Body)> respond)
        {
            _respond = respond;
            _listener.Start();
            Address = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _loop = Task.Run(ServeAsync);
        }

        public Uri Address { get; }

        public Uri? RedirectTo { get; set; }

        public int Requests { get; private set; }

        public void Dispose()
        {
            _listener.Stop();
            _ = _loop.Exception;
        }

        private async Task ServeAsync()
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                var line = await reader.ReadLineAsync().ConfigureAwait(false) ?? string.Empty;
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync().ConfigureAwait(false)))
                {
                }

                Requests++;
                var (status, body) = _respond(line);
                var location = status == HttpStatusCode.Redirect ? $"Location: {RedirectTo}\r\n" : string.Empty;
                var bytes = System.Text.Encoding.UTF8.GetBytes(body ?? string.Empty);
                var head = $"HTTP/1.1 {(int)status} X\r\n{location}Content-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
                await stream.WriteAsync(bytes).ConfigureAwait(false);
            }
        }
    }

    [Fact]
    public void Needs_a_client_a_server_and_an_api_key()
    {
        var server = new Uri("https://octopus.example.test/");

        Assert.Throws<ArgumentNullException>(() => new OctopusAccount(null!, server, ApiKey, null));
        Assert.Throws<ArgumentNullException>(() => new OctopusAccount(new HttpClient(), null!, ApiKey, null));
        Assert.Throws<ArgumentException>(() => new OctopusAccount(new HttpClient(), server, " ", null));
    }

    [Fact]
    public void Explains_why_a_server_without_https_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => new OctopusAccount(new HttpClient(), new Uri("http://octopus.example.test/"), ApiKey, null));

        Assert.Contains("https", ex.Message, StringComparison.Ordinal);
        Assert.Equal("server", ex.ParamName);
    }

    [Fact]
    public async Task Keeps_the_path_of_a_server_address_without_a_trailing_slash()
    {
        var handler = new FakeHttpHandler()
            .Respond("/octopus/api/spaces/all", Spaces)
            .Respond("/octopus/api/Spaces-1/environments/all", """[{"Name":"Dev"}]""");
        var account = new OctopusAccount(handler.Client(), new Uri("https://octopus.example.test/octopus"), ApiKey, null);

        Assert.Equal(["Dev"], await account.ListEnvironmentsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Says_so_when_there_is_no_default_space()
    {
        var handler = new FakeHttpHandler().Respond("/api/spaces/all", """[{"Id":"Spaces-2","Name":"Platform","IsDefault":false},{"Id":"Spaces-3","Name":"Other"}]""");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Account(handler).ListProjectsAsync(TestContext.Current.CancellationToken));

        Assert.Contains("no default space", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("""["Default"]""")]
    [InlineData("""[null,[{"Id":"Spaces-1","IsDefault":true}]]""")]
    public async Task Says_so_when_the_spaces_are_not_objects(string body)
    {
        var handler = new FakeHttpHandler().Respond("/api/spaces/all", body);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Account(handler).ListProjectsAsync(TestContext.Current.CancellationToken));

        Assert.Contains("no default space", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_space_without_an_id_is_not_a_match()
    {
        var handler = new FakeHttpHandler().Respond("/api/spaces/all", """[{"Name":"Default","IsDefault":true}]""");

        await Assert.ThrowsAsync<HttpRequestException>(() => Account(handler).ListProjectsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Names_the_space_that_was_not_found_even_when_a_default_exists()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Account(Handler(), "Nowhere").ListEnvironmentsAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Nowhere", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lists_environments_in_the_order_octopus_gives_and_skips_unnamed_ones()
    {
        var handler = Handler().Respond("/api/Spaces-1/environments/all", """[{"Name":"Test"},{"Id":"x"},{"Name":"Dev"},{"Name":5}]""");

        Assert.Equal(["Test", "Dev"], await Account(handler).ListEnvironmentsAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("""{"Items":[{"Name":"Orders","Slug":"orders"}]}""")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    public async Task An_answer_that_is_not_a_list_has_no_projects(string body)
    {
        var handler = Handler().Respond("/api/Spaces-1/projects/all", body);

        Assert.Empty(await Account(handler).ListProjectsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_project_without_a_slug_gets_an_empty_one()
    {
        var handler = Handler().Respond("/api/Spaces-1/projects/all", """[{"Name":"Orders"}]""");

        var project = Assert.Single(await Account(handler).ListProjectsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(new OctopusProjectInfo("Orders", string.Empty), project);
        Assert.Equal("Orders", project.ToString());
    }

    [Fact]
    public async Task Error_messages_name_what_could_not_be_read()
    {
        var handler = new FakeHttpHandler().Respond("/api/spaces/all", "{}", HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Account(handler).ListProjectsAsync(TestContext.Current.CancellationToken));

        Assert.Contains("api/spaces/all", ex.Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    private static FakeHttpHandler Handler() => new FakeHttpHandler().Respond("/api/spaces/all", Spaces);

    private static OctopusAccount Account(FakeHttpHandler handler, string? space = null) =>
        new(handler.Client(), new Uri("https://octopus.example.test/"), ApiKey, space);
}
