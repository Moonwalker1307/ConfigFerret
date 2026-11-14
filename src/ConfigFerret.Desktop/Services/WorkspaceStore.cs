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
        return JsonSerializer.Deserialize<Workspace>(stream, JsonOptions) ?? new Workspace();
    }

    /// <summary>Writes the workspace, replacing the file in one step so a crash never leaves half a file.</summary>
    public void Save(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, Serialize(workspace));
        if (!OperatingSystem.IsWindows())
        {
            // Paths and repository names are not secret, but they are nobody else's business either.
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, Path, overwrite: true);
    }

    /// <summary>The workspace as JSON.</summary>
    public static string Serialize(Workspace workspace) => JsonSerializer.Serialize(workspace, JsonOptions);
}
