using ConfigFerret.Kubernetes;

namespace ConfigFerret.Tool;

/// <summary>Reads input files, with <c>-</c> for standard input, and turns a missing file into a one-line error.</summary>
internal sealed class InputReader(CliHost host)
{
    public const string StandardInput = "-";

    /// <summary>
    /// The most the tool reads from a file it finds by itself: <c>appsettings.json</c>,
    /// <c>appsettings.{Environment}.json</c> and the files in a manifest folder. Files named on the command line have
    /// no limit.
    /// </summary>
    public const long MaxFoundFileBytes = 64 * 1024 * 1024;

    private bool _stdinUsed;

    public string ReadText(string path)
    {
        if (path != StandardInput)
        {
            return File.ReadAllText(RequireFile(path));
        }

        if (_stdinUsed)
        {
            throw new UsageException("Standard input (-) can be read only once.");
        }

        _stdinUsed = true;
        return host.In.ReadToEnd();
    }

    /// <summary>
    /// Loads manifests from files, folders (every <c>*.yaml</c>, <c>*.yml</c> and <c>*.json</c> below them, without
    /// following symbolic links) and standard input, in the order given. A file is named in output by its file name,
    /// and a file found in a folder by its path inside the folder.
    /// </summary>
    public KubernetesManifests ReadManifests(IReadOnlyList<string> paths)
    {
        KubernetesManifests? manifests = null;
        foreach (var (file, name) in paths.SelectMany(ExpandFolder))
        {
            var text = ReadText(file);
            if (manifests is null)
            {
                manifests = KubernetesManifests.Parse(text, name);
            }
            else
            {
                manifests.Add(text, name);
            }
        }

        return manifests ?? throw new UsageException("No manifest files found in " + string.Join(", ", paths) + ".");
    }

    // A folder stands for every manifest file below it, in a stable order, each named by its path inside the folder so
    // that files of the same name in different folders can be told apart. Only the system's own separator becomes "/":
    // on Linux and macOS a "\" is part of a file name. Links below it are skipped: a link to a parent folder never
    // ends, and one out of the folder reads other files.
    private static IEnumerable<(string File, string Name)> ExpandFolder(string path) =>
        path != StandardInput && Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .Select(f => (RequireFoundFileSize(f), Path.GetRelativePath(path, f).Replace(Path.DirectorySeparatorChar, '/')))
            : [(path, path == StandardInput ? "stdin" : Path.GetFileName(path))];

    /// <summary>Refuses a file the tool found by itself that is bigger than <see cref="MaxFoundFileBytes"/>.</summary>
    public static string RequireFoundFileSize(string path)
    {
        // The length of the open file, not FileInfo.Length: for a link that is the size of the link itself.
        using var handle = File.OpenHandle(path);
        if (RandomAccess.GetLength(handle) > MaxFoundFileBytes)
        {
            throw new UsageException($"{path} is bigger than {MaxFoundFileBytes / (1024 * 1024)} MB, the most the tool reads from a file it finds by itself.");
        }

        return path;
    }

    public static string RequireFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new UsageException(Directory.Exists(path) ? $"{path} is a folder, not a file." : $"File not found: {path}");
        }

        return path;
    }

    public static string RequireDirectory(string path, string what)
    {
        if (!Directory.Exists(path))
        {
            throw new UsageException(File.Exists(path) ? $"{what} must be a folder, but {path} is a file." : $"{what} not found: {path}");
        }

        return path;
    }
}
