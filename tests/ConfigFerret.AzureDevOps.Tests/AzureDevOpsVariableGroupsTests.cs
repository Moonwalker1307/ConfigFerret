using System.Net;
using System.Text;
using System.Text.Json;

namespace ConfigFerret.AzureDevOps.Tests;

public sealed class AzureDevOpsVariableGroupsTests : IDisposable
{
    internal const string Token = "ado_pat_never_printed_7c1";
    private const string Endpoint = "/contoso/api/_apis/distributedtask/variablegroups";

    private readonly ApiHandler _handler = new();
    private readonly HttpClient _client;

    public AzureDevOpsVariableGroupsTests() => _client = _handler.Client();

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }

    internal static AzureDevOpsOptions Options(params string[] groups)
    {
        var options = new AzureDevOpsOptions
        {
            OrganizationUrl = new Uri("https://dev.azure.com/contoso/"),
            Project = "api",
            PersonalAccessToken = Token,
        };
        foreach (var group in groups)
        {
            options.GroupNames.Add(group);
        }

        return options;
    }

    internal static string Query(string? group, string? continuation = null) =>
        Endpoint + "?" + (group is null ? string.Empty : $"groupName={Uri.EscapeDataString(group)}&")
        + "queryOrder=IdAscending&$top=100" + (continuation is null ? string.Empty : $"&continuationToken={continuation}") + "&api-version=7.1";

    internal static object Group(int id, string name, object variables, string type = "Vsts", string? vault = null) => new
    {
        id,
        name,
        type,
        description = $"{name} settings",
        modifiedOn = "2026-03-04T05:06:07Z",
        providerData = vault is null ? null : new { serviceEndpointId = "e1", vault },
        variables,
    };

    internal static string Page(params object[] groups) => JsonSerializer.Serialize(new { count = groups.Length, value = groups });

    private static readonly object Production = Group(7, "api-production", new Dictionary<string, object>
    {
        ["Db.Timeout"] = new { value = "30" },
        ["Db.Password"] = new { isSecret = true, value = "leaked-if-kept" },
        ["Region"] = new { value = "eu", isReadOnly = true },
    });

    private static readonly object Shared = Group(3, "shared", new Dictionary<string, object> { ["Db.Timeout"] = new { value = "10" } });

    private Task<AzureDevOpsVariableGroups> Load(AzureDevOpsOptions options) =>
        AzureDevOpsVariableGroups.LoadAsync(options, _client, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Reads_the_named_groups_in_the_order_given()
    {
        _handler.Respond(Query("shared"), Page(Shared)).Respond(Query("api-production"), Page(Production));

        var groups = await Load(Options("shared", "api-production"));

        Assert.Equal("contoso/api", groups.Project);
        Assert.Equal(["shared", "api-production"], groups.Groups.Select(g => g.Name));
        var production = groups.Groups[1];
        Assert.Equal(7, production.Id);
        Assert.Equal(AzureDevOpsGroupType.Vsts, production.Type);
        Assert.Equal("api-production settings", production.Description);
        Assert.Null(production.KeyVaultName);
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero), production.ModifiedOn);
        Assert.Equal(["10", "30"], groups.Find("db.timeout").Select(v => v.Value));
        Assert.Equal(["shared", "api-production"], groups.Find("DB.TIMEOUT").Select(v => v.Group));
        Assert.Equal(4, groups.Variables.Count);
        Assert.Empty(groups.Warnings);
    }

    [Fact]
    public async Task Never_keeps_a_secret_value_and_marks_secrets_sensitive()
    {
        _handler.Respond(Query("api-production"), Page(Production));

        var groups = await Load(Options("api-production"));

        var password = Assert.Single(groups.Find("Db.Password"));
        Assert.True(password.IsSecret);
        Assert.True(password.IsSensitive);
        Assert.Null(password.Value);
        Assert.Equal(7, password.GroupId);
        Assert.False(Assert.Single(groups.Find("Region")).IsSensitive);
    }

    [Fact]
    public async Task Sends_the_token_only_in_a_basic_header_with_an_empty_user()
    {
        _handler.Respond(Query("api-production"), Page(Production));

        await Load(Options("api-production"));

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
        Assert.Equal(":" + Token, Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization!.Parameter!)));
        Assert.Equal("Suppress", Assert.Single(request.Headers.GetValues("X-TFS-FedAuthRedirect")));
        Assert.Contains(request.Headers.Accept, a => a.MediaType == "application/json");
        Assert.DoesNotContain(Token, request.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Equal("dev.azure.com", request.RequestUri!.Host);
    }

    [Fact]
    public async Task Sends_an_access_token_as_a_bearer_token()
    {
        _handler.Respond(Query(null), Page(Production));
        var options = Options();
        options.PersonalAccessToken = null;
        options.AccessToken = " entra-token ";

        await Load(options);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("entra-token", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task Needs_exactly_one_credential()
    {
        var none = Options();
        none.PersonalAccessToken = " ";
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Load(none));
        Assert.StartsWith("Set PersonalAccessToken or AccessToken: Azure DevOps", ex.Message, StringComparison.Ordinal);

        var both = Options();
        both.AccessToken = "entra";
        ex = await Assert.ThrowsAsync<ArgumentException>(() => Load(both));
        Assert.StartsWith("Set PersonalAccessToken or AccessToken, not both.", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Checks_the_options()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => AzureDevOpsVariableGroups.LoadAsync(null!, _client, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => AzureDevOpsVariableGroups.LoadAsync(Options(), null!, TestContext.Current.CancellationToken));

        var noUrl = Options();
        noUrl.OrganizationUrl = null;
        await Assert.ThrowsAsync<ArgumentException>(() => Load(noUrl));

        var http = Options();
        http.OrganizationUrl = new Uri("http://tfs.example.com/tfs/DefaultCollection/");
        await Assert.ThrowsAsync<ArgumentException>(() => Load(http));

        var noProject = Options();
        noProject.Project = " ";
        await Assert.ThrowsAsync<ArgumentException>(() => Load(noProject));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Reads_every_group_by_id_when_no_names_are_given_following_the_continuation_token()
    {
        _handler
            .Respond(Query(null), Page(Production), HttpStatusCode.OK, ("X-MS-ContinuationToken", "8"))
            .Respond(Query(null, "8"), Page(Shared, Production), HttpStatusCode.OK, ("X-MS-ContinuationToken", "8"));

        var groups = await Load(Options());

        Assert.Equal([3, 7], groups.Groups.Select(g => g.Id));
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Stops_on_an_empty_page_even_with_a_token()
    {
        _handler.Respond(Query(null), Page(), HttpStatusCode.OK, ("X-MS-ContinuationToken", "8"));

        var groups = await Load(Options());

        Assert.Empty(groups.Groups);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Stops_after_a_hundred_pages()
    {
        using var handler = new EndlessHandler();
        using var client = new HttpClient(handler, disposeHandler: false);

        var groups = await AzureDevOpsVariableGroups.LoadAsync(Options(), client, TestContext.Current.CancellationToken);

        Assert.Equal(100, handler.Count);
        Assert.Single(groups.Groups);
    }

    [Fact]
    public async Task Warns_about_a_named_group_that_is_missing_but_not_about_a_wildcard()
    {
        _handler.Respond(Query("gone"), Page()).Respond(Query("api-*"), Page(Production)).Respond(Query("api-production"), Page(Production));

        var groups = await Load(Options("gone", "api-*", "api-production"));

        Assert.Equal(["api-production"], groups.Groups.Select(g => g.Name));
        var warning = Assert.Single(groups.Warnings);
        Assert.Equal("Azure DevOps variable group gone was not found in project contoso/api, or the token cannot read it.", warning);
    }

    [Fact]
    public async Task Reads_a_key_vault_linked_group_without_values()
    {
        var vault = Group(9, "kv", new Dictionary<string, object> { ["Db--Password"] = new { value = "nope", isSecret = false } }, "AzureKeyVault", "contoso-prod");
        _handler.Respond(Query("kv"), Page(vault));

        var groups = await Load(Options("kv"));

        var group = Assert.Single(groups.Groups);
        Assert.Equal(AzureDevOpsGroupType.AzureKeyVault, group.Type);
        Assert.Equal("contoso-prod", group.KeyVaultName);
        var secret = Assert.Single(group.Variables);
        Assert.Null(secret.Value);
        Assert.True(secret.IsSensitive);
        Assert.False(secret.IsSecret);
        Assert.Equal("contoso-prod", secret.KeyVaultName);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Found)]
    public async Task Fails_with_the_status_and_never_the_token(HttpStatusCode status)
    {
        _handler.Respond(Query("a"), """{"message":"TF400813: The user is not authorized"}""", status);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Load(Options("a")));

        Assert.Equal(status, ex.StatusCode);
        Assert.StartsWith("Azure DevOps returned", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_a_sign_in_page_sent_with_203_as_an_unaccepted_token()
    {
        _handler.Respond(Query("a"), "<html>sign in</html>", HttpStatusCode.NonAuthoritativeInformation);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Load(Options("a")));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("203", ex.Message, StringComparison.Ordinal);
        Assert.Contains("the token was not accepted", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"count":0}""")]
    [InlineData("""{"value":{}}""")]
    [InlineData("42")]
    public async Task Fails_on_a_response_without_a_value_array(string body)
    {
        _handler.Respond(Query("a"), body);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Load(Options("a")));

        Assert.Equal($"Azure DevOps returned an unexpected response for {Endpoint}: no 'value' array.", ex.Message);
    }

    [Fact]
    public async Task Skips_groups_and_variables_it_cannot_read()
    {
        const string body = """
            {"value":[
              1, "x", null, {"name":"no-id"}, {"id":"1","name":"string-id"}, {"id":1}, {"id":2,"name":"  "},
              {"id":99999999999,"name":"too-big"},
              {"id":4,"name":"odd","type":"Custom","variables":{"":{"value":"empty name"},"A":"not an object","B":{"value":5},"C":{"value":"ok","isSecret":"true"},"\ud800":{"value":"lone"}}},
              {"id":5,"name":"no-vars","variables":[1,2]},
              {"id":6,"\udc00":1,"name":"surrogate-key","type":null}
            ]}
            """;
        _handler.Respond(Query("a"), body);

        var groups = await Load(Options("a"));

        Assert.Equal(["odd", "no-vars", "surrogate-key"], groups.Groups.Select(g => g.Name));
        var odd = groups.Groups[0];
        Assert.Equal(AzureDevOpsGroupType.Other, odd.Type);
        Assert.Equal(["A", "B", "C"], odd.Variables.Select(v => v.Name));
        Assert.All(odd.Variables.Take(2), v => Assert.Null(v.Value));
        Assert.Equal("ok", odd.Variables[2].Value);
        Assert.False(odd.Variables[2].IsSecret);
        Assert.Empty(groups.Groups[1].Variables);
        Assert.Equal(AzureDevOpsGroupType.Vsts, groups.Groups[2].Type);
    }

    [Fact]
    public async Task Fails_on_a_response_that_is_not_json()
    {
        _handler.Respond(Query("a"), "<html/>");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Load(Options("a")));

        Assert.Contains("not JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uses_a_server_collection_url_and_escapes_the_project_and_group()
    {
        var options = Options("my group");
        options.OrganizationUrl = new Uri("https://tfs.example.com/tfs/DefaultCollection");
        options.Project = " My Project ";
        _handler.Respond("/tfs/DefaultCollection/My%20Project/_apis/distributedtask/variablegroups?groupName=my%20group&queryOrder=IdAscending&$top=100&api-version=7.1", Page(Shared));

        var groups = await Load(options);

        Assert.Equal("DefaultCollection/My Project", groups.Project);
        Assert.Single(groups.Groups);
    }

    [Theory]
    [InlineData("https://dev.azure.com/contoso/", "contoso")]
    [InlineData("https://contoso.visualstudio.com/", "contoso")]
    [InlineData("https://tfs.example.com/", "tfs.example.com")]
    [InlineData("https://tfs.example.com/tfs/Default%20Collection/", "Default Collection")]
    public void Names_the_organization_from_its_url(string url, string expected)
    {
        Assert.Equal(expected, AzureDevOpsVariableGroups.OrganizationName(new Uri(url)));
    }

    [Theory]
    [InlineData("Db.Timeout", "DB_TIMEOUT")]
    [InlineData("db timeout", "DB_TIMEOUT")]
    [InlineData("Db__Timeout", "DB__TIMEOUT")]
    public void Turns_a_variable_name_into_the_environment_name_scripts_see(string name, string expected)
    {
        Assert.Equal(expected, AzureDevOpsVariableGroups.ToEnvironmentName(name));
        Assert.Equal(expected, new AzureDevOpsVariable(name, null, false, "g", 1).EnvironmentName);
    }

    [Fact]
    public void Finds_variables_by_the_environment_name_or_the_exact_name()
    {
        var groups = new AzureDevOpsVariableGroups("contoso/api",
        [
            new(1, "g", AzureDevOpsGroupType.Vsts, null, null, null,
            [
                new("Db.Timeout", "30", false, "g", 1),
                new("DB_TIMEOUT", "40", false, "g", 1),
                new("Other", "1", false, "g", 1),
            ]),
        ]);

        Assert.Equal(["30", "40"], groups.FindByEnvironmentName("db_timeout").Select(v => v.Value));
        Assert.Empty(groups.Find("DB_TIMEOUTS"));
        Assert.Throws<ArgumentNullException>(() => groups.Find(null!));
        Assert.Throws<ArgumentNullException>(() => groups.FindByEnvironmentName(null!));
        Assert.Throws<ArgumentNullException>(() => AzureDevOpsVariableGroups.ToEnvironmentName(null!));
        Assert.Throws<ArgumentException>(() => new AzureDevOpsVariableGroups(" ", []));
        Assert.Throws<ArgumentNullException>(() => new AzureDevOpsVariableGroups("p", null!));
    }

    private sealed class EndlessHandler : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _sent = [];

        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Page(Shared)) };
            response.Headers.Add("X-MS-ContinuationToken", Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
