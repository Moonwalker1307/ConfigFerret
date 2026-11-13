using System.Net;
using System.Text.Json;
using ConfigFerret.Tests.Shared;

namespace ConfigFerret.GitHub.Tests;

public class GitHubVariablesTests
{
    private const string Token = "ghp_test_token_never_printed";
    private const string Repo = "/repos/contoso/api";

    private static GitHubVariablesOptions Options(string? environment = "production") => new()
    {
        Owner = "contoso",
        Repository = "api",
        Environment = environment,
        Token = Token,
    };

    private static string Variables(params (string Name, string Value)[] items) => JsonSerializer.Serialize(new
    {
        total_count = items.Length,
        variables = items.Select(i => new { name = i.Name, value = i.Value, created_at = "2026-01-01T00:00:00Z", updated_at = "2026-02-03T04:05:06Z" }),
    });

    private static string Secrets(params string[] names) => JsonSerializer.Serialize(new
    {
        total_count = names.Length,
        secrets = names.Select(n => new { name = n, created_at = "2026-01-01T00:00:00Z", updated_at = "2026-01-01T00:00:00Z" }),
    });

    private static FakeHttpHandler FullRepository() => new FakeHttpHandler()
        .Respond($"{Repo}/actions/organization-variables?per_page=30&page=1", Variables(("DB_TIMEOUT", "10"), ("REGION", "eu")))
        .Respond($"{Repo}/actions/organization-secrets?per_page=100&page=1", Secrets("SHARED_KEY"))
        .Respond($"{Repo}/actions/variables?per_page=30&page=1", Variables(("DB_TIMEOUT", "20")))
        .Respond($"{Repo}/actions/secrets?per_page=100&page=1", Secrets("DB_PASSWORD"))
        .Respond($"{Repo}/environments/production/variables?per_page=30&page=1", Variables(("DB_TIMEOUT", "30")))
        .Respond($"{Repo}/environments/production/secrets?per_page=100&page=1", Secrets("DB_PASSWORD"));

    [Fact]
    public async Task Reads_organization_repository_and_environment_entries_lowest_priority_first()
    {
        var handler = FullRepository();
        using var client = handler.Client();

        var github = await GitHubVariables.LoadAsync(Options(), client, TestContext.Current.CancellationToken);

        var timeout = github.Find("db_timeout", GitHubVariableKind.Variable);
        Assert.Equal(["10", "20", "30"], timeout.Select(v => v.Value));
        Assert.Equal([GitHubVariableScope.Organization, GitHubVariableScope.Repository, GitHubVariableScope.Environment], timeout.Select(v => v.Scope));
        Assert.Equal(["contoso", "contoso/api", "production"], timeout.Select(v => v.Owner));
        Assert.Equal(new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero), timeout[0].UpdatedAt);

