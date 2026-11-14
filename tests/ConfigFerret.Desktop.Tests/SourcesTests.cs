using System.Text.Json;
using ConfigFerret.Desktop.Model;
using ConfigFerret.Desktop.Services;

namespace ConfigFerret.Desktop.Tests;

public class SourcesTests
{
    [Theory]
    [InlineData(SourceKind.AppSettings, "App settings", typeof(AppSettingsSource))]
    [InlineData(SourceKind.EnvironmentVariables, "Environment variables", typeof(EnvironmentVariablesSource))]
    [InlineData(SourceKind.Kubernetes, "Kubernetes manifests", typeof(KubernetesSource))]
    [InlineData(SourceKind.Helm, "Helm chart", typeof(HelmSource))]
    [InlineData(SourceKind.Kustomize, "Kustomize", typeof(KustomizeSource))]
    [InlineData(SourceKind.GitHub, "GitHub Actions", typeof(GitHubSource))]
    [InlineData(SourceKind.Octopus, "Octopus Deploy", typeof(OctopusSource))]
    public void The_catalog_creates_a_named_source_of_each_kind(SourceKind kind, string name, Type type)
    {
        var source = SourceCatalog.Create(kind);

        Assert.IsType(type, source);
        Assert.Equal(kind, source.Kind);
        Assert.Equal(name, SourceCatalog.DisplayName(kind));
        Assert.Equal(name, source.Name);
        Assert.Equal(name, source.KindName);
        Assert.True(source.Enabled);
        Assert.Null(source.Problem);
        Assert.NotEqual(Guid.Empty, source.Id);
    }

    [Fact]
    public void The_catalog_lists_every_kind()
    {
        Assert.Equal(Enum.GetValues<SourceKind>(), SourceCatalog.Kinds);
    }

