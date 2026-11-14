using System.Net;
using System.Text;
using ConfigFerret.Desktop.Services;
using ConfigFerret.Tests.Shared;

namespace ConfigFerret.Desktop.Tests;

public class GitHubAccountTests
{
    private const string Token = "ghp" + "_secretsecretsecretsecretsecret000000";

    [Fact]
    public async Task Reads_the_login()
    {
        var handler = new FakeHttpHandler().Respond("/user", """{"login":"ferret-fan"}""");

        Assert.Equal("ferret-fan", await Account(handler).GetLoginAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sends_the_token_in_the_authorization_header_only()
    {
        var handler = new FakeHttpHandler().Respond("/user", """{"login":"ferret-fan"}""");

        await Account(handler).GetLoginAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal(Token, request.Headers.Authorization?.Parameter);
        Assert.DoesNotContain(Token, request.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asks_for_the_next_page_when_a_page_is_full()
    {
        var handler = new FakeHttpHandler()
            .Respond(ReposPath(1), Repositories(100, 0))
            .Respond(ReposPath(2), Repositories(1, 100));

        var repositories = await Account(handler).ListRepositoriesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(101, repositories.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("contoso/repo100", repositories[^1].FullName);
    }

    [Fact]
    public async Task Stops_after_a_page_that_is_not_full()
    {
        var handler = new FakeHttpHandler().Respond(ReposPath(1), Repositories(99, 0));

        var repositories = await Account(handler).ListRepositoriesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(99, repositories.Count);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Reads_the_repository_details_and_fills_in_missing_ones()
    {
        var handler = new FakeHttpHandler().Respond(
            ReposPath(1),
            """
            [
              {"full_name":"contoso/orders","description":"Orders API","default_branch":"develop","private":true,"language":"C#","pushed_at":"2026-11-10T10:00:00Z"},
              {"full_name":"ferret-fan/dotfiles","description":null,"private":false,"language":null,"pushed_at":null},
              {"full_name":"no-slash"},
              {"description":"no name"}
            ]
            """);

        var repositories = await Account(handler).ListRepositoriesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, repositories.Count);
        var orders = repositories[0];
        Assert.Equal("contoso", orders.Owner);
        Assert.Equal("orders", orders.Name);
        Assert.Equal("develop", orders.DefaultBranch);
        Assert.True(orders.IsPrivate);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero), orders.PushedAt);
        var dotfiles = repositories[1];
        Assert.Equal("main", dotfiles.DefaultBranch);
        Assert.Equal(string.Empty, dotfiles.Description);
        Assert.Equal(string.Empty, dotfiles.Language);
        Assert.Null(dotfiles.PushedAt);
    }

    [Theory]
    [InlineData("http://github.example.test/api/v3/")]
    [InlineData("ftp://github.example.test/")]
    public void Refuses_an_api_address_that_is_not_https(string address)
    {
        var ex = Assert.Throws<ArgumentException>(() => new GitHubAccount(new HttpClient(), new Uri(address), Token));

        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost:8080/api/v3/")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("https://github.example.test/api/v3")]
    public void Accepts_https_and_loopback_addresses(string address)
    {
        _ = new GitHubAccount(new HttpClient(), new Uri(address), Token);
    }

    [Fact]
    public async Task Keeps_the_api_path_of_an_enterprise_server_address()
    {
        var handler = new FakeHttpHandler().Respond("/api/v3/user", """{"login":"x"}""");
        var account = new GitHubAccount(handler.Client(), new Uri("https://github.example.test/api/v3"), Token);

        Assert.Equal("x", await account.GetLoginAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Needs_a_token()
    {
        Assert.Throws<ArgumentException>(() => new GitHubAccount(new HttpClient(), GitHubAccount.DefaultApiBase, " "));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "refused")]
    [InlineData(HttpStatusCode.Forbidden, "not allowed")]
    [InlineData(HttpStatusCode.NotFound, "not found")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task Error_messages_never_contain_the_token(HttpStatusCode status, string expected)
    {
        // The server even echoes the token back in its body, which must not reach the message.
        var handler = new FakeHttpHandler().Respond("/user", $"{{\"message\":\"bad {Token}\"}}", status);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Account(handler).GetLoginAsync(TestContext.Current.CancellationToken));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        Assert.Equal(status, ex.StatusCode);
    }

    [Fact]
    public async Task Says_so_when_the_login_is_missing()
    {
        var handler = new FakeHttpHandler().Respond("/user", "{}");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Account(handler).GetLoginAsync(TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lists_environments_and_treats_a_missing_or_forbidden_list_as_empty()
    {
        var handler = new FakeHttpHandler()
            .Respond("/repos/contoso/orders/environments?per_page=100", """{"environments":[{"name":"staging"},{"name":"production"}]}""")
            .Respond("/repos/contoso/private/environments?per_page=100", "{}", HttpStatusCode.Forbidden);
        var account = Account(handler);

        Assert.Equal(["staging", "production"], await account.ListEnvironmentsAsync("contoso/orders", TestContext.Current.CancellationToken));
        Assert.Empty(await account.ListEnvironmentsAsync("contoso/private", TestContext.Current.CancellationToken));
        Assert.Empty(await account.ListEnvironmentsAsync("contoso/missing", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Fails_a_download_with_a_message_that_has_no_token_and_leaves_no_folder_behind_content()
    {
        var handler = new FakeHttpHandler().Respond("/repos/contoso/orders/tarball/main", "{}", HttpStatusCode.NotFound);
        using var parent = new TempFolder();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Account(handler).DownloadAsync("contoso/orders", "main", parent.Path, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(parent.Path));
    }

    [Fact]
    public async Task Downloads_a_repository_into_a_new_folder()
    {
        var tarball = FakeGitHub.Tarball(OrdersRepo.Root);
        using var http = new HttpClient(new TarballHandler(tarball));
        var account = new GitHubAccount(http, GitHubAccount.DefaultApiBase, Token);
        using var parent = new TempFolder();

        var folder = await account.DownloadAsync("contoso/orders", "main", parent.Path, TestContext.Current.CancellationToken);

        Assert.StartsWith(parent.Path, folder, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(folder, "src", "Orders.Api", "appsettings.json")));
        Assert.True(File.Exists(Path.Join(folder, ".github", "workflows", "deploy.yml")));
    }

    private static string ReposPath(int page) =>
        $"/user/repos?per_page=100&page={page}&sort=pushed&affiliation=owner,collaborator,organization_member";

    private static GitHubAccount Account(FakeHttpHandler handler) => new(handler.Client(), GitHubAccount.DefaultApiBase, Token);

    private static string Repositories(int count, int start)
    {
        var builder = new StringBuilder("[");
        for (var i = 0; i < count; i++)
        {
            builder.Append(i == 0 ? "" : ",").Append(System.Globalization.CultureInfo.InvariantCulture, $$"""{"full_name":"contoso/repo{{start + i}}","default_branch":"main"}""");
        }

        return builder.Append(']').ToString();
    }

    private sealed class TarballHandler(byte[] tarball) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tarball) });
    }
}
