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
}
