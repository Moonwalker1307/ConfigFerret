using ConfigFerret.Kubernetes;

namespace ConfigFerret.Tool;

/// <summary>Reads input files, with <c>-</c> for standard input, and turns a missing file into a one-line error.</summary>
internal sealed class InputReader(CliHost host)
{
    public const string StandardInput = "-";

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
    /// Loads manifests from files, folders (every <c>*.yaml</c>, <c>*.yml</c> and <c>*.json</c> below them) and
    /// standard input, in the order given.
    /// </summary>
    public KubernetesManifests ReadManifests(IReadOnlyList<string> paths)
    {
        KubernetesManifests? manifests = null;
        foreach (var path in paths)
        {
            IEnumerable<string> files = path != StandardInput && Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.Ordinal)
                : [path];
            foreach (var file in files)
            {
                var text = ReadText(file);
                var name = file == StandardInput ? "stdin" : Path.GetFileName(file);
                if (manifests is null)
                {
                    manifests = KubernetesManifests.Parse(text, name);
                }
                else
                {
                    manifests.Add(text, name);
                }
            }
        }

        return manifests ?? throw new UsageException("No manifest files found in " + string.Join(", ", paths) + ".");
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
