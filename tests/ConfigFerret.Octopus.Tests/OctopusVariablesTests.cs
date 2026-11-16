using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
        Assert.Equal(["Env", "Machine", "Role", "Action", "Channel", "t", "Process"], scope.Values.Select(v => Assert.Single(v)));
        Assert.Equal(ProcessScope.TypeNotGiven, octopus.Entries[0].Process);
    }

    [Theory]
    [InlineData("Region/West", "default,west")]
    [InlineData("region/west", "default,west")]
    [InlineData("West", "default,west,office")]
    [InlineData("Office/West", "default,office")]
    [InlineData("Region/East", "default")]
    public async Task Keeps_tenant_tags_by_canonical_name_so_a_tag_or_its_tag_set_and_tag_matches(string tenantTag, string expected)
    {
        // Octopus scopes a variable to a tenant tag by its canonical name, tag set/tag, and gives only the tag as its name.
        var octopus = await LoadSet("""
            {
              "ScopeValues": {
                "TenantTags": [{"Id":"Region/West","Name":"West"},{"Id":"Region/East","Name":"East"},{"Id":"Office/West","Name":"West"}]
              },
              "Variables": [
                {"Name":"Api:Region","Value":"default","Scope":{}},
                {"Name":"Api:Region","Value":"west","Scope":{"TenantTag":["Region/West"]}},
                {"Name":"Api:Region","Value":"office","Scope":{"TenantTag":["Office/West"]}}
              ]
            }
            """);
        var options = new OctopusTraceOptions();
        options.TenantTags.Add(tenantTag);
        var layer = new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "environment variable Api:Region", Reference = new EnvironmentVariableRef("Api:Region") };

        var chain = new OctopusResolver(octopus, options).Resolve(layer, new TraceDiagnostics());

        Assert.Equal(expected, string.Join(",", chain.Select(s => s.Value)));
        Assert.Equal(["Region/West"], octopus.Entries[1].Scope["TenantTag"]);
    }

    [Theory]
    [InlineData("Rebuild index")]
    [InlineData("Api")]
    [InlineData("Ops/Api")]
    public async Task Leaves_out_values_scoped_only_to_a_runbook_and_ranks_the_deployment_process_above_no_scope(string runbook)
    {
        // Octopus lists the deployment process, named after the project, with the runbooks; a trace is of a deployment.
        // Only the ids tell them apart, since a runbook can have any name.
        var octopus = await LoadSet($$$"""
            {
              "ScopeValues": {
                "Processes": [{"ProcessType":"Deployment","Id":"Projects-1","Name":"Api"},{"ProcessType":"Runbook","Id":"Runbooks-1","Name":"{{{runbook}}}"}]
              },
              "Variables": [
                {"Name":"Db:Timeout","Value":"30","Scope":{}},
                {"Name":"Db:Timeout","Value":"5","Scope":{"ProcessOwner":["Runbooks-1"]}},
                {"Name":"Db:Pool","Value":"20","Scope":{"ProcessOwner":["Runbooks-1","Projects-1"]}},
                {"Name":"Db:Pool","Value":"10","Scope":{}},
                {"Name":"Db:Pool","Value":"40","Scope":{"Channel":["Beta"]}}
              ]
            }
            """);
        var resolver = new OctopusResolver(octopus, new OctopusTraceOptions { Environment = "Production" });
        var diagnostics = new TraceDiagnostics();

        Assert.Equal(
            [ProcessScope.TypeNotGiven, ProcessScope.RunbooksOnly, ProcessScope.Deployment, ProcessScope.TypeNotGiven, ProcessScope.TypeNotGiven],
            octopus.Entries.Select(e => e.Process));
        var timeout = resolver.Resolve(Layer("Db:Timeout", "30"), diagnostics);
        var pool = resolver.Resolve(Layer("Db:Pool", null), diagnostics);

        Assert.Equal(["30"], timeout.Select(s => s.Value));
        Assert.Equal(["10", "20", "40"], pool.Select(s => s.Value));
        Assert.Equal($"matched by name; scoped to process {runbook}, Api", pool[1].Note);
        Assert.Empty(diagnostics.Warnings);

        static ProvenanceStep Layer(string name, string? value) =>
            new() { Kind = LayerKind.DotNetProvider, Source = $"environment variable {name}", Value = value, Reference = new EnvironmentVariableRef(name) };
    }

    [Fact]
    public async Task Skips_a_project_without_a_variable_set()
    {
        var octopus = await LoadSet("{}", """{"Id":"Projects-1","Name":"Api"}""");

        Assert.Empty(octopus.Entries);
        Assert.Single(octopus.Warnings);
    }

    private const string InGitWarning = "Octopus project Api keeps its variables in Git, and those are not read: only the variables stored in Octopus itself, such as its sensitive ones, are traced.";

    [Theory]
    [InlineData("""{"Type":"VersionControlled","ConversionState":{"VariablesAreInGit":true,"RunbooksAreInGit":false}}""", true)]
    [InlineData("""{"Type":"versioncontrolled","ConversionState":{"VariablesAreInGit":true}}""", true)]
    [InlineData("""{"Type":"VersionControlled","ConversionState":{"VariablesAreInGit":false}}""", false)]
    [InlineData("""{"Type":"VersionControlled"}""", false)]
    [InlineData("""{"Type":"VersionControlled","ConversionState":"x"}""", false)]
    [InlineData("""{"Type":"Database"}""", false)]
    [InlineData("""[]""", false)]
    public async Task Warns_that_variables_kept_in_Git_are_not_read(string persistenceSettings, bool inGit)
    {
        // Octopus then keeps only the sensitive project variables in its database, and the rest in the Git repository.
        var octopus = await LoadSet(
            """{"Variables":[{"Name":"Db:Password","Value":null,"Type":"Sensitive","IsSensitive":true}]}""",
            $$"""{"Id":"Projects-1","Name":"Api","VariableSetId":"vs-1","PersistenceSettings":{{persistenceSettings}}}""");

        Assert.Single(octopus.Entries);
        Assert.Equal(
            inGit ? [InGitWarning] : [],
            octopus.Warnings);
    }

    [Theory]
    [InlineData("""{"Type":"VersionControlled","ConversionState":{"VariablesAreInGit":true}}""", InGitWarning)]
    [InlineData("""{"Type":"Database"}""", "Octopus project Api has no variables the API key can see.")]
    public async Task Gives_one_reason_when_no_project_variables_are_stored_in_Octopus(string persistenceSettings, string warning)
    {
        var octopus = await LoadSet(
            """{"Variables":[]}""",
            $$"""{"Id":"Projects-1","Name":"Api","VariableSetId":"vs-1","PersistenceSettings":{{persistenceSettings}}}""");

        Assert.Empty(octopus.Entries);
        Assert.Equal([warning], octopus.Warnings);
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

    [Theory]
    [InlineData("""{"Variables":[{"Name":"A","Value":"1","\ud800":1}]}""")]
    [InlineData("""{"Variables":[{"Name":"\udfff"}]}""")]
    public async Task Reports_a_response_with_a_lone_surrogate_as_an_unexpected_response(string variables)
    {
        // TryGetProperty and GetString throw InvalidOperationException on a lone surrogate escape.
        var handler = new FakeHttpHandler()
            .Respond("/api/spaces/all", """{"Items":[{"Id":"Spaces-1","Name":"Default","IsDefault":true}]}""")
            .Respond("/api/Spaces-1/projects/all", """{"Items":[{"Id":"Projects-1","Name":"Api","VariableSetId":"vs-1"}]}""")
            .Respond("/api/Spaces-1/variables/vs-1", variables);
        using var client = handler.Client();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => OctopusVariables.LoadAsync(Options("api"), client, TestContext.Current.CancellationToken));
        Assert.Contains("not valid UTF-16", ex.Message, StringComparison.Ordinal);
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

    // The real client, not a fake handler: HttpClient removes only the Authorization header when it follows a redirect,
    // so X-Octopus-ApiKey would reach the other address.
    [Fact]
    public async Task Does_not_follow_a_redirect_with_the_API_key_through_its_own_client()
    {
        await using var other = new LoopbackServer(_ => "HTTP/1.1 404 Not Found\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
        await using var octopus = new LoopbackServer(_ => $"HTTP/1.1 302 Found\r\nLocation: {other.Address}api/spaces/all\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        var options = Options();
        options.ServerUri = octopus.Address;

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => OctopusVariables.LoadAsync(options, TestContext.Current.CancellationToken));

        // A followed redirect would be answered, and so recorded, before the load could end.
        Assert.Empty(other.Requests);
        var request = Assert.Single(octopus.Requests);
        Assert.Contains($"X-Octopus-ApiKey: {ApiKey}", request, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Found, ex.StatusCode);
        Assert.Contains("Redirects are not followed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Names_Octopus_in_errors()
    {
        var handler = new FakeHttpHandler().Respond("/api/spaces/all", "{}", HttpStatusCode.InternalServerError);
        using var client = handler.Client();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => OctopusVariables.LoadAsync(Options(), client, TestContext.Current.CancellationToken));

        Assert.StartsWith("Octopus returned 500", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A minimal HTTP server on a free loopback port that records each request head and sends a fixed answer.</summary>
    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<string, string> _respond;
        private readonly Task _serving;

        public LoopbackServer(Func<string, string> respond)
        {
            _respond = respond;
            _listener.Start();
            _serving = ServeAsync();
        }

        public Uri Address => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

        public ConcurrentQueue<string> Requests { get; } = new();

        public async ValueTask DisposeAsync()
        {
            // The token alone ends the loop, since every wait in it takes the token. The listener is stopped only
            // afterwards: an accept that starts on a stopped listener throws "Not listening" before it looks at the
            // token.
            await _stop.CancelAsync();
            await _serving;
            _listener.Stop();
            _listener.Dispose();
            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = client.GetStream();
                    var head = await ReadHeadAsync(stream, _stop.Token);
                    Requests.Enqueue(head);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(_respond(head)), _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
        }

        // A GET has no body, so the request ends with the blank line after the headers.
        private static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var head = new StringBuilder();
            var buffer = new byte[4096];
            while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            return head.ToString();
        }
    }
}
