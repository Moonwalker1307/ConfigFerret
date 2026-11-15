namespace ConfigFerret.Compose;

/// <summary>
/// Several dotenv files read in order, each one winning over the ones before, the way apps layer <c>.env</c>,
/// <c>.env.local</c> and <c>.env.production</c>. A later file's <c>${VAR}</c> can refer to keys set in an earlier one.
/// </summary>
public sealed class DotEnvLayers
{
    private DotEnvLayers(List<DotEnvFile> files)
    {
        Files = files;
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in files.SelectMany(f => f.Entries))
        {
            variables[entry.Name] = entry.Value;
        }

        Variables = variables;
    }

    /// <summary>The files, lowest priority first.</summary>
    public IReadOnlyList<DotEnvFile> Files { get; }

    /// <summary>Each key's value: the last file, and in it the last line, that sets it wins.</summary>
    public IReadOnlyDictionary<string, string> Variables { get; }

    /// <summary>Problems found while reading the files.</summary>
    public IReadOnlyList<string> Warnings => [.. Files.SelectMany(f => f.Warnings)];

    /// <summary>Every entry that sets <paramref name="name"/>, lowest priority first. The last one wins.</summary>
    public IReadOnlyList<DotEnvEntry> History(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Files.SelectMany(f => f.Entries).Where(e => string.Equals(e.Name, name, StringComparison.Ordinal)).ToList();
    }

    /// <summary>Reads the dotenv files at <paramref name="paths"/>, lowest priority first.</summary>
    /// <param name="paths">The files, lowest priority first.</param>
    /// <param name="variables">Variables such as the shell's, which <c>${VAR}</c> references read first. The process environment is never read.</param>
    /// <param name="skipMissing">True to skip a file that does not exist instead of failing.</param>
    /// <exception cref="FormatException">A file is not valid dotenv syntax.</exception>
    /// <exception cref="IOException">A file could not be read.</exception>
    public static DotEnvLayers Load(IEnumerable<string> paths, IReadOnlyDictionary<string, string?>? variables = null, bool skipMissing = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var full = paths.Select(Path.GetFullPath).Where(p => !skipMissing || File.Exists(p)).ToList();
        return Build(full.Select(p => new DotEnvSourceText(Path.GetFileName(p), p, () => File.ReadAllText(p))), variables);
    }

    /// <summary>Parses dotenv texts, lowest priority first.</summary>
    /// <param name="files">Each file's name as shown in reports, and its contents.</param>
    /// <param name="variables">Variables such as the shell's, which <c>${VAR}</c> references read first.</param>
    /// <exception cref="FormatException">A file is not valid dotenv syntax.</exception>
    public static DotEnvLayers Parse(IEnumerable<(string Name, string Text)> files, IReadOnlyDictionary<string, string?>? variables = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        return Build(files.Select(f => new DotEnvSourceText(f.Name, null, () => f.Text)), variables);
    }

    /// <summary>
    /// The conventional dotenv files in <paramref name="directory"/> that exist, lowest priority first:
    /// <c>.env</c>, <c>.env.local</c>, then for an environment such as <c>production</c>, <c>.env.production</c> and
    /// <c>.env.production.local</c>.
    /// </summary>
    public static IReadOnlyList<string> Conventional(string directory, string? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var names = new List<string> { ".env", ".env.local" };
        if (!string.IsNullOrWhiteSpace(environment))
        {
            names.Add($".env.{environment.Trim()}");
            names.Add($".env.{environment.Trim()}.local");
        }

        return names.Select(n => Path.Join(Path.GetFullPath(directory), n)).Where(File.Exists).ToList();
    }

    // Each file sees the variables passed in first, then its own earlier keys, then what the earlier files set.
    internal static DotEnvLayers Build(IEnumerable<DotEnvSourceText> files, IReadOnlyDictionary<string, string?>? variables) =>
        Build(files, DotEnvFile.VariablesLookup(variables));

    internal static DotEnvLayers Build(IEnumerable<DotEnvSourceText> files, Func<string, LookupResult?> shell)
    {
        var earlier = new Dictionary<string, DotEnvEntry>(StringComparer.Ordinal);
        var parsed = new List<DotEnvFile>();
        foreach (var (name, path, read) in files)
        {
            var file = DotEnvFile.Parse(read(), name, path, shell, fallback: v => earlier.TryGetValue(v, out var e) ? new LookupResult(e.Value, e) : null);
            parsed.Add(file);
            foreach (var entry in file.Entries)
            {
                earlier[entry.Name] = entry;
            }
        }

        return new DotEnvLayers(parsed);
    }
}

/// <summary>A dotenv file to parse: its name in reports, its full path when on disk, and how to read it.</summary>
internal sealed record DotEnvSourceText(string Name, string? Path, Func<string> Read);