        var password = github.Find("DB_PASSWORD", GitHubVariableKind.Secret);
        Assert.Equal(2, password.Count);
        Assert.All(password, p => Assert.Null(p.Value));
        Assert.Empty(github.Warnings);
    }

    [Fact]
    public async Task Sends_the_token_only_as_a_bearer_header_with_the_api_version()
    {
        var handler = FullRepository();
        using var client = handler.Client();

        await GitHubVariables.LoadAsync(Options(), client, TestContext.Current.CancellationToken);

        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("Bearer", r.Headers.Authorization?.Scheme);
            Assert.Equal(Token, r.Headers.Authorization?.Parameter);
            Assert.Equal("2022-11-28", Assert.Single(r.Headers.GetValues("X-GitHub-Api-Version")));
            Assert.Contains(r.Headers.Accept, a => a.MediaType == "application/vnd.github+json");
            Assert.DoesNotContain(Token, r.RequestUri!.ToString(), StringComparison.Ordinal);
            Assert.Equal("api.github.com", r.RequestUri!.Host);
        });
    }

    [Fact]
    public async Task Follows_pages_until_the_total_is_read()
    {
        var first = Enumerable.Range(1, 30).Select(i => ($"V{i}", i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var page1 = JsonSerializer.Serialize(new { total_count = 31, variables = first.Select(v => new { name = v.Item1, value = v.Item2 }) });
        var page2 = JsonSerializer.Serialize(new { total_count = 31, variables = new[] { new { name = "V31", value = "31" } } });
        var handler = new FakeHttpHandler()
            .Respond($"{Repo}/actions/variables?per_page=30&page=1", page1)
            .Respond($"{Repo}/actions/variables?per_page=30&page=2", page2);
        using var client = handler.Client();

        var options = Options(environment: null);
        options.IncludeOrganization = false;
        options.IncludeSecrets = false;
        var github = await GitHubVariables.LoadAsync(options, client, TestContext.Current.CancellationToken);

        Assert.Equal(31, github.Entries.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Skips_organization_entries_a_user_owned_repository_does_not_have_with_a_warning()
    {
        var handler = new FakeHttpHandler()
            .Respond($"{Repo}/actions/variables?per_page=30&page=1", Variables(("A", "1")))
            .Respond($"{Repo}/actions/secrets?per_page=100&page=1", Secrets());
        using var client = handler.Client();

        var github = await GitHubVariables.LoadAsync(Options(environment: null), client, TestContext.Current.CancellationToken);

        Assert.Single(github.Entries);
        Assert.Equal(2, github.Warnings.Count);
        Assert.All(github.Warnings, w => Assert.Contains("organization", w, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fails_with_a_message_that_holds_the_status_and_path_but_not_the_token()
    {
        var handler = new FakeHttpHandler()
            .Respond($"{Repo}/actions/variables?per_page=30&page=1", """{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized);
        using var client = handler.Client();
        var options = Options(environment: null);
        options.IncludeOrganization = false;

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => GitHubVariables.LoadAsync(options, client, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
        Assert.Contains("/repos/contoso/api/actions/variables", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Bad credentials", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Does_not_follow_a_redirect_with_the_token()
    {
        var handler = new FakeHttpHandler()
            .Respond($"{Repo}/actions/variables?per_page=30&page=1", "", HttpStatusCode.Found);
        using var client = handler.Client();
        var options = Options(environment: null);
        options.IncludeOrganization = false;

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => GitHubVariables.LoadAsync(options, client, TestContext.Current.CancellationToken));

        Assert.Contains("Redirects are not followed", ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("http://github.example.com/api/v3/")]
    [InlineData("https://user:pass@github.example.com/api/v3/")]
    public async Task Refuses_an_address_that_would_leak_the_token(string address)
    {
        var options = Options();
        options.ApiBaseAddress = new Uri(address);
        using var client = new FakeHttpHandler().Client();

        await Assert.ThrowsAsync<ArgumentException>(() => GitHubVariables.LoadAsync(options, client, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Keeps_the_path_of_a_GitHub_Enterprise_Server_address()
    {
        var handler = new FakeHttpHandler()
            .Respond("/api/v3/repos/contoso/api/actions/variables?per_page=30&page=1", Variables(("A", "1")));
        using var client = handler.Client();
        var options = Options(environment: null);
        options.IncludeOrganization = false;
        options.IncludeSecrets = false;
        options.ApiBaseAddress = new Uri("https://github.example.com/api/v3");

        var github = await GitHubVariables.LoadAsync(options, client, TestContext.Current.CancellationToken);

        Assert.Single(github.Entries);
    }

    private static GitHubVariablesOptions RepositoryOnly()
    {
        var options = Options(environment: null);
        options.IncludeOrganization = false;
        options.IncludeSecrets = false;
        return options;
    }

    [Fact]
    public async Task Skips_entries_that_are_not_objects_or_have_no_string_name()
    {
        // Found by fuzzing: these used to throw InvalidOperationException out of the trace.
        var handler = new FakeHttpHandler().Respond(
            $"{Repo}/actions/variables?per_page=30&page=1",
            """{"total_count":"five","variables":[1,"x",null,{"name":7,"value":"a"},{"name":"","value":"b"},{"value":"c"},{"name":"OK","value":5,"updated_at":12}]}""");
        using var client = handler.Client();

        var github = await GitHubVariables.LoadAsync(RepositoryOnly(), client, TestContext.Current.CancellationToken);

        var entry = Assert.Single(github.Entries);
        Assert.Equal("OK", entry.Name);
        Assert.Null(entry.Value);
        Assert.Null(entry.UpdatedAt);
    }

    [Theory]
    [InlineData("""{"total_count":1}""")]
    [InlineData("""{"total_count":1,"variables":{}}""")]
    [InlineData("""[]""")]
    public async Task Fails_on_a_response_without_the_array(string body)
    {
        var handler = new FakeHttpHandler().Respond($"{Repo}/actions/variables?per_page=30&page=1", body);
        using var client = handler.Client();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => GitHubVariables.LoadAsync(RepositoryOnly(), client, TestContext.Current.CancellationToken));

        Assert.Equal("GitHub returned an unexpected response for /repos/contoso/api/actions/variables: no 'variables' array.", ex.Message);
    }

    [Fact]
    public async Task Stops_at_a_full_page_when_the_total_is_read()
    {
        var page = JsonSerializer.Serialize(new { total_count = 30, variables = Enumerable.Range(1, 30).Select(i => new { name = $"V{i}", value = "x" }) });
        var handler = new FakeHttpHandler().Respond($"{Repo}/actions/variables?per_page=30&page=1", page);
        using var client = handler.Client();

        var github = await GitHubVariables.LoadAsync(RepositoryOnly(), client, TestContext.Current.CancellationToken);

        Assert.Equal(30, github.Entries.Count);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Asks_for_the_next_page_after_a_full_page_without_a_total()
    {
        var page = JsonSerializer.Serialize(new { variables = Enumerable.Range(1, 30).Select(i => new { name = $"V{i}", value = "x" }) });
        var handler = new FakeHttpHandler()
            .Respond($"{Repo}/actions/variables?per_page=30&page=1", page)
            .Respond($"{Repo}/actions/variables?per_page=30&page=2", """{"variables":[]}""");
        using var client = handler.Client();

        var github = await GitHubVariables.LoadAsync(RepositoryOnly(), client, TestContext.Current.CancellationToken);

        Assert.Equal(30, github.Entries.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Stops_after_100_pages_from_a_server_that_never_ends()
    {
        var handler = new EndlessPages();
        using var client = new HttpClient(handler);

        var github = await GitHubVariables.LoadAsync(RepositoryOnly(), client, TestContext.Current.CancellationToken);

        Assert.Equal(100, handler.Count);
        Assert.Equal(3000, github.Entries.Count);
    }

    private sealed class EndlessPages : HttpMessageHandler
    {
        private static readonly string Page = JsonSerializer.Serialize(new { total_count = 1_000_000, variables = Enumerable.Range(1, 30).Select(i => new { name = $"V{i}", value = "x" }) });

        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Page) });
        }
    }

    [Fact]
    public async Task Names_the_scope_and_kind_of_entries_it_could_not_read()
    {
        var handler = new FakeHttpHandler()
            .Respond($"{Repo}/actions/variables?per_page=30&page=1", Variables(("A", "1")))
            .Respond($"{Repo}/actions/secrets?per_page=100&page=1", """{"message":"Resource not accessible"}""", HttpStatusCode.Forbidden)
            .Respond($"{Repo}/environments/production/variables?per_page=30&page=1", Variables());
        using var client = handler.Client();
        var options = Options();
        options.IncludeOrganization = false;

        var github = await GitHubVariables.LoadAsync(options, client, TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            "Skipped GitHub repository secrets: GitHub returned 403 Forbidden for /repos/contoso/api/actions/secrets. Resource not accessible",
            "Skipped GitHub environment secrets: GitHub returned 404 NotFound for /repos/contoso/api/environments/production/secrets. Not Found",
        ], github.Warnings);
    }

    [Fact]
    public async Task Names_organization_variables_it_could_not_read()
    {
        var handler = new FakeHttpHandler().Respond($"{Repo}/actions/variables?per_page=30&page=1", Variables());
        using var client = handler.Client();
        var options = RepositoryOnly();
        options.IncludeOrganization = true;

        var github = await GitHubVariables.LoadAsync(options, client, TestContext.Current.CancellationToken);

        Assert.StartsWith("Skipped GitHub organization variables: ", Assert.Single(github.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fails_on_other_errors_from_entries_it_may_skip()
    {
        var handler = new FakeHttpHandler()
            .Respond($"{Repo}/actions/variables?per_page=30&page=1", Variables())
            .Respond($"{Repo}/actions/secrets?per_page=100&page=1", "{}", HttpStatusCode.InternalServerError);
        using var client = handler.Client();
        var options = Options(environment: null);
        options.IncludeOrganization = false;

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => GitHubVariables.LoadAsync(options, client, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task Escapes_and_trims_the_names_in_the_path()
    {
        var handler = new FakeHttpHandler()
            .Respond("/repos/my%20org/api/actions/variables?per_page=30&page=1", Variables())
            .Respond("/repos/my%20org/api/environments/prod%2Feu/variables?per_page=30&page=1", Variables(("A", "1")));
        using var client = handler.Client();
        var options = RepositoryOnly();
        options.Owner = "my org";
        options.Repository = " api ";
        options.Environment = "prod/eu";

        var github = await GitHubVariables.LoadAsync(options, client, TestContext.Current.CancellationToken);

        Assert.Equal("prod/eu", Assert.Single(github.Entries).Owner);
    }

    [Fact]
    public async Task Sends_no_authorization_header_without_a_token()
    {
        var handler = new FakeHttpHandler().Respond($"{Repo}/actions/variables?per_page=30&page=1", Variables());
        using var client = handler.Client();
        var options = RepositoryOnly();
        options.Token = "";

        await GitHubVariables.LoadAsync(options, client, TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(handler.Requests).Headers.Authorization);
    }

    [Fact]
    public async Task Checks_its_arguments()
    {
        using var client = new FakeHttpHandler().Client();
        var ct = TestContext.Current.CancellationToken;

        Assert.Throws<ArgumentNullException>(() => new GitHubVariables(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => GitHubVariables.LoadAsync(null!, client, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => GitHubVariables.LoadAsync(Options(), null!, ct));
        var noOwner = Options();
        noOwner.Owner = " ";
        await Assert.ThrowsAsync<ArgumentException>(() => GitHubVariables.LoadAsync(noOwner, client, ct));
        var noRepository = Options();
        noRepository.Repository = "";
        await Assert.ThrowsAsync<ArgumentException>(() => GitHubVariables.LoadAsync(noRepository, client, ct));
    }

    [Fact]
    public async Task Loads_through_its_own_client()
    {
        // An http address that is not loopback is refused before anything is sent.
        var options = Options();
        options.ApiBaseAddress = new Uri("http://github.example.com/");

        await Assert.ThrowsAsync<ArgumentException>(() => GitHubVariables.LoadAsync(options, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Orders_entries_by_scope_and_finds_them_by_kind()
    {
        var github = new GitHubVariables(
        [
            new("A", "env", GitHubVariableKind.Variable, GitHubVariableScope.Environment, "production"),
            new("A", null, GitHubVariableKind.Secret, GitHubVariableScope.Repository, "contoso/api"),
            new("a", "org", GitHubVariableKind.Variable, GitHubVariableScope.Organization, "contoso"),
        ]);

        Assert.Equal([GitHubVariableScope.Organization, GitHubVariableScope.Repository, GitHubVariableScope.Environment], github.Entries.Select(e => e.Scope));
        Assert.Equal(["org", "env"], github.Find("A", GitHubVariableKind.Variable).Select(e => e.Value));
        Assert.Single(github.Find("a", GitHubVariableKind.Secret));
        Assert.Empty(github.Find("B", GitHubVariableKind.Variable));
        Assert.Empty(github.Warnings);
    }
}