    [Fact]
    public void Unknown_kinds_cannot_be_created_and_are_named_by_their_value()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SourceCatalog.Create((SourceKind)99));
        Assert.Equal("99", SourceCatalog.DisplayName((SourceKind)99));
    }

    [Fact]
    public void Every_new_source_has_its_own_id()
    {
        Assert.NotEqual(SourceCatalog.Create(SourceKind.Helm).Id, SourceCatalog.Create(SourceKind.Helm).Id);
    }

    [Fact]
    public void New_app_settings_sources_have_production_defaults()
    {
        var source = Assert.IsType<AppSettingsSource>(SourceCatalog.Create(SourceKind.AppSettings));

        Assert.Equal("Production", source.EnvironmentName);
        Assert.True(source.UseUserSecrets);
        Assert.Empty(source.ExtraFiles);
        Assert.Empty(source.EnvironmentPrefixes);
    }

    [Fact]
    public void New_connection_sources_read_the_usual_environment_variable()
    {
        var github = Assert.IsType<GitHubSource>(SourceCatalog.Create(SourceKind.GitHub));
        var octopus = Assert.IsType<OctopusSource>(SourceCatalog.Create(SourceKind.Octopus));

        Assert.Equal("GITHUB_TOKEN", github.CredentialVariable);
        Assert.Equal("token", github.CredentialName);
        Assert.Equal(CredentialSource.Stored, github.CredentialSource);
        Assert.Equal("OCTOPUS_API_KEY", octopus.CredentialVariable);
        Assert.Equal("API key", octopus.CredentialName);
        Assert.True(octopus.IncludeLibraryVariableSets);
    }

    [Fact]
    public void A_connection_stores_its_credential_under_its_kind_and_id_unless_shared()
    {
        var source = Assert.IsType<GitHubSource>(SourceCatalog.Create(SourceKind.GitHub));

        Assert.Equal($"GitHub/{source.Id:N}", source.CredentialAccount);

        source.CredentialAccountName = "github-account";

        Assert.Equal("github-account", source.CredentialAccount);
    }

    [Fact]
    public void Multi_line_text_boxes_split_into_trimmed_non_empty_lines()
    {
        var source = new KubernetesSource { PathsText = "  deploy/a.yaml \r\n\r\n\tdeploy/b.yaml\n   \n" };

        Assert.Equal(["deploy/a.yaml", "deploy/b.yaml"], source.Paths);
        Assert.Equal("deploy/a.yaml" + Environment.NewLine + "deploy/b.yaml", source.PathsText);
    }

    [Fact]
    public void Multi_line_text_boxes_accept_null_and_raise_a_change()
    {
        var source = new KubernetesSource { Paths = ["x"] };
        var raised = new List<string?>();
        source.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        source.PathsText = null!;

        Assert.Empty(source.Paths);
        Assert.Equal([nameof(KubernetesSource.PathsText)], raised);
    }

    [Fact]
    public void Every_list_has_a_text_box_form()
    {
        var app = new AppSettingsSource { ExtraFilesText = "a.json\nb.json", EnvironmentPrefixesText = "ORDERS_\nAPP_" };
        var helm = new HelmSource { ValuesFilesText = "v1.yaml\nv2.yaml", SetValuesText = "a=1\nb=2" };
        var github = new GitHubSource { WorkflowsText = "ci.yml\ndeploy.yml" };
        var octopus = new OctopusSource { RolesText = "web\napi", TenantTagsText = "Region/West\nRegion/East" };

        Assert.Equal(["a.json", "b.json"], app.ExtraFiles);
        Assert.Equal(["ORDERS_", "APP_"], app.EnvironmentPrefixes);
        Assert.Equal(["v1.yaml", "v2.yaml"], helm.ValuesFiles);
        Assert.Equal(["a=1", "b=2"], helm.SetValues);
        Assert.Equal(["ci.yml", "deploy.yml"], github.Workflows);
        Assert.Equal(["web", "api"], octopus.Roles);
        Assert.Equal(["Region/West", "Region/East"], octopus.TenantTags);

        var nl = Environment.NewLine;
        Assert.Equal("a.json" + nl + "b.json", app.ExtraFilesText);
        Assert.Equal("ORDERS_" + nl + "APP_", app.EnvironmentPrefixesText);
        Assert.Equal("v1.yaml" + nl + "v2.yaml", helm.ValuesFilesText);
        Assert.Equal("a=1" + nl + "b=2", helm.SetValuesText);
        Assert.Equal("ci.yml" + nl + "deploy.yml", github.WorkflowsText);
        Assert.Equal("web" + nl + "api", octopus.RolesText);
        Assert.Equal("Region/West" + nl + "Region/East", octopus.TenantTagsText);
    }

    [Fact]
    public void Text_box_edits_raise_a_change_for_that_property()
    {
        var changed = new List<string?>();
        var app = new AppSettingsSource();
        app.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        app.ExtraFilesText = "a";
        app.EnvironmentPrefixesText = "b";
        var helm = new HelmSource();
        helm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        helm.ValuesFilesText = "a";
        helm.SetValuesText = "b";
        var github = new GitHubSource();
        github.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        github.WorkflowsText = "a";
        var octopus = new OctopusSource();
        octopus.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        octopus.RolesText = "a";
        octopus.TenantTagsText = "b";

        Assert.Equal(
            ["ExtraFilesText", "EnvironmentPrefixesText", "ValuesFilesText", "SetValuesText", "WorkflowsText", "RolesText", "TenantTagsText"],
            changed);
    }

    [Fact]
    public void Sources_round_trip_through_json_with_their_type_and_without_runtime_state()
    {
        var profile = new Profile { Name = "Orders", Mode = TraceMode.Container };
        profile.Sources.Add(new AppSettingsSource { Name = "app", Folder = "/src/app", EnvironmentName = "Staging", UseUserSecrets = false, ExtraFiles = ["x.json"], EnvironmentPrefixes = ["P_"] });
        profile.Sources.Add(new EnvironmentVariablesSource { File = ".env", Variables = "A=1" });
        profile.Sources.Add(new KubernetesSource { Paths = ["k8s"], Workload = "api", Container = "web" });
        profile.Sources.Add(new HelmSource { ChartFolder = "chart", ValuesFiles = ["v.yaml"], SetValues = ["a=b"] });
        profile.Sources.Add(new KustomizeSource { Folder = "overlay", AllowFilesOutsideFolder = true });
        profile.Sources.Add(new GitHubSource { Repository = "c/o", Environment = "production", ApiUrl = "https://ghe/api/v3/", Workflows = ["ci.yml"], CredentialSource = CredentialSource.EnvironmentVariable, CredentialVariable = "TOK", Problem = "boom", CredentialAccountName = "shared" });
        profile.Sources.Add(new OctopusSource { Server = "https://o/", Space = "Spaces-1", Project = "P", Environment = "Prod", Machine = "m1", Channel = "c", Step = "s", IncludeLibraryVariableSets = false, Roles = ["web"], TenantTags = ["R/W"] });

        var json = JsonSerializer.Serialize(profile.Sources, WorkspaceStore.JsonOptions);
        var back = JsonSerializer.Deserialize<List<SourceSettings>>(json, WorkspaceStore.JsonOptions)!;

        Assert.Contains("\"type\": \"github\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("problem", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shared", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Text\"", json, StringComparison.Ordinal);
        Assert.Equal(7, back.Count);
        var app = Assert.IsType<AppSettingsSource>(back[0]);
        Assert.Equal(("/src/app", "Staging", false), (app.Folder, app.EnvironmentName, app.UseUserSecrets));
        Assert.Equal(["x.json"], app.ExtraFiles);
        Assert.Equal(["P_"], app.EnvironmentPrefixes);
        var env = Assert.IsType<EnvironmentVariablesSource>(back[1]);
        Assert.Equal((".env", "A=1"), (env.File, env.Variables));
        var k8s = Assert.IsType<KubernetesSource>(back[2]);
        Assert.Equal(["k8s"], k8s.Paths);
        Assert.Equal(("api", "web"), (k8s.Workload, k8s.Container));
        var helm = Assert.IsType<HelmSource>(back[3]);
        Assert.Equal("chart", helm.ChartFolder);
        Assert.Equal(["v.yaml"], helm.ValuesFiles);
        Assert.Equal(["a=b"], helm.SetValues);
        var kustomize = Assert.IsType<KustomizeSource>(back[4]);
        Assert.Equal(("overlay", true), (kustomize.Folder, kustomize.AllowFilesOutsideFolder));
        var github = Assert.IsType<GitHubSource>(back[5]);
        Assert.Equal(("c/o", "production", "https://ghe/api/v3/"), (github.Repository, github.Environment, github.ApiUrl));
        Assert.Equal(["ci.yml"], github.Workflows);
        Assert.Equal((CredentialSource.EnvironmentVariable, "TOK"), (github.CredentialSource, github.CredentialVariable));
        Assert.Null(github.Problem);
        Assert.Equal(profile.Sources[5].Id, github.Id);
        var octopus = Assert.IsType<OctopusSource>(back[6]);
        Assert.Equal(("https://o/", "Spaces-1", "P", "Prod"), (octopus.Server, octopus.Space, octopus.Project, octopus.Environment));
        Assert.Equal(("m1", "c", "s", false), (octopus.Machine, octopus.Channel, octopus.Step, octopus.IncludeLibraryVariableSets));
        Assert.Equal(["web"], octopus.Roles);
        Assert.Equal(["R/W"], octopus.TenantTags);
    }

    [Fact]
    public void A_profile_is_named_and_starts_empty()
    {
        var profile = new Profile();

        Assert.Equal("New profile", profile.Name);
        Assert.Equal(TraceMode.App, profile.Mode);
        Assert.Empty(profile.Sources);
        Assert.NotEqual(Guid.Empty, profile.Id);
        Assert.Equal("New profile", profile.ToString());
        Assert.NotEqual(new Profile().Id, profile.Id);
    }

    [Fact]
    public void A_workspace_starts_with_the_defaults()
    {
        var workspace = new Workspace();

        Assert.Equal(2, workspace.Version);
        Assert.True(workspace.DarkTheme);
        Assert.Equal(string.Empty, workspace.GitHubApiUrl);
        Assert.Equal(string.Empty, workspace.OctopusServer);
        Assert.Equal(string.Empty, workspace.OctopusSpace);
        Assert.Empty(workspace.Repositories);
        Assert.Empty(workspace.RecentFolders);
    }

    [Fact]
    public void Repository_choices_are_matched_without_regard_to_case()
    {
        var workspace = new Workspace();
        workspace.Repositories["Contoso/Orders"] = new AnalysisChoices();

        Assert.True(workspace.Repositories.ContainsKey("contoso/orders"));
    }

    [Fact]
    public void A_loaded_workspace_still_matches_repositories_without_regard_to_case()
    {
        var workspace = JsonSerializer.Deserialize<Workspace>("""{"repositories":{"Contoso/Orders":{}}}""", WorkspaceStore.JsonOptions)!;

        Assert.True(workspace.Repositories.ContainsKey("contoso/orders"));
    }
}
