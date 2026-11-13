using System.Net;
using ConfigFerret.Tests.Shared;

namespace ConfigFerret.Octopus.Tests;

public class OctopusVariablesTests
{
    private const string ApiKey = "API-TESTKEYNEVERPRINTED";

    private const string Spaces = """[{"Id":"Spaces-1","Name":"Default","IsDefault":true},{"Id":"Spaces-2","Name":"Platform","IsDefault":false}]""";

    private const string Projects = """
        [
          {"Id":"Projects-7","Name":"Billing API","Slug":"billing-api","VariableSetId":"variableset-Projects-7","IncludedLibraryVariableSetIds":["LibraryVariableSets-3"]},
          {"Id":"Projects-8","Name":"Other","Slug":"other","VariableSetId":"variableset-Projects-8","IncludedLibraryVariableSetIds":[]}
        ]
        """;

    private const string ProjectVariables = """
        {
          "Id": "variableset-Projects-7",
          "Variables": [
            {"Id":"1","Name":"Db:Timeout","Value":"30","Type":"String","IsSensitive":false,"Scope":{"Environment":["Environments-2"]}},
            {"Id":"2","Name":"Db:Timeout","Value":"10","Type":"String","IsSensitive":false,"Scope":{}},
            {"Id":"3","Name":"Db:Password","Value":null,"Type":"Sensitive","IsSensitive":true,"Scope":{"Role":["web"]}}
          ],
          "ScopeValues": {
            "Environments": [{"Id":"Environments-1","Name":"Staging"},{"Id":"Environments-2","Name":"Production"}],
            "Roles": [{"Id":"web","Name":"web"}],
            "Machines": [], "Actions": [], "Channels": [], "TenantTags": []
          }
        }
        """;

    private const string LibrarySet = """{"Id":"LibraryVariableSets-3","Name":"Shared","VariableSetId":"variableset-LibraryVariableSets-3"}""";

    private const string LibraryVariables = """
        {"Id":"variableset-LibraryVariableSets-3","Variables":[{"Id":"9","Name":"Region","Value":"eu","Type":"String","Scope":{}}],"ScopeValues":{}}
        """;

    private static FakeHttpHandler Server(string prefix = "") => new FakeHttpHandler()
        .Respond($"{prefix}/api/spaces/all", Spaces)
        .Respond($"{prefix}/api/Spaces-1/projects/all", Projects)
        .Respond($"{prefix}/api/Spaces-1/variables/variableset-Projects-7", ProjectVariables)
        .Respond($"{prefix}/api/Spaces-1/libraryvariablesets/LibraryVariableSets-3", LibrarySet)
        .Respond($"{prefix}/api/Spaces-1/variables/variableset-LibraryVariableSets-3", LibraryVariables);

    private static OctopusVariablesOptions Options(string project = "billing-api") => new()
    {
        ServerUri = new Uri("https://octopus.example.com/"),
        ApiKey = ApiKey,
        Project = project,
    };

    [Theory]
    [InlineData("billing-api")]
    [InlineData("Billing API")]
    [InlineData("Projects-7")]
    public async Task Reads_project_and_library_variables_with_scope_names(string project)
    {
        var handler = Server();
        using var client = handler.Client();

        var octopus = await OctopusVariables.LoadAsync(Options(project), client, TestContext.Current.CancellationToken);

        Assert.Equal(4, octopus.Entries.Count);
        var timeout = octopus.Find("db:timeout");
        Assert.Equal(2, timeout.Count);
        Assert.Equal(["Production"], timeout[0].Scope["Environment"]);
        Assert.Empty(timeout[1].Scope);
        Assert.All(timeout, t => Assert.Equal("Billing API", t.SetName));
        Assert.All(timeout, t => Assert.True(t.IsProjectVariable));

        var password = Assert.Single(octopus.Find("Db:Password"));
        Assert.True(password.IsSensitive);
        Assert.Null(password.Value);

        var region = Assert.Single(octopus.Find("Region"));
        Assert.False(region.IsProjectVariable);
        Assert.Equal("library variable set Shared", region.SetName);
    }

    [Fact]
    public async Task Sends_the_API_key_only_in_its_header()
    {
        var handler = Server();
        using var client = handler.Client();

        await OctopusVariables.LoadAsync(Options(), client, TestContext.Current.CancellationToken);

        Assert.Equal(5, handler.Requests.Count);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(ApiKey, Assert.Single(r.Headers.GetValues("X-Octopus-ApiKey")));
            Assert.Null(r.Headers.Authorization);
            Assert.DoesNotContain(ApiKey, r.RequestUri!.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Uses_a_space_id_directly_and_looks_up_a_space_name()
    {
        var handler = Server();
        using var client = handler.Client();
        var options = Options();
        options.Space = "Spaces-1";

        await OctopusVariables.LoadAsync(options, client, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath == "/api/spaces/all");

        options.Space = "nope";
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => OctopusVariables.LoadAsync(options, client, TestContext.Current.CancellationToken));
        Assert.Contains("'nope'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keeps_a_path_prefix_on_the_server_address()
    {
        var handler = Server("/octopus");
        using var client = handler.Client();
        var options = Options();
        options.ServerUri = new Uri("https://example.com/octopus");

        var octopus = await OctopusVariables.LoadAsync(options, client, TestContext.Current.CancellationToken);

        Assert.Equal(4, octopus.Entries.Count);
    }

    [Fact]
    public async Task Can_skip_library_variable_sets()
    {
        var handler = Server();
        using var client = handler.Client();
        var options = Options();
        options.IncludeLibraryVariableSets = false;

        var octopus = await OctopusVariables.LoadAsync(options, client, TestContext.Current.CancellationToken);

        Assert.Equal(3, octopus.Entries.Count);
    }

    [Fact]
    public async Task Reports_a_missing_project()
    {
        using var client = Server().Client();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => OctopusVariables.LoadAsync(Options("missing"), client, TestContext.Current.CancellationToken));

        Assert.Contains("'missing'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fails_with_the_status_and_Octopus_error_but_not_the_API_key()
    {
        var handler = new FakeHttpHandler()
            .Respond("/api/spaces/all", """{"ErrorMessage":"You must be logged in to perform this action."}""", HttpStatusCode.Unauthorized);
        using var client = handler.Client();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => OctopusVariables.LoadAsync(Options(), client, TestContext.Current.CancellationToken));

        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
        Assert.Contains("logged in", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://octopus.example.com/", false)]
    [InlineData("https://admin:pw@octopus.example.com/", false)]
    [InlineData("http://localhost:8065/", true)]
    public async Task Refuses_an_address_that_would_leak_the_API_key(string address, bool allowed)
    {
        var handler = Server();
        using var client = handler.Client();
        var options = Options();
        options.ServerUri = new Uri(address);

        if (allowed)
        {
            await OctopusVariables.LoadAsync(options, client, TestContext.Current.CancellationToken);
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() => OctopusVariables.LoadAsync(options, client, TestContext.Current.CancellationToken));
            Assert.Empty(handler.Requests);
        }
    }
}
