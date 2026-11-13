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

    private static async Task<OctopusVariables> LoadSet(string variables, string project = """{"Id":"Projects-1","Name":"Api","VariableSetId":"vs-1"}""")
    {
        var handler = new FakeHttpHandler()
            .Respond("/api/Spaces-1/projects/all", $"[{project}]")
            .Respond("/api/Spaces-1/variables/vs-1", variables);
        using var client = handler.Client();
        var options = Options("Api");
        options.Space = "Spaces-1";
        return await OctopusVariables.LoadAsync(options, client, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"Variables":{}}""")]
    [InlineData("""{"ScopeValues":[],"Variables":[]}""")]
    public async Task Warns_about_a_variable_set_without_a_variables_array(string variables)
    {
        // Found by fuzzing: a variable set that is not an object used to throw InvalidOperationException.
        var octopus = await LoadSet(variables);

        Assert.Empty(octopus.Entries);
        Assert.Equal(["Octopus project Api has no variables the API key can see."], octopus.Warnings);
    }

    [Fact]
    public async Task Skips_variables_and_ids_that_are_not_what_Octopus_sends()
    {
        // Found by fuzzing: ids that are not strings used to throw InvalidOperationException.
        var octopus = await LoadSet("""
            {
              "ScopeValues": {
                "Environments": [{"Id":"Environments-1","Name":"Production"},{"Id":"Environments-2"},{"Name":"no id"},7],
                "Machines": {"Id":"Machines-1"},
                "Unknown": [{"Id":"X","Name":"Y"}]
              },
              "Variables": [
                1, {"Name":""}, {"Value":"no name"},
                {"Name":"A","Value":"1","Scope":{"Environment":["Environments-1","Environments-2",7,"",null],"Machine":"Machines-1","Role":[]}},
                {"Name":"B","Value":2,"Type":7,"Scope":[]},
                {"Name":"C","Value":"secret","IsSensitive":true},
                {"Name":"D","Value":"secret","IsSensitive":"true","Type":"sensitive"},
                {"Name":"E","Value":"x","IsSensitive":false,"Type":"Certificate","Scope":{"Machine":["Machines-1"],"Process":["Runbooks-1"]}}
              ]
            }
            """, """{"Id":"Projects-1","Name":"Api","VariableSetId":"vs-1","IncludedLibraryVariableSetIds":[7,null,""]}""");

        Assert.Equal(["A", "B", "C", "D", "E"], octopus.Entries.Select(e => e.Name));
        var a = octopus.Entries[0];
        Assert.Equal(["Production", "Environments-2"], a.Scope["Environment"]);
        Assert.Single(a.Scope);
        var b = octopus.Entries[1];
        Assert.Null(b.Value);
        Assert.Equal("String", b.Type);
        Assert.Empty(b.Scope);
        Assert.True(octopus.Entries[2].IsSensitive);
        Assert.Null(octopus.Entries[2].Value);
        Assert.True(octopus.Entries[3].IsSensitive);
        Assert.Null(octopus.Entries[3].Value);
        var e = octopus.Entries[4];
        Assert.False(e.IsSensitive);
        Assert.Equal("x", e.Value);
        Assert.Equal(["Machines-1"], e.Scope["Machine"]);
        Assert.Equal(["Runbooks-1"], e.Scope["Process"]);
        Assert.Empty(octopus.Warnings);
    }

    [Fact]
    public async Task Maps_every_scope_list_to_its_dimension()
    {
        var octopus = await LoadSet("""
            {
              "ScopeValues": {
                "Environments": [{"Id":"e","Name":"Env"}], "Machines": [{"Id":"m","Name":"Machine"}], "Roles": [{"Id":"r","Name":"Role"}],
                "Actions": [{"Id":"a","Name":"Action"}], "Channels": [{"Id":"c","Name":"Channel"}],
                "TenantTags": [{"Id":"t","Name":"Tag"}], "Processes": [{"Id":"p","Name":"Process"}]
              },
              "Variables": [{"Name":"A","Value":"1","Scope":{
                "Environment":["e"],"Machine":["m"],"Role":["r"],"Action":["a"],"Channel":["c"],"TenantTag":["t"],"ProcessOwner":["p"]}}]
            }
            """);

        var scope = Assert.Single(octopus.Entries).Scope;
        Assert.Equal(["Env", "Machine", "Role", "Action", "Channel", "Tag", "Process"], scope.Values.Select(v => Assert.Single(v)));
    }

    [Fact]
    public async Task Skips_a_project_without_a_variable_set()
    {
        var octopus = await LoadSet("{}", """{"Id":"Projects-1","Name":"Api"}""");

        Assert.Empty(octopus.Entries);
        Assert.Single(octopus.Warnings);
    }

    [Theory]
    [InlineData("""[{"Id":"Spaces-1","Name":"Default","IsDefault":false},{"Id":"Spaces-2","Name":"Other","IsDefault":"true"},5]""")]
    [InlineData("""{"Items":"x"}""")]
    [InlineData("""{"Items":[{"Name":"Default","IsDefault":true}]}""")]
    public async Task Says_when_there_is_no_default_space(string spaces)
    {
        var handler = new FakeHttpHandler().Respond("/api/spaces/all", spaces);
        using var client = handler.Client();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => OctopusVariables.LoadAsync(Options(), client, TestContext.Current.CancellationToken));

        Assert.Equal("Octopus has no default space the API key can see. Set Space.", ex.Message);
    }

    [Fact]
    public async Task Says_when_a_named_space_is_missing()
    {
        var handler = new FakeHttpHandler().Respond("/api/spaces/all", Spaces);
        using var client = handler.Client();
        var options = Options();
        options.Space = "Nope";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => OctopusVariables.LoadAsync(options, client, TestContext.Current.CancellationToken));

        Assert.Equal("Octopus space 'Nope' was not found, or the API key cannot see it.", ex.Message);
    }

    [Fact]
    public async Task Reads_spaces_and_projects_from_a_paged_Items_list()
    {
        var handler = new FakeHttpHandler()
            .Respond("/api/spaces/all", """{"Items":[{"Id":"Spaces-1","Name":"Default","IsDefault":true}]}""")
            .Respond("/api/Spaces-1/projects/all", """{"Items":[{"Id":"Projects-1","Name":"Api","VariableSetId":"vs-1"}]}""")
            .Respond("/api/Spaces-1/variables/vs-1", """{"Variables":[{"Name":"A","Value":"1"}]}""");
        using var client = handler.Client();

        var octopus = await OctopusVariables.LoadAsync(Options("api"), client, TestContext.Current.CancellationToken);

        var entry = Assert.Single(octopus.Entries);
        Assert.Equal("Api", entry.SetName);
        Assert.True(entry.IsProjectVariable);
        Assert.Equal("String", entry.Type);
    }

    [Fact]
    public async Task Prefers_an_id_match_over_a_slug_and_a_slug_over_a_name()
    {
        var handler = new FakeHttpHandler()
            .Respond("/api/Spaces-1/projects/all", """
                [{"Id":"Projects-1","Name":"s","VariableSetId":"vs-name"},
                 {"Id":"Projects-2","Name":"q","Slug":"s","VariableSetId":"vs-slug"},
                 {"Id":"x","Name":"w","VariableSetId":"vs-id"},
                 {"Id":"Projects-3","Name":"x","Slug":"x","VariableSetId":"vs-name"}]
                """)
            .Respond("/api/Spaces-1/variables/vs-id", """{"Variables":[{"Name":"FromId"}]}""")
            .Respond("/api/Spaces-1/variables/vs-slug", """{"Variables":[{"Name":"FromSlug"}]}""")
            .Respond("/api/Spaces-1/variables/vs-name", """{"Variables":[{"Name":"FromName"}]}""");
        using var client = handler.Client();
        var options = Options("x");
        options.Space = "Spaces-1";

        Assert.Equal("FromId", Assert.Single((await OctopusVariables.LoadAsync(options, client, TestContext.Current.CancellationToken)).Entries).Name);

        options.Project = "S";
        Assert.Equal("FromSlug", Assert.Single((await OctopusVariables.LoadAsync(options, client, TestContext.Current.CancellationToken)).Entries).Name);
    }

    [Fact]
    public async Task Names_a_library_set_by_id_when_it_has_no_name()
    {
        var handler = new FakeHttpHandler()
            .Respond("/api/Spaces-1/projects/all", """[{"Id":"Projects-1","Name":"Api","IncludedLibraryVariableSetIds":["Lib-1"]}]""")
            .Respond("/api/Spaces-1/libraryvariablesets/Lib-1", """{"VariableSetId":"vs-lib"}""")
            .Respond("/api/Spaces-1/variables/vs-lib", """{"Variables":[{"Name":"A","Value":"1"}]}""");
        using var client = handler.Client();
        var options = Options("Api");
        options.Space = "Spaces-1";

        var entry = Assert.Single((await OctopusVariables.LoadAsync(options, client, TestContext.Current.CancellationToken)).Entries);

        Assert.Equal("library variable set Lib-1", entry.SetName);
        Assert.False(entry.IsProjectVariable);
    }

    [Fact]
    public async Task Checks_its_arguments()
    {
        using var client = new FakeHttpHandler().Client();
        var ct = TestContext.Current.CancellationToken;

        Assert.Throws<ArgumentNullException>(() => new OctopusVariables(null!));
        Assert.Empty(new OctopusVariables([]).Warnings);
        Assert.Equal(["w"], new OctopusVariables([], ["w"]).Warnings);
        await Assert.ThrowsAsync<ArgumentNullException>(() => OctopusVariables.LoadAsync(null!, client, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => OctopusVariables.LoadAsync(Options(), null!, ct));
        var noKey = Options();
        noKey.ApiKey = " ";
        await Assert.ThrowsAsync<ArgumentException>(() => OctopusVariables.LoadAsync(noKey, client, ct));
        var noProject = Options();
        noProject.Project = "";
        await Assert.ThrowsAsync<ArgumentException>(() => OctopusVariables.LoadAsync(noProject, client, ct));
    }

    [Fact]
    public async Task Loads_through_its_own_client()
    {
        // An http address that is not loopback is refused before anything is sent.
        var options = Options();
        options.ServerUri = new Uri("http://octopus.example.com/");

        await Assert.ThrowsAsync<ArgumentException>(() => OctopusVariables.LoadAsync(options, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Names_Octopus_in_errors()
    {
        var handler = new FakeHttpHandler().Respond("/api/spaces/all", "{}", HttpStatusCode.InternalServerError);
        using var client = handler.Client();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => OctopusVariables.LoadAsync(Options(), client, TestContext.Current.CancellationToken));

        Assert.StartsWith("Octopus returned 500", ex.Message, StringComparison.Ordinal);
    }
}
