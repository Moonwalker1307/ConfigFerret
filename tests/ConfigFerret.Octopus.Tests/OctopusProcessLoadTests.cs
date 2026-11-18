using System.Net;
using ConfigFerret.Tests.Shared;

namespace ConfigFerret.Octopus.Tests;

public class OctopusProcessLoadTests
{
    private const string ApiKey = "API-TESTKEYNEVERPRINTED";

    private const string Spaces = """[{"Id":"Spaces-1","Name":"Default","IsDefault":true}]""";

    private const string Projects = """
        [{"Id":"Projects-7","Name":"Billing API","Slug":"billing-api","VariableSetId":"variableset-Projects-7","IncludedLibraryVariableSetIds":["LibraryVariableSets-3"]}]
        """;

    private const string VersionControlledProjects = """
        [{"Id":"Projects-7","Name":"Billing API","Slug":"billing-api","VariableSetId":"variableset-Projects-7","IncludedLibraryVariableSetIds":[],
          "PersistenceSettings":{"Type":"VersionControlled","DefaultBranch":"develop"}}]
        """;

    private const string ProjectVariables = """
        {
          "Variables": [{"Name":"Db:Timeout","Value":"30","Type":"String","Scope":{}}],
          "ScopeValues": {
            "Environments": [{"Id":"Environments-1","Name":"Staging"},{"Id":"Environments-2","Name":"Production"}],
            "Channels": [{"Id":"Channels-1","Name":"Default"}]
          }
        }
        """;

    private const string LibrarySet = """{"Id":"LibraryVariableSets-3","Name":"Shared","VariableSetId":"variableset-LibraryVariableSets-3"}""";

    private const string LibraryVariables = """{"Variables":[{"Name":"Region","Value":"eu","Type":"String","Scope":{}}]}""";

    private const string Process = """
        {
          "Id": "deploymentprocess-Projects-7",
          "Steps": [
            {
              "Name": "Deploy web",
              "Properties": {"Octopus.Action.TargetRoles": "web, api"},
              "Actions": [{
                "Name": "Deploy web",
                "ActionType": "Octopus.TentaclePackage",
                "Environments": ["Environments-2"],
                "ExcludedEnvironments": ["Environments-9"],
                "Channels": ["Channels-1"],
                "Properties": {
                  "Octopus.Action.EnabledFeatures": "Octopus.Features.JsonConfigurationVariables,Octopus.Features.SubstituteInFiles,Octopus.Features.ConfigurationTransforms",
                  "Octopus.Action.Package.JsonConfigurationVariablesTargets": "appsettings.json\nconfig/*.json\n",
                  "Octopus.Action.SubstituteInFiles.TargetFiles": "web.template",
                  "Octopus.Action.Template.Id": "ActionTemplates-4",
                  "Octopus.Action.Template.Version": "3",
                  "Greeting": "hello",
                  "ApiToken": {"HasValue": true, "NewValue": null}
                }
              }]
            },
            {
              "Name": "Notify",
              "Actions": [{"Name": "Notify", "ActionType": "Octopus.Script", "IsDisabled": true, "Properties": {"Octopus.Action.Package.JsonConfigurationVariablesEnabled": "True"}}]
            },
            "not a step",
            {"Name": "No actions"}
          ]
        }
        """;

    private static FakeHttpHandler Server(string projects = Projects) => new FakeHttpHandler()
        .Respond("/api/spaces/all", Spaces)
        .Respond("/api/Spaces-1/projects/all", projects)
        .Respond("/api/Spaces-1/variables/variableset-Projects-7", ProjectVariables)
        .Respond("/api/Spaces-1/libraryvariablesets/LibraryVariableSets-3", LibrarySet)
        .Respond("/api/Spaces-1/variables/variableset-LibraryVariableSets-3", LibraryVariables)
        .Respond("/api/Spaces-1/actiontemplates/ActionTemplates-4", """{"Id":"ActionTemplates-4","Name":"Deploy .NET app"}""");

    private static OctopusVariablesOptions Options(bool readProcess = true) => new()
    {
        ServerUri = new Uri("https://octopus.example.com/"),
        ApiKey = ApiKey,
        Project = "billing-api",
        ReadDeploymentProcess = readProcess,
    };

