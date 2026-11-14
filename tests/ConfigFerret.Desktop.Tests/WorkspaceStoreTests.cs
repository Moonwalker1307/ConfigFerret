using System.Text.Json;
using ConfigFerret.Desktop.Model;
using ConfigFerret.Desktop.Services;

namespace ConfigFerret.Desktop.Tests;

public class WorkspaceStoreTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("configferret-workspace-");

    public void Dispose()
    {
        _folder.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private string FilePath => Path.Join(_folder.FullName, "nested", "workspace.json");

    [Fact]
    public void Loads_an_empty_workspace_when_there_is_no_file()
    {
        var workspace = new WorkspaceStore(FilePath).Load();

        Assert.Equal(string.Empty, workspace.GitHubApiUrl);
        Assert.True(workspace.DarkTheme);
        Assert.Empty(workspace.Repositories);
        Assert.Empty(workspace.RecentFolders);
    }

    [Fact]
    public void Round_trips_the_workspace()
    {
        var store = new WorkspaceStore(FilePath);
        var workspace = new Workspace
        {
            GitHubApiUrl = "https://github.example.test/api/v3/",
            OctopusServer = "https://octopus.example.test/",
            OctopusSpace = "Spaces-2",
            DarkTheme = false,
            RecentFolders = ["/work/orders", "/work/billing"],
        };
        workspace.Repositories["contoso/orders"] = new AnalysisChoices
        {
            App = "src/Orders.Api",
            Environment = "Staging",
            HelmChart = "deploy/chart",
            HelmValues = "deploy/values-eu.yaml",
            Kustomization = "k8s/overlays/prod",
            GitHubEnvironment = "production",
            OctopusProject = "Orders",
            OctopusEnvironment = "Production",
        };

        store.Save(workspace);
        var loaded = new WorkspaceStore(FilePath).Load();

        Assert.Equal(2, loaded.Version);
        Assert.Equal("https://github.example.test/api/v3/", loaded.GitHubApiUrl);
        Assert.Equal("https://octopus.example.test/", loaded.OctopusServer);
        Assert.Equal("Spaces-2", loaded.OctopusSpace);
        Assert.False(loaded.DarkTheme);
        Assert.Equal(["/work/orders", "/work/billing"], loaded.RecentFolders);
        var choices = Assert.Single(loaded.Repositories).Value;
        Assert.Equal("src/Orders.Api", choices.App);
        Assert.Equal("Staging", choices.Environment);
        Assert.Equal("deploy/chart", choices.HelmChart);
        Assert.Equal("deploy/values-eu.yaml", choices.HelmValues);
        Assert.Equal("k8s/overlays/prod", choices.Kustomization);
        Assert.Equal("production", choices.GitHubEnvironment);
        Assert.Equal("Orders", choices.OctopusProject);
        Assert.Equal("Production", choices.OctopusEnvironment);
    }

    [Fact]
    public void Looks_repositories_up_without_regard_to_case_after_a_load()
    {
        var store = new WorkspaceStore(FilePath);
        var workspace = new Workspace();
        workspace.Repositories["Contoso/Orders"] = new AnalysisChoices { App = "src/Orders.Api" };
        store.Save(workspace);

        var loaded = store.Load();

        Assert.True(loaded.Repositories.TryGetValue("contoso/orders", out var choices));
        Assert.Equal("src/Orders.Api", choices.App);
        Assert.Same(choices, loaded.Repositories["CONTOSO/ORDERS"]);
    }

    [Fact]
    public void Keeps_one_entry_for_the_same_repository_spelled_two_ways_after_a_load()
    {
        var store = new WorkspaceStore(FilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """{"repositories":{"contoso/orders":{"app":"a"}}}""");

        var loaded = store.Load();
        loaded.Repositories["Contoso/Orders"] = new AnalysisChoices { App = "b" };

        Assert.Single(loaded.Repositories);
        Assert.Equal("b", loaded.Repositories["contoso/orders"].App);
    }

    [Fact]
    public void Writes_camel_case_names_and_enum_names()
    {
        var json = WorkspaceStore.Serialize(new Workspace { OctopusServer = "https://octopus.example.test/" });

        Assert.Contains("\"octopusServer\"", json, StringComparison.Ordinal);
        Assert.Contains("\"darkTheme\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Has_no_property_that_could_hold_a_credential()
    {
        var workspace = new Workspace();
        workspace.Repositories["contoso/orders"] = new AnalysisChoices { App = "src/Orders.Api", OctopusProject = "Orders" };
        var json = WorkspaceStore.Serialize(workspace);

        using var document = JsonDocument.Parse(json);

        Assert.DoesNotContain(PropertyNames(document.RootElement), name =>
            name.Contains("token", StringComparison.OrdinalIgnoreCase)
            || name.Contains("apikey", StringComparison.OrdinalIgnoreCase)
            || name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_saved_connection_source_holds_no_credential_even_when_one_was_set()
    {
        var github = new GitHubSource { Repository = "contoso/orders", CredentialAccountName = "github" };
        var octopus = new OctopusSource { Server = "https://octopus.example.test/", Project = "Orders", CredentialAccountName = "octopus" };

        foreach (var source in new SourceSettings[] { github, octopus })
        {
            var json = JsonSerializer.Serialize(source, WorkspaceStore.JsonOptions);
            using var document = JsonDocument.Parse(json);
            var names = PropertyNames(document.RootElement).ToList();

            Assert.DoesNotContain(names, n => n.Equals("token", StringComparison.OrdinalIgnoreCase) || n.Equals("apikey", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.Contains("credentialAccount", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Throws_a_json_exception_for_a_file_that_is_not_a_workspace()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{ not json");

        Assert.ThrowsAny<JsonException>(() => new WorkspaceStore(FilePath).Load());
    }

    [Fact]
    public void Replaces_the_file_without_leaving_a_temporary_one()
    {
        var store = new WorkspaceStore(FilePath);
        store.Save(new Workspace { OctopusSpace = "one" });
        store.Save(new Workspace { OctopusSpace = "two" });

        Assert.Equal("two", store.Load().OctopusSpace);
        Assert.Equal([FilePath], Directory.EnumerateFiles(Path.GetDirectoryName(FilePath)!));
    }

    [Fact]
    public void Saves_the_file_readable_by_its_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new WorkspaceStore(FilePath);
        store.Save(new Workspace());
        store.Save(new Workspace { DarkTheme = false });

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
    }

    [Fact]
    public void The_default_path_is_in_the_application_data_folder()
    {
        Assert.EndsWith(Path.Join("ConfigFerret", "workspace.json"), WorkspaceStore.DefaultPath, StringComparison.Ordinal);
    }

    private static IEnumerable<string> PropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var inner in PropertyNames(property.Value))
                    {
                        yield return inner;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var inner in PropertyNames(item))
                    {
                        yield return inner;
                    }
                }

                break;
        }
    }
}
