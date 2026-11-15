namespace ConfigFerret.Web.Services;

/// <summary>Settings for the web app, from the <c>ConfigFerret</c> configuration section (for example <c>ConfigFerret__DataFolder</c>).</summary>
public sealed class WebAppOptions
{
    /// <summary>The configuration section.</summary>
    public const string Section = "ConfigFerret";

    /// <summary>
    /// Where the workspace file (connection addresses, theme and the choices made per repository) is saved. It never
    /// holds a credential. Empty for <c>ConfigFerret</c> in the user's application data folder. The image uses /data.
    /// </summary>
    public string DataFolder { get; set; } = string.Empty;

    /// <summary>
    /// The folder whose subfolders can be opened with "Open a folder", such as a volume of clones mounted at /repos.
    /// Empty turns the button off. Nothing outside this folder can be opened.
    /// </summary>
    public string Folders { get; set; } = string.Empty;

    /// <summary>The workspace file.</summary>
    public string WorkspacePath => DataFolder.Length == 0
        ? Desktop.Services.WorkspaceStore.DefaultPath
        : Path.Join(DataFolder, "workspace.json");
}