    [Fact]
    public async Task Reads_the_current_process_with_its_features_targets_and_names()
    {
        var handler = Server().Respond("/api/Spaces-1/projects/Projects-7/deploymentprocesses", Process);
        using var client = handler.Client();

        var octopus = await OctopusVariables.LoadAsync(Options(), client, TestContext.Current.CancellationToken);

        var process = Assert.IsType<OctopusDeploymentProcess>(octopus.Process);
        Assert.Equal("current deployment process", process.Description);
        Assert.Equal(2, process.Actions.Count);
        var deploy = process.Actions[0];
        Assert.Equal("Deploy web", deploy.StepName);
        Assert.Equal(["web", "api"], deploy.Roles);
        Assert.Equal(["Production"], deploy.Environments);
        Assert.Equal(["Environments-9"], deploy.ExcludedEnvironments);
        Assert.Equal(["Default"], deploy.Channels);
        Assert.Equal(["appsettings.json", "config/*.json"], deploy.StructuredConfigurationTargets);
        Assert.Equal(["web.template"], deploy.SubstituteInFilesTargets);
        Assert.True(deploy.ConfigurationTransforms);
        Assert.False(deploy.ConfigurationVariables);
        Assert.Equal("Deploy .NET app", deploy.StepTemplate);
        Assert.Equal("hello", deploy.Parameters["Greeting"]);
        Assert.Null(deploy.Parameters["ApiToken"]);
        Assert.DoesNotContain("Octopus.Action.Template.Version", deploy.Parameters.Keys);

        var notify = process.Actions[1];
        Assert.True(notify.IsDisabled);
        Assert.Empty(notify.StructuredConfigurationTargets);

        Assert.Contains("Step Deploy web of Octopus project Billing API runs XML configuration transforms on .config files, which are not traced.", octopus.Warnings);
        Assert.All(handler.Requests, r => Assert.Equal(ApiKey, Assert.Single(r.Headers.GetValues("X-Octopus-ApiKey"))));
    }

