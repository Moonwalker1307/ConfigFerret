using System.Text.Json;
using System.Text.Json.Serialization;
using ConfigFerret.Desktop.Model;

namespace ConfigFerret.Desktop.Services;

/// <summary>
/// Loads and saves the <see cref="Workspace"/> as JSON in the user's application data folder. The file holds no
/// credentials: those live in the operating system's credential store or in environment variables.
/// </summary>
public sealed class WorkspaceStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Creates a store that reads and writes <paramref name="path"/>.</summary>
    public WorkspaceStore(string path) => Path = path;

    /// <summary>The workspace file.</summary>
    public string Path { get; }

    /// <summary>The default file: <c>ConfigFerret/workspace.json</c> in the user's application data folder.</summary>
    public static string DefaultPath =>
        System.IO.Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "ConfigFerret", "workspace.json");

    /// <summary>Reads the workspace, or returns an empty one when the file does not exist yet.</summary>
    /// <exception cref="JsonException">The file is not a valid workspace.</exception>
    public Workspace Load()
    {
        if (!File.Exists(Path))
        {
            return new Workspace();
        }

        using var stream = File.OpenRead(Path);
        return Sanitize(JsonSerializer.Deserialize<Workspace>(stream, JsonOptions));
    }

    /// <summary>
    /// A file can say <c>null</c> for any member, and the app reads the members without checking. Returns a workspace
    /// with every member set, dropping what is null.
    /// </summary>
    private static Workspace Sanitize(Workspace? loaded)
    {
        if (loaded is null)
        {
            return new Workspace();
        }

        var workspace = new Workspace
        {
            Version = loaded.Version,
            GitHubApiUrl = loaded.GitHubApiUrl ?? string.Empty,
            OctopusServer = loaded.OctopusServer ?? string.Empty,
            OctopusSpace = loaded.OctopusSpace ?? string.Empty,
            DarkTheme = loaded.DarkTheme,
            SidebarHidden = loaded.SidebarHidden,
            RecentFolders = (loaded.RecentFolders ?? []).Where(f => f is not null).ToList(),
        };
        foreach (var (name, choices) in loaded.Repositories ?? [])
        {
            if (name is not null && choices is not null)
            {
                choices.Environment ??= new AnalysisChoices().Environment;
                workspace.Repositories[name] = choices;
            }
        }

        return workspace;
    }

    /// <summary>Writes the workspace, replacing the file in one step so a crash never leaves half a file.</summary>
    public void Save(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temp = Path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            // Paths and repository names are not secret, but they are nobody else's business either. The mode is set
            // when the file is created, so it is never readable by others, even for a moment.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var writer = new StreamWriter(temp, new System.Text.UTF8Encoding(false), options))
        {
            writer.Write(Serialize(workspace));
        }

        if (!OperatingSystem.IsWindows())
        {
            // A .tmp file left by an earlier crash keeps its old mode, so set it again.
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, Path, overwrite: true);
    }

    /// <summary>The workspace as JSON.</summary>
    public static string Serialize(Workspace workspace) => JsonSerializer.Serialize(workspace, JsonOptions);
}
