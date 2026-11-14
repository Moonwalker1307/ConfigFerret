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
    public void Saving_needs_a_workspace()
    {
        Assert.Throws<ArgumentNullException>(() => new WorkspaceStore(FilePath).Save(null!));
    }

    [Fact]
    public void Knows_its_path_and_the_default_one()
    {
        Assert.Equal(FilePath, new WorkspaceStore(FilePath).Path);
        Assert.EndsWith(Path.Join("ConfigFerret", "workspace.json"), WorkspaceStore.DefaultPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Loading_a_file_that_says_null_gives_an_empty_workspace()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "null");

        Assert.True(new WorkspaceStore(FilePath).Load().DarkTheme);
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
    public void Replaces_a_stale_temporary_file_and_keeps_its_content_out_of_the_result()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var stale = FilePath + ".tmp";
        File.WriteAllText(stale, "{ half a file from a crash, and a much longer line so truncation shows ........................................ }");
        File.SetUnixFileMode(stale, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var store = new WorkspaceStore(FilePath);

        store.Save(new Workspace { OctopusSpace = "fresh" });

        Assert.False(File.Exists(stale));
        Assert.Equal("fresh", store.Load().OctopusSpace);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
    }

    [Fact]
    public void Writes_utf8_without_a_byte_order_mark()
    {
        new WorkspaceStore(FilePath).Save(new Workspace());

        var bytes = File.ReadAllBytes(FilePath);

        Assert.Equal((byte)'{', bytes[0]);
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

    [Theory]
    [InlineData("""{"repositories":null,"recentFolders":null}""")]
    [InlineData("""{"gitHubApiUrl":null,"octopusServer":null,"octopusSpace":null}""")]
    [InlineData("""{"repositories":{"a/b":null,"c/d":{"environment":null}},"recentFolders":[null,"/work/x"]}""")]
    public void Nulls_in_the_file_never_reach_the_app(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, json);

        var workspace = new WorkspaceStore(FilePath).Load();

        Assert.NotNull(workspace.Repositories);
        Assert.NotNull(workspace.RecentFolders);
        Assert.NotNull(workspace.GitHubApiUrl);
        Assert.NotNull(workspace.OctopusServer);
        Assert.NotNull(workspace.OctopusSpace);
        Assert.All(workspace.Repositories, r => Assert.NotNull(r.Value?.Environment));
        Assert.DoesNotContain(null!, workspace.RecentFolders);
        Assert.DoesNotContain("a/b", workspace.Repositories.Keys);
    }

    [Fact]
    public void Every_setting_survives_a_save_and_load()
    {
        // Load rebuilds the workspace to drop nulls, so a new setting must be copied there too.
        var dir = Directory.CreateTempSubdirectory("configferret-ws-");
        try
        {
            var store = new WorkspaceStore(Path.Join(dir.FullName, "workspace.json"));
            var saved = new Workspace();
            foreach (var property in typeof(Workspace).GetProperties().Where(p => p.CanWrite && p.SetMethod!.IsPublic))
            {
                if (property.PropertyType == typeof(bool))
                {
                    property.SetValue(saved, !(bool)property.GetValue(saved)!);
                }
                else if (property.PropertyType == typeof(string))
                {
                    property.SetValue(saved, "x-" + property.Name);
                }
                else if (property.PropertyType == typeof(int))
                {
                    property.SetValue(saved, (int)property.GetValue(saved)! + 1);
                }
            }

            store.Save(saved);
            var loaded = store.Load();

            foreach (var property in typeof(Workspace).GetProperties().Where(p => p.PropertyType == typeof(bool) || p.PropertyType == typeof(string) || p.PropertyType == typeof(int)))
            {
                Assert.True(Equals(property.GetValue(saved), property.GetValue(loaded)), property.Name);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