    [Fact]
    public async Task Does_not_read_the_process_unless_asked()
    {
        var handler = Server();
        using var client = handler.Client();

        var octopus = await OctopusVariables.LoadAsync(Options(readProcess: false), client, TestContext.Current.CancellationToken);

        Assert.Null(octopus.Process);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.Contains("deploymentprocesses", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Warns_and_links_by_name_when_the_process_cannot_be_read(HttpStatusCode status)
    {
        var handler = Server().Respond("/api/Spaces-1/projects/Projects-7/deploymentprocesses", """{"ErrorMessage":"no"}""", status);
        using var client = handler.Client();

        var octopus = await OctopusVariables.LoadAsync(Options(), client, TestContext.Current.CancellationToken);

        Assert.Null(octopus.Process);
        Assert.Equal(2, octopus.Entries.Count);
        var warning = Assert.Single(octopus.Warnings);
        Assert.Contains($"(HTTP {(int)status})", warning, StringComparison.Ordinal);
        Assert.Contains("ProcessView", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keeps_a_step_template_id_it_cannot_name()
    {
        var handler = Server()
            .Respond("/api/Spaces-1/projects/Projects-7/deploymentprocesses", Process)
            .Respond("/api/Spaces-1/actiontemplates/ActionTemplates-4", "{}", HttpStatusCode.Forbidden);
        using var client = handler.Client();

        var octopus = await OctopusVariables.LoadAsync(Options(), client, TestContext.Current.CancellationToken);

        Assert.Equal("ActionTemplates-4", octopus.Process!.Actions[0].StepTemplate);
    }

    [Theory]
    [InlineData(null, "refs%2Fheads%2Fdevelop", "refs/heads/develop")]
    [InlineData("main", "refs%2Fheads%2Fmain", "refs/heads/main")]
    [InlineData("refs/tags/v1.4.0", "refs%2Ftags%2Fv1.4.0", "refs/tags/v1.4.0")]
    public async Task Reads_a_version_controlled_process_at_a_Git_reference(string? gitRef, string escaped, string shown)
    {
        var handler = Server(VersionControlledProjects).Respond($"/api/Spaces-1/projects/Projects-7/{escaped}/deploymentprocesses", Process);
        using var client = handler.Client();
        var options = Options();
        options.GitRef = gitRef;

        var octopus = await OctopusVariables.LoadAsync(options, client, TestContext.Current.CancellationToken);

        Assert.Equal($"deployment process at {shown}", octopus.Process!.Description);
        Assert.Equal(2, octopus.Process.Actions.Count);
    }

    [Fact]
    public async Task Reads_the_variables_and_process_a_release_was_created_with()
    {
        var handler = Server()
            .Respond("/api/Spaces-1/projects/Projects-7/releases/1.4.0", """
                {"Version":"1.4.0","ProjectDeploymentProcessSnapshotId":"deploymentprocess-Projects-7-s-2-ABC",
                 "ProjectVariableSetSnapshotId":"variableset-Projects-7-s-5-DEF",
                 "LibraryVariableSetSnapshotIds":["variableset-LibraryVariableSets-3-s-1-GHI","odd-snapshot"]}
                """)
            .Respond("/api/Spaces-1/variables/variableset-Projects-7-s-5-DEF", """{"Variables":[{"Name":"Db:Timeout","Value":"25","Type":"String","Scope":{}}]}""")
            .Respond("/api/Spaces-1/variables/variableset-LibraryVariableSets-3-s-1-GHI", """{"Variables":[{"Name":"Region","Value":"us","Type":"String","Scope":{}}]}""")
            .Respond("/api/Spaces-1/variables/odd-snapshot", """{"Variables":[{"Name":"Odd","Value":"1","Type":"String","Scope":{}}]}""")
            .Respond("/api/Spaces-1/deploymentprocesses/deploymentprocess-Projects-7-s-2-ABC", Process);
        using var client = handler.Client();
        var options = Options(readProcess: false);
        options.Release = " 1.4.0 ";

        var octopus = await OctopusVariables.LoadAsync(options, client, TestContext.Current.CancellationToken);

        Assert.Equal("25", Assert.Single(octopus.Find("Db:Timeout")).Value);
        var region = Assert.Single(octopus.Find("Region"));
        Assert.Equal("us", region.Value);
        Assert.Equal("library variable set Shared", region.SetName);
        Assert.Equal("library variable set odd-snapshot", Assert.Single(octopus.Find("Odd")).SetName);
        Assert.Equal("deployment process of release 1.4.0", octopus.Process!.Description);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath == "/api/Spaces-1/variables/variableset-Projects-7");
    }

    [Fact]
    public async Task Reports_a_missing_release()
    {
        using var client = Server().Client();
        var options = Options();
        options.Release = "9.9.9";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => OctopusVariables.LoadAsync(options, client, TestContext.Current.CancellationToken));

        Assert.Equal("Octopus release 9.9.9 of project Billing API was not found, or the API key cannot see it.", ex.Message);
    }

    [Fact]
    public async Task Warns_when_a_release_names_no_process_snapshot()
    {
        using var client = Server().Respond("/api/Spaces-1/projects/Projects-7/releases/1.0", """{"Version":"1.0"}""").Client();
        var options = Options();
        options.Release = "1.0";

        var octopus = await OctopusVariables.LoadAsync(options, client, TestContext.Current.CancellationToken);

        Assert.Null(octopus.Process);
        Assert.Contains(octopus.Warnings, w => w.Contains("names no deployment process snapshot", StringComparison.Ordinal));
        Assert.Equal("30", Assert.Single(octopus.Find("Db:Timeout")).Value);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"Steps":"x"}""")]
    [InlineData("""{"Steps":[{"Actions":[{"Properties":{"Octopus.Action.EnabledFeatures":7,"Octopus.Action.TargetRoles":[]}}]}]}""")]
    public async Task Survives_an_odd_process(string body)
    {
        using var client = Server().Respond("/api/Spaces-1/projects/Projects-7/deploymentprocesses", body).Client();

        var octopus = await OctopusVariables.LoadAsync(Options(), client, TestContext.Current.CancellationToken);

        Assert.NotNull(octopus.Process);
        Assert.All(octopus.Process.Actions, a => Assert.Empty(a.StructuredConfigurationTargets));
    }

    [Fact]
    public async Task Links_through_the_loaded_process_in_a_trace()
    {
        using var client = Server().Respond("/api/Spaces-1/projects/Projects-7/deploymentprocesses", Process).Client();
        var octopus = await OctopusVariables.LoadAsync(Options(), client, TestContext.Current.CancellationToken);
        var resolver = new OctopusResolver(octopus, new OctopusTraceOptions { Environment = "Production" });

        var chain = resolver.Resolve(new ProvenanceStep
        {
            Kind = LayerKind.DotNetProvider,
            Source = "appsettings.json",
            Value = "30",
            ConfigurationKey = "Db:Timeout",
            Reference = new FileRef("/app/appsettings.json"),
        }, new TraceDiagnostics());

        var step = Assert.Single(chain);
        Assert.False(step.IsInferred);
        Assert.Contains("in step Deploy web (step template Deploy .NET app, target appsettings.json)", step.Note, StringComparison.Ordinal);
    }
}
