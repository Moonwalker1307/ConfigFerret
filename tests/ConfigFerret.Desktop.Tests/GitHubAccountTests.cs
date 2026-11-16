using System.IO.Compression;
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

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"full_name":"no-slash"}""")]
    [InlineData("""{"full_name":"contoso/same"}""")]
    public async Task Stops_at_a_full_page_that_adds_no_repository(string item)
    {
        var page = "[" + string.Join(",", Enumerable.Repeat(item, 100)) + "]";
        var handler = EndlessPages(_ => page);

        using var http = new HttpClient(handler);

        var repositories = await new GitHubAccount(http, GitHubAccount.DefaultApiBase, Token).ListRepositoriesAsync(TestContext.Current.CancellationToken);

        Assert.True(repositories.Count <= 1);
        Assert.True(handler.Requests.Count <= 2, $"{handler.Requests.Count} requests");
    }

    [Fact]
    public async Task Asks_for_no_more_pages_than_the_list_can_hold()
    {
        // Every page is full but holds one usable repository.
        var handler = EndlessPages(page => "[" + string.Join(",", Enumerable.Repeat("{}", 99)) + $$""",{"full_name":"contoso/repo{{page}}"}]""");

        using var http = new HttpClient(handler);

        var repositories = await new GitHubAccount(http, GitHubAccount.DefaultApiBase, Token).ListRepositoriesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(20, handler.Requests.Count);
        Assert.Equal(20, repositories.Count);
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

    [Fact]
    public async Task Follows_a_tarball_redirect_to_another_host_without_the_token()
    {
        var tarball = FakeGitHub.Tarball(OrdersRepo.Root);
        var handler = new RedirectingHandler(request => request.RequestUri!.Host switch
        {
            "api.github.com" => Redirect("https://codeload.github.com/contoso/orders/legacy.tar.gz/refs/heads/main?token=short-lived"),
            "codeload.github.com" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tarball) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var http = new HttpClient(handler);
        using var parent = new TempFolder();

        var folder = await new GitHubAccount(http, GitHubAccount.DefaultApiBase, Token).DownloadAsync("contoso/orders", "main", parent.Path, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Join(folder, "src", "Orders.Api", "appsettings.json")));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(Token, handler.Requests[0].Authorization);
        Assert.Equal("codeload.github.com", handler.Requests[1].Uri.Host);
        Assert.Null(handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task Keeps_the_token_on_a_redirect_within_the_api_host()
    {
        var handler = new RedirectingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/user" => Redirect("/users/ferret-fan"),
            "/users/ferret-fan" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"login":"ferret-fan"}""") },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var http = new HttpClient(handler);

        Assert.Equal("ferret-fan", await new GitHubAccount(http, GitHubAccount.DefaultApiBase, Token).GetLoginAsync(TestContext.Current.CancellationToken));
        Assert.All(handler.Requests, r => Assert.Equal(Token, r.Authorization));
    }

    [Theory]
    [InlineData("http://codeload.example.test/x.tar.gz")]
    [InlineData("https://api.github.com:8443/user")]
    [InlineData("http://api.github.com/user")]
    public async Task Never_sends_the_token_to_another_origin_or_over_http(string location)
    {
        var handler = new RedirectingHandler(request => request.RequestUri!.Host == "api.github.com" && request.RequestUri.Port == 443
            ? Redirect(location)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"login":"ferret-fan"}""") });
        using var http = new HttpClient(handler);

        try
        {
            await new GitHubAccount(http, GitHubAccount.DefaultApiBase, Token).GetLoginAsync(TestContext.Current.CancellationToken);
        }
        catch (HttpRequestException ex)
        {
            Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        }

        Assert.All(handler.Requests.Skip(1), r => Assert.Null(r.Authorization));
        Assert.DoesNotContain(handler.Requests, r => r.Uri.Scheme == "http");
    }

    [Fact]
    public async Task Gives_up_after_too_many_redirects()
    {
        var handler = new RedirectingHandler(_ => Redirect("/user"));
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => new GitHubAccount(http, GitHubAccount.DefaultApiBase, Token).GetLoginAsync(TestContext.Current.CancellationToken));
        Assert.Equal(6, handler.Requests.Count);
    }

    [Theory]
    [InlineData("contoso/..")]
    [InlineData("../user")]
    [InlineData("contoso/orders/extra")]
    [InlineData("contoso")]
    [InlineData("contoso/or ders")]
    [InlineData("contoso/..\\..\\evil")]
    [InlineData("contoso/%2e%2e")]
    public async Task Refuses_repository_names_that_could_change_the_path(string fullName)
    {
        var handler = new FakeHttpHandler();
        using var parent = new TempFolder();

        await Assert.ThrowsAsync<ArgumentException>(() => Account(handler).DownloadAsync(fullName, "main", parent.Path, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => Account(handler).ListEnvironmentsAsync(fullName, TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(parent.Path));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task Refuses_a_ref_that_is_a_dot_segment(string gitRef)
    {
        var handler = new FakeHttpHandler();
        using var parent = new TempFolder();

        await Assert.ThrowsAsync<ArgumentException>(() => Account(handler).DownloadAsync("contoso/orders", gitRef, parent.Path, TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Makes_the_download_folder_readable_by_the_user_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var http = new HttpClient(new TarballHandler(FakeGitHub.Tarball(OrdersRepo.Root)));
        using var parent = new TempFolder();

        var folder = await new GitHubAccount(http, GitHubAccount.DefaultApiBase, Token).DownloadAsync("contoso/orders", "main", parent.Path, TestContext.Current.CancellationToken);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
    }

    [Fact]
    public async Task Removes_a_download_that_ends_early_and_keeps_the_other_folders()
    {
        var tarball = CutInsideABlock(FakeGitHub.Tarball(OrdersRepo.Root));
        using var http = new HttpClient(new TarballHandler(tarball));
        using var parent = new TempFolder();
        parent.Write(Path.Join("contoso_orders-earlier", "appsettings.json"), "{}");

        await Assert.ThrowsAnyAsync<IOException>(() => new GitHubAccount(http, GitHubAccount.DefaultApiBase, Token).DownloadAsync("contoso/orders", "main", parent.Path, TestContext.Current.CancellationToken));

        Assert.Equal(["contoso_orders-earlier"], Directory.EnumerateFileSystemEntries(parent.Path).Select(Path.GetFileName));
        Assert.True(File.Exists(parent.Full(Path.Join("contoso_orders-earlier", "appsettings.json"))));
    }

    [Fact]
    public async Task Removes_a_cancelled_download()
    {
        var tarball = FakeGitHub.Tarball(OrdersRepo.Root);
        using var parent = new TempFolder();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var http = new HttpClient(new StreamHandler(() => new InterruptedStream(tarball, tarball.Length / 2, () =>
        {
            Assert.NotEmpty(Directory.EnumerateFiles(Assert.Single(Directory.GetDirectories(parent.Path)), "*", SearchOption.AllDirectories));
            cancel.Cancel();
        })));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitHubAccount(http, GitHubAccount.DefaultApiBase, Token).DownloadAsync("contoso/orders", "main", parent.Path, cancel.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(parent.Path));
    }

    [Fact]
    public async Task Removes_a_failed_download_without_following_links_and_keeps_the_error()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tarball = FakeGitHub.Tarball(OrdersRepo.Root);
        using var outside = new TempFolder();
        outside.Write(Path.Join("deep", "keep.json"), "{}");
        using var parent = new TempFolder();
        var error = new IOException("The connection was reset.");
        using var http = new HttpClient(new StreamHandler(() => new InterruptedStream(tarball, tarball.Length / 2, () =>
        {
            var target = Assert.Single(Directory.GetDirectories(parent.Path));
            Directory.CreateSymbolicLink(Path.Join(target, "linked-folder"), outside.Path);
            File.CreateSymbolicLink(Path.Join(target, "linked.json"), outside.Full(Path.Join("deep", "keep.json")));
            throw error;
        })));

        var thrown = await Assert.ThrowsAsync<IOException>(() => new GitHubAccount(http, GitHubAccount.DefaultApiBase, Token).DownloadAsync("contoso/orders", "main", parent.Path, TestContext.Current.CancellationToken));

        Assert.Same(error, thrown);
        Assert.Empty(Directory.EnumerateFileSystemEntries(parent.Path));
        Assert.True(File.Exists(outside.Full(Path.Join("deep", "keep.json"))));
    }

    // The tarball with its tar cut two thirds in, inside a 512-byte block so it can't look like a clean end, and gzipped
    // again whole.
    private static byte[] CutInsideABlock(byte[] tarball)
    {
        using var tar = new MemoryStream();
        using (var unzip = new GZipStream(new MemoryStream(tarball), CompressionMode.Decompress))
        {
            unzip.CopyTo(tar);
        }

        using var cut = new MemoryStream();
        using (var gzip = new GZipStream(cut, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(tar.GetBuffer(), 0, (int)(tar.Length * 2 / 3 / 512 * 512) + 100);
        }

        return cut.ToArray();
    }

    private static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

    [Fact]
    public void Needs_a_client_and_an_api_address()
    {
        Assert.Throws<ArgumentNullException>(() => new GitHubAccount(null!, GitHubAccount.DefaultApiBase, Token));
        Assert.Throws<ArgumentNullException>(() => new GitHubAccount(new HttpClient(), null!, Token));
    }

    [Fact]
    public void Explains_why_an_api_address_without_https_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => new GitHubAccount(new HttpClient(), new Uri("http://github.example.test/"), Token));

        Assert.Contains("https", ex.Message, StringComparison.Ordinal);
        Assert.Equal("apiBase", ex.ParamName);
    }

    [Fact]
    public async Task Identifies_itself_and_asks_for_the_json_api()
    {
        var handler = new FakeHttpHandler().Respond("/user", """{"login":"ferret-fan"}""");

        await Account(handler).GetLoginAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString(), StringComparison.Ordinal);
        Assert.Contains("ConfigFerret-Desktop", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        Assert.Equal("2022-11-28", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
    }

    [Theory]
    [InlineData("""{"login":5}""")]
    [InlineData("""{"login":null}""")]
    [InlineData("[]")]
    [InlineData("""[{"login":"ferret-fan"}]""")]
    [InlineData("\"ferret-fan\"")]
    [InlineData("5")]
    public async Task Says_so_when_the_login_is_not_text(string body)
    {
        var handler = new FakeHttpHandler().Respond("/user", body);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Account(handler).GetLoginAsync(TestContext.Current.CancellationToken));

        Assert.Contains("did not say who", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/user", """{"login":"\ud800"}""")]
    [InlineData("/user", """{"\ud800":1,"login":"ferret-fan"}""")]
    [InlineData("/user/repos?per_page=100&page=1&sort=pushed&affiliation=owner,collaborator,organization_member", """[{"full_name":"contoso/orders","description":"\ud800"}]""")]
    public async Task Refuses_an_answer_whose_text_is_not_valid(string path, string body)
    {
        var handler = new FakeHttpHandler().Respond("/user", """{"login":"ferret-fan"}""").Respond(path, body);
        var account = Account(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await account.GetLoginAsync(TestContext.Current.CancellationToken);
            await account.ListRepositoriesAsync(TestContext.Current.CancellationToken);
        });

        Assert.Contains("not valid UTF-16", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("utf-16")]
    public async Task Reads_an_answer_in_the_charset_it_names_or_else_as_utf8(string charset)
    {
        using var http = new HttpClient(new RedirectingHandler(_ =>
        {
            var content = new StringContent("""{"login":"ferret-fan"}""", charset == "utf-16" ? Encoding.Unicode : Encoding.UTF8, "application/json");
            content.Headers.ContentType!.CharSet = charset;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));

        Assert.Equal("ferret-fan", await new GitHubAccount(http, GitHubAccount.DefaultApiBase, Token).GetLoginAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Says_so_when_the_answer_is_not_json()
    {
        var handler = new FakeHttpHandler().Respond("/user", $"<html>{Token}</html>");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Account(handler).GetLoginAsync(TestContext.Current.CancellationToken));

        Assert.Contains("GitHub returned a response that is not JSON for /user", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"environments":{"name":"staging"}}""")]
    [InlineData("""{"environments":"staging"}""")]
    [InlineData("""{"environments":[]}""")]
    [InlineData("[]")]
    [InlineData("""[{"name":"staging"}]""")]
    [InlineData("\"staging\"")]
    public async Task Lists_no_environments_when_the_answer_has_no_list(string body)
    {
        var handler = new FakeHttpHandler().Respond("/repos/contoso/orders/environments?per_page=100", body);

        Assert.Empty(await Account(handler).ListEnvironmentsAsync("contoso/orders", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Skips_environments_without_a_name()
    {
        var handler = new FakeHttpHandler().Respond("/repos/contoso/orders/environments?per_page=100", """{"environments":[{"name":"staging"},{"id":3},{"name":7}]}""");

        Assert.Equal(["staging"], await Account(handler).ListEnvironmentsAsync("contoso/orders", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Other_errors_listing_environments_are_not_hidden()
    {
        var handler = new FakeHttpHandler().Respond("/repos/contoso/orders/environments?per_page=100", "{}", HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<HttpRequestException>(() => Account(handler).ListEnvironmentsAsync("contoso/orders", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Keeps_the_slash_between_owner_and_name()
    {
        var handler = new FakeHttpHandler();

        await Account(handler).ListEnvironmentsAsync("con-toso/or.ders_1", TestContext.Current.CancellationToken);

        Assert.Equal("/repos/con-toso/or.ders_1/environments?per_page=100", Assert.Single(handler.Requests).RequestUri!.PathAndQuery);
    }

    [Theory]
    [InlineData("", "main")]
    [InlineData("contoso/orders", " ")]
    public async Task A_download_needs_a_repository_and_a_ref(string fullName, string gitRef)
    {
        var handler = new FakeHttpHandler();
        using var parent = new TempFolder();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Account(handler).DownloadAsync(fullName, gitRef, parent.Path, TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Downloads_the_ref_that_was_asked_for_with_the_token()
    {
        var handler = new FakeHttpHandler();
        using var parent = new TempFolder();

        await Assert.ThrowsAsync<HttpRequestException>(() => Account(handler).DownloadAsync("contoso/orders", "feature/x y", parent.Path, TestContext.Current.CancellationToken));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/repos/contoso/orders/tarball/feature%2Fx%20y", request.RequestUri!.PathAndQuery);
        Assert.Equal(Token, request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task A_download_goes_into_a_folder_named_after_the_repository_and_never_reuses_one()
    {
        var tarball = FakeGitHub.Tarball(OrdersRepo.Root);
        using var http = new HttpClient(new TarballHandler(tarball));
        var account = new GitHubAccount(http, GitHubAccount.DefaultApiBase, Token);
        using var parent = new TempFolder();

        var first = await account.DownloadAsync("contoso/orders", "main", parent.Path, TestContext.Current.CancellationToken);
        var second = await account.DownloadAsync("contoso/orders", "main", parent.Path, TestContext.Current.CancellationToken);

        Assert.NotEqual(first, second);
        Assert.Equal(parent.Path, Path.GetDirectoryName(first));
        Assert.StartsWith("contoso_orders-", Path.GetFileName(first), StringComparison.Ordinal);
    }

    private static string ReposPath(int page) =>
        $"/user/repos?per_page=100&page={page}&sort=pushed&affiliation=owner,collaborator,organization_member";

    private static GitHubAccount Account(FakeHttpHandler handler) => new(handler.Client(), GitHubAccount.DefaultApiBase, Token);

    // Answers each page of the repository list with page(number), and gives up with an error after 50 requests so a
    // list that never ends fails the test instead of hanging it.
    private static RedirectingHandler EndlessPages(Func<int, string> page)
    {
        var requests = 0;
        return new RedirectingHandler(request =>
        {
            if (++requests > 50)
            {
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            }

            var number = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"]!, System.Globalization.CultureInfo.InvariantCulture);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page(number), Encoding.UTF8, "application/json") };
        });
    }

    private static string Repositories(int count, int start)
    {
        var builder = new StringBuilder("[");
        for (var i = 0; i < count; i++)
        {
            builder.Append(i == 0 ? "" : ",").Append(System.Globalization.CultureInfo.InvariantCulture, $$"""{"full_name":"contoso/repo{{start + i}}","default_branch":"main"}""");
        }

        return builder.Append(']').ToString();
    }

    /// <summary>Answers with <paramref name="respond"/> and records each request's address and token.</summary>
    private sealed class RedirectingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.Authorization?.Parameter));
            return Task.FromResult(respond(request));
        }
    }

    private sealed class TarballHandler(byte[] tarball) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tarball) });
    }

    /// <summary>Answers every request with the body <paramref name="body"/> makes.</summary>
    private sealed class StreamHandler(Func<Stream> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body()) });
    }

    /// <summary>Serves <paramref name="data"/> and calls <paramref name="interrupt"/> once, when <paramref name="after"/> bytes were read.</summary>
    private sealed class InterruptedStream(byte[] data, int after, Action interrupt) : Stream
    {
        private int _position;
        private bool _interrupted;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position == after && !_interrupted)
            {
                _interrupted = true;
                interrupt();
            }

            var count = Math.Min(buffer.Length, (_position < after ? after : data.Length) - _position);
            data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
