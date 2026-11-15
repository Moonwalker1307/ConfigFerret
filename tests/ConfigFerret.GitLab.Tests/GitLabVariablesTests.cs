using System.Net;
using System.Text.Json;

namespace ConfigFerret.GitLab.Tests;

public sealed class GitLabVariablesTests : IDisposable
{
    internal const string Token = "glpat-never_printed_4d2";
    internal const string ProjectPath = "/api/v4/projects/contoso%2Fplatform%2Fapi";

    private readonly ApiHandler _handler = new();
    private readonly HttpClient _client;

    public GitLabVariablesTests() => _client = _handler.Client();

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }

    internal static GitLabOptions Options() => new() { Project = "contoso/platform/api", Token = Token };

    internal static string Project(long id = 42, string kind = "group", string fullPath = "contoso/platform") => JsonSerializer.Serialize(new
    {
        id,
        path_with_namespace = "contoso/platform/api",
        @namespace = new { id = 5, kind, full_path = fullPath },
    });

    internal static string Page(int page) => $"?per_page=100&page={page}";

    internal static object Variable(string key, string? value, string scope = "*", bool masked = false, bool hidden = false, bool @protected = false, string type = "env_var") => new
    {
        key,
        value,
        variable_type = type,
        @protected,
        masked,
        hidden,
        raw = false,
        environment_scope = scope,
        description = (string?)null,
    };

    internal static string Variables(params object[] items) => JsonSerializer.Serialize(items);

    internal static ApiHandler Full(ApiHandler handler) => handler
        .Respond(ProjectPath, Project())
        .Respond("/api/v4/admin/ci/variables" + Page(1), Variables(Variable("DB_TIMEOUT", "5")))
        .Respond("/api/v4/groups/contoso/variables" + Page(1), Variables(Variable("DB_TIMEOUT", "10"), Variable("REGION", "eu")))
        .Respond("/api/v4/groups/contoso%2Fplatform/variables" + Page(1), Variables(Variable("DB_TIMEOUT", "20", "production")))
        .Respond("/api/v4/projects/42/variables" + Page(1), Variables(
            Variable("DB_TIMEOUT", "30"),
            Variable("DB_TIMEOUT", "31", "review/*"),
            Variable("DB_TIMEOUT", "32", "production"),
            Variable("DB_PASSWORD", "hunter2", masked: true),
            Variable("API_KEY", null, hidden: true, masked: true),
            Variable("KUBECONFIG", "apiVersion: v1", type: "file", @protected: true)));

    private Task<GitLabVariables> Load(GitLabOptions options) => GitLabVariables.LoadAsync(options, _client, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Reads_project_and_ancestor_group_variables_outer_group_first()
    {
        Full(_handler);

        var gitlab = await Load(Options());

        Assert.Equal(
            ["contoso:10", "contoso/platform:20", "contoso/platform/api:30", "contoso/platform/api:31", "contoso/platform/api:32"],
            gitlab.Find("DB_TIMEOUT").Select(v => $"{v.Owner}:{v.Value}"));
        Assert.Equal([GitLabVariableLevel.Group, GitLabVariableLevel.Group, GitLabVariableLevel.Project], gitlab.Find("DB_TIMEOUT").Take(3).Select(v => v.Level));
        Assert.Empty(gitlab.Find("db_timeout"));
        Assert.Empty(gitlab.Warnings);
        Assert.DoesNotContain(_handler.Paths, p => p.Contains("admin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reads_instance_variables_only_when_asked()
    {
        Full(_handler);
        var options = Options();
        options.IncludeInstance = true;

        var gitlab = await Load(options);

        var instance = gitlab.Find("DB_TIMEOUT")[0];
        Assert.Equal(GitLabVariableLevel.Instance, instance.Level);
        Assert.Equal("gitlab.com", instance.Owner);
    }

    [Fact]
    public async Task Skips_groups_when_asked_and_for_a_project_in_a_user_namespace()
    {
        Full(_handler);
        var options = Options();
        options.IncludeGroups = false;
        Assert.All((await Load(options)).Entries, e => Assert.Equal(GitLabVariableLevel.Project, e.Level));

        using var userHandler = new ApiHandler()
            .Respond(ProjectPath, Project(kind: "user", fullPath: "someone"))
            .Respond("/api/v4/projects/42/variables" + Page(1), Variables(Variable("A", "1")));
        using var client = userHandler.Client();
        var user = await GitLabVariables.LoadAsync(Options(), client, TestContext.Current.CancellationToken);
        Assert.Equal(2, userHandler.Requests.Count);
        Assert.Single(user.Entries);
    }

    [Fact]
    public async Task Never_keeps_hidden_values_and_keeps_masked_ones_only_when_asked()
    {
        Full(_handler);

        var gitlab = await Load(Options());

        var password = Assert.Single(gitlab.Find("DB_PASSWORD"));
        Assert.True(password.IsMasked);
        Assert.True(password.IsSensitive);
        Assert.Null(password.Value);
        var key = Assert.Single(gitlab.Find("API_KEY"));
        Assert.True(key.IsHidden);
        Assert.Null(key.Value);

        var options = Options();
        options.IncludeMaskedValues = true;
        var withValues = await Load(options);
        Assert.Equal("hunter2", Assert.Single(withValues.Find("DB_PASSWORD")).Value);
        Assert.Null(Assert.Single(withValues.Find("API_KEY")).Value);
    }

    [Fact]
    public async Task Reads_the_file_protected_and_scope_flags()
    {
        Full(_handler);

        var gitlab = await Load(Options());

        var kube = Assert.Single(gitlab.Find("KUBECONFIG"));
        Assert.True(kube.IsFile);
        Assert.True(kube.IsProtected);
        Assert.False(kube.IsSensitive);
        Assert.Equal("apiVersion: v1", kube.Value);
        Assert.Equal(["*", "review/*", "production"], gitlab.Find("DB_TIMEOUT").Skip(2).Select(v => v.EnvironmentScope));
        Assert.False(Assert.Single(gitlab.Find("REGION")).IsFile);
    }

    [Fact]
    public async Task Sends_the_token_only_in_the_private_token_header()
    {
        Full(_handler);

        await Load(Options());

        Assert.Equal(4, _handler.Requests.Count);
        Assert.All(_handler.Requests, r =>
        {
            Assert.Equal(Token, Assert.Single(r.Headers.GetValues("PRIVATE-TOKEN")));
            Assert.Null(r.Headers.Authorization);
            Assert.Contains(r.Headers.Accept, a => a.MediaType == "application/json");
            Assert.DoesNotContain(Token, r.RequestUri!.ToString(), StringComparison.Ordinal);
            Assert.Equal("gitlab.com", r.RequestUri!.Host);
        });
    }

    [Fact]
    public async Task Sends_no_token_header_without_a_token_and_uses_a_self_managed_address()
    {
        var options = Options();
        options.Token = null;
        options.Project = " 42 ";
        options.BaseAddress = new Uri("https://gitlab.example.com/gitlab");
        _handler
            .Respond("/gitlab/api/v4/projects/42", Project(fullPath: "a"))
            .Respond("/gitlab/api/v4/groups/a/variables" + Page(1), "[]")
            .Respond("/gitlab/api/v4/projects/42/variables" + Page(1), "[]");

        var gitlab = await Load(options);

        Assert.Empty(gitlab.Entries);
        Assert.All(_handler.Requests, r => Assert.False(r.Headers.Contains("PRIVATE-TOKEN")));
    }

    [Fact]
    public async Task Follows_the_next_page_header()
    {
        _handler
            .Respond(ProjectPath, Project(kind: "user"))
            .Respond("/api/v4/projects/42/variables" + Page(1), Variables(Variable("A", "1")), HttpStatusCode.OK, ("X-Next-Page", "3"))
            .Respond("/api/v4/projects/42/variables" + Page(3), Variables(Variable("B", "2")), HttpStatusCode.OK, ("X-Next-Page", ""));

        var gitlab = await Load(Options());

        Assert.Equal(["A", "B"], gitlab.Entries.Select(e => e.Key));
    }

    [Fact]
    public async Task Stops_when_the_next_page_header_goes_backwards()
    {
        _handler
            .Respond(ProjectPath, Project(kind: "user"))
            .Respond("/api/v4/projects/42/variables" + Page(1), Variables(Variable("A", "1")), HttpStatusCode.OK, ("X-Next-Page", "1"));

        var gitlab = await Load(Options());

        Assert.Single(gitlab.Entries);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Pages_by_count_without_the_header()
    {
        var full = Enumerable.Range(0, 100).Select(i => Variable($"V{i}", "x")).ToArray();
        _handler
            .Respond(ProjectPath, Project(kind: "user"))
            .Respond("/api/v4/projects/42/variables" + Page(1), Variables(full))
            .Respond("/api/v4/projects/42/variables" + Page(2), Variables(Variable("LAST", "y")));

        var gitlab = await Load(Options());

        Assert.Equal(101, gitlab.Entries.Count);
        Assert.Equal(3, _handler.Requests.Count);
    }

    [Fact]
    public async Task Stops_on_an_empty_page_even_with_a_next_page()
    {
        _handler
            .Respond(ProjectPath, Project(kind: "user"))
            .Respond("/api/v4/projects/42/variables" + Page(1), "[]", HttpStatusCode.OK, ("X-Next-Page", "2"));

        await Load(Options());

        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Stops_after_a_hundred_pages()
    {
        using var handler = new EndlessHandler();
        using var client = new HttpClient(handler, disposeHandler: false);

        var gitlab = await GitLabVariables.LoadAsync(Options(), client, TestContext.Current.CancellationToken);

        Assert.Equal(101, handler.Count);
        Assert.Equal(100, gitlab.Entries.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Warns_and_goes_on_when_group_or_instance_variables_cannot_be_read(HttpStatusCode status)
    {
        Full(_handler).Respond("/api/v4/groups/contoso/variables" + Page(1), """{"message":"403 Forbidden"}""", status)
            .Respond("/api/v4/admin/ci/variables" + Page(1), """{"message":"403 Forbidden"}""", status);
        var options = Options();
        options.IncludeInstance = true;

        var gitlab = await Load(options);

        Assert.Equal(2, gitlab.Warnings.Count);
        Assert.StartsWith("Skipped GitLab instance variables of gitlab.com: GitLab returned", gitlab.Warnings[0], StringComparison.Ordinal);
        Assert.StartsWith("Skipped GitLab group variables of contoso: GitLab returned", gitlab.Warnings[1], StringComparison.Ordinal);
        Assert.All(gitlab.Warnings, w => Assert.DoesNotContain(Token, w, StringComparison.Ordinal));
        Assert.Equal(["contoso/platform", "contoso/platform/api"], gitlab.Find("DB_TIMEOUT").Select(v => v.Owner).Distinct());
    }

    [Fact]
    public async Task Drops_a_group_page_already_read_when_a_later_page_fails()
    {
        var full = Enumerable.Range(0, 100).Select(i => Variable($"G{i}", "x")).ToArray();
        _handler
            .Respond(ProjectPath, Project(fullPath: "contoso"))
            .Respond("/api/v4/groups/contoso/variables" + Page(1), Variables(full))
            .Respond("/api/v4/projects/42/variables" + Page(1), "[]");

        var gitlab = await Load(Options());

        Assert.Empty(gitlab.Entries);
        Assert.Single(gitlab.Warnings);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Fails_when_the_project_variables_cannot_be_read(HttpStatusCode status)
    {
        _handler.Respond(ProjectPath, Project(kind: "user")).Respond("/api/v4/projects/42/variables" + Page(1), """{"message":"403 Forbidden"}""", status);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Load(Options()));

        Assert.Equal(status, ex.StatusCode);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fails_when_the_project_is_not_found_or_a_group_error_is_not_access()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Load(Options()));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal($"GitLab returned 404 NotFound for {ProjectPath}. 404 Not Found", ex.Message);

        Full(_handler).Respond("/api/v4/groups/contoso/variables" + Page(1), "{}", HttpStatusCode.BadGateway);
        ex = await Assert.ThrowsAsync<HttpRequestException>(() => Load(Options()));
        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"id":"42"}""")]
    [InlineData("""{"name":"api"}""")]
    public async Task Fails_on_a_project_without_an_id(string body)
    {
        _handler.Respond(ProjectPath, body);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Load(Options()));

        Assert.Equal($"GitLab returned an unexpected response for {ProjectPath}: no project id.", ex.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("\"x\"")]
    public async Task Fails_on_variables_that_are_not_an_array(string body)
    {
        _handler.Respond(ProjectPath, Project(kind: "user")).Respond("/api/v4/projects/42/variables" + Page(1), body);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Load(Options()));

        Assert.Equal("GitLab returned an unexpected response for /api/v4/projects/42/variables: not an array of variables.", ex.Message);
    }

    [Fact]
    public async Task Uses_the_given_project_when_the_project_has_no_path_and_skips_odd_variables()
    {
        const string variables = """
            [1, null, {"value":"no key"}, {"key":""}, {"key":5}, {"key":"\ud800"},
             {"key":"A","value":7,"environment_scope":"","masked":"true","variable_type":"File"},
             {"\udc00":1,"key":"B","value":"b","hidden":false}]
            """;
        _handler
            .Respond(ProjectPath, """{"id":42,"path_with_namespace":" ","namespace":{"kind":"group","full_path":""}}""")
            .Respond("/api/v4/projects/42/variables" + Page(1), variables);

        var gitlab = await Load(Options());

        Assert.Equal(["A", "B"], gitlab.Entries.Select(e => e.Key));
        var a = gitlab.Entries[0];
        Assert.Null(a.Value);
        Assert.Equal("*", a.EnvironmentScope);
        Assert.False(a.IsMasked);
        Assert.False(a.IsFile);
        Assert.Equal("contoso/platform/api", a.Owner);
        Assert.Equal("b", gitlab.Entries[1].Value);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Checks_the_options()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => GitLabVariables.LoadAsync(null!, _client, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => GitLabVariables.LoadAsync(Options(), null!, TestContext.Current.CancellationToken));
        var noProject = Options();
        noProject.Project = " ";
        await Assert.ThrowsAsync<ArgumentException>(() => Load(noProject));
        var http = Options();
        http.BaseAddress = new Uri("http://gitlab.example.com/");
        await Assert.ThrowsAsync<ArgumentException>(() => Load(http));
        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [InlineData("*", null, 1)]
    [InlineData("", null, 1)]
    [InlineData(null, "production", 1)]
    [InlineData("production", null, 0)]
    [InlineData("production", "", 0)]
    [InlineData("production", "production", int.MaxValue)]
    [InlineData("Production", "production", 0)]
    [InlineData("review/*", "review/feature-1", 9)]
    [InlineData("review/*", "review/", 9)]
    [InlineData("review/*", "staging", 0)]
    [InlineData("*-eu", "prod-eu", 5)]
    [InlineData("*-eu", "prod-us", 0)]
    [InlineData("a*b*c", "aXbYc", 6)]
    [InlineData("a*b*c", "aXcYb", 0)]
    [InlineData("ab*ab", "ab", 0)]
    [InlineData("ab*ab", "abab", 6)]
    [InlineData("staging", "staging-2", 0)]
    public void Ranks_environment_scopes(string? scope, string? environment, int expected)
    {
        Assert.Equal(expected, GitLabVariables.ScopeRank(scope, environment));
    }

    [Fact]
    public void Picks_the_best_scope_per_owner_lowest_priority_first()
    {
        var gitlab = new GitLabVariables(
        [
            new("DB_TIMEOUT", "30", GitLabVariableLevel.Project, "p"),
            new("DB_TIMEOUT", "31", GitLabVariableLevel.Project, "p", "review/*"),
            new("DB_TIMEOUT", "32", GitLabVariableLevel.Project, "p", "production"),
            new("DB_TIMEOUT", "33", GitLabVariableLevel.Project, "p", "review/feature-*"),
            new("DB_TIMEOUT", "10", GitLabVariableLevel.Group, "g"),
            new("DB_TIMEOUT", "11", GitLabVariableLevel.Group, "g", "staging"),
            new("DB_TIMEOUT", "5", GitLabVariableLevel.Instance, "i"),
        ]);

        Assert.Equal(["5", "10", "30"], gitlab.Effective("DB_TIMEOUT", null).Select(v => v.Value));
        Assert.Equal(["5", "10", "32"], gitlab.Effective("DB_TIMEOUT", "production").Select(v => v.Value));
        Assert.Equal(["5", "10", "33"], gitlab.Effective("DB_TIMEOUT", "review/feature-1").Select(v => v.Value));
        Assert.Equal(["5", "10", "31"], gitlab.Effective("DB_TIMEOUT", "review/fix-1").Select(v => v.Value));
        Assert.Equal(["5", "11", "30"], gitlab.Effective("DB_TIMEOUT", "staging").Select(v => v.Value));
        Assert.Empty(gitlab.Effective("OTHER", "production"));
        Assert.Throws<ArgumentNullException>(() => gitlab.Effective(null!, null));
        Assert.Throws<ArgumentNullException>(() => gitlab.Find(null!));
        Assert.Throws<ArgumentNullException>(() => new GitLabVariables(null!));
    }

    [Fact]
    public void Marks_masked_or_hidden_variables_sensitive()
    {
        Assert.True(new GitLabVariable("A", null, GitLabVariableLevel.Project, "p", IsMasked: true).IsSensitive);
        Assert.True(new GitLabVariable("A", null, GitLabVariableLevel.Project, "p", IsHidden: true).IsSensitive);
        Assert.False(new GitLabVariable("A", null, GitLabVariableLevel.Project, "p", IsProtected: true, IsFile: true).IsSensitive);
    }

    private sealed class EndlessHandler : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _sent = [];

        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            var body = request.RequestUri!.AbsolutePath.EndsWith("/variables", StringComparison.Ordinal)
                ? Variables(Variable("V" + Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "x"))
                : Project(kind: "user");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            var query = request.RequestUri!.Query;
            var page = query.Contains("page=", StringComparison.Ordinal) ? int.Parse(query[(query.LastIndexOf('=') + 1)..], System.Globalization.CultureInfo.InvariantCulture) : 0;
            response.Headers.Add("X-Next-Page", (page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            _sent.Add(response);
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _sent.ForEach(r => r.Dispose());
            }

            base.Dispose(disposing);
        }
    }
}
