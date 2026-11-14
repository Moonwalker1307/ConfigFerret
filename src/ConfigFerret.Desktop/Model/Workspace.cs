using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ConfigFerret.Desktop.Model;

/// <summary>
/// Everything the app saves between runs: the connection settings, the theme and the choices made per repository.
/// Never holds a credential; tokens and API keys live in the operating system's credential store.
/// </summary>
public sealed class Workspace
{
    /// <summary>The file format version.</summary>
    public int Version { get; set; } = 2;

    /// <summary>The GitHub API address. Empty for github.com.</summary>
    public string GitHubApiUrl { get; set; } = string.Empty;

    /// <summary>The Octopus server address. Empty when Octopus is not connected.</summary>
    public string OctopusServer { get; set; } = string.Empty;

    /// <summary>The Octopus space. Empty for the default space.</summary>
    public string OctopusSpace { get; set; } = string.Empty;

    /// <summary>The dark theme is on.</summary>
    public bool DarkTheme { get; set; } = true;

    /// <summary>The repository sidebar is hidden.</summary>
    public bool SidebarHidden { get; set; }

    /// <summary>The choices made for each repository (owner/name) or local folder (full path).</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, Services.AnalysisChoices> Repositories { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Local folders opened recently, newest first.</summary>
    public List<string> RecentFolders { get; init; } = [];
}

/// <summary>What a trace starts from.</summary>
public enum TraceMode
{
    /// <summary>The app's own configuration: appsettings files, environment variables and what deploys them.</summary>
    App,

    /// <summary>A Kubernetes container seen from outside: the environment variables and files its manifests give it.</summary>
    Container,
}

/// <summary>A named set of sources that together give the full picture of one app's configuration.</summary>
public sealed partial class Profile : ObservableObject
{
    /// <summary>A stable id, also used to name stored credentials.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The name shown in the profile list, for example "Orders API, production".</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = "New profile";

    /// <summary>What the trace starts from.</summary>
    [ObservableProperty]
    public partial TraceMode Mode { get; set; } = TraceMode.App;

    /// <summary>The sources, in the order the app's configuration stacks them (later ones win).</summary>
    public ObservableCollection<SourceSettings> Sources { get; init; } = [];

    /// <inheritdoc />
    public override string ToString() => Name;
}
