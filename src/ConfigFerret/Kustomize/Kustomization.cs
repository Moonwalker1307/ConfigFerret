using System.Globalization;
using ConfigFerret.Internal;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ConfigFerret.Kustomize;

/// <summary>
/// The ConfigMap and Secret generators of a kustomization and all its bases, with the history of every key
/// across overlays (<c>behavior: create</c>, <c>merge</c> and <c>replace</c>), and the final generated names
/// after <c>namePrefix</c> and <c>nameSuffix</c>.
/// </summary>
/// <remarks>
/// Patches that edit ConfigMaps or env entries directly are not traced yet. Generated names get a content hash
/// suffix from kustomize; names are matched with or without it.
/// </remarks>
public sealed class Kustomization
{
    private static readonly string[] FileNames = ["kustomization.yaml", "kustomization.yml", "Kustomization"];

    private Kustomization(List<GeneratedData> generated)
    {
        ConfigMaps = generated.Where(g => !g.IsSecret).ToDictionary(g => g.Name, StringComparer.Ordinal);
        Secrets = generated.Where(g => g.IsSecret).ToDictionary(g => g.Name, StringComparer.Ordinal);
    }

    /// <summary>The generated ConfigMaps, by final name (without the hash suffix).</summary>
    public IReadOnlyDictionary<string, GeneratedData> ConfigMaps { get; }

    /// <summary>
    /// The generated Secrets, by final name (without the hash suffix).
    /// A Secret can share its name with a ConfigMap, as in Kubernetes.
    /// </summary>
    public IReadOnlyDictionary<string, GeneratedData> Secrets { get; }

    /// <summary>Loads the kustomization in <paramref name="directory"/> and all its bases.</summary>
    /// <param name="directory">The folder that holds the kustomization file.</param>
    /// <param name="allowFilesOutsideFolder">
    /// Like kustomize, a generator's env files must sit in or below the folder of the kustomization that names them,
    /// after following symbolic links. Pass true to read them from anywhere, as
    /// <c>kustomize build --load-restrictor LoadRestrictionsNone</c> does. Bases and resources may be anywhere either way.
    /// </param>
    /// <exception cref="InvalidOperationException">An env file is outside its kustomization's folder, or the bases form a cycle.</exception>
    public static Kustomization Load(string directory, bool allowFilesOutsideFolder = false)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var root = Path.GetFullPath(directory);
        var state = LoadDirectory(root, allowFilesOutsideFolder, []);

        // Show kustomization files relative to the closest folder that holds all of them.
        var files = state.SelectMany(g => g.Keys.Values).SelectMany(h => h).Select(h => h.Kustomization).Distinct().ToList();
        var common = CommonFolder(files.Count > 0 ? files : [Path.Combine(root, "kustomization.yaml")]);
        foreach (var generated in state)
        {
            foreach (var history in generated.Keys.Values)
            {
                for (var i = 0; i < history.Count; i++)
                {
                    history[i] = history[i] with { Kustomization = Path.GetRelativePath(common, history[i].Kustomization).Replace('\\', '/') };
                }
            }
        }

        return new Kustomization(state);
    }

    /// <summary>Finds a generated ConfigMap or Secret by the name a manifest uses, which may carry a hash suffix.</summary>
    public GeneratedData? Find(string name, bool isSecret)
    {
        ArgumentNullException.ThrowIfNull(name);
        var generated = isSecret ? Secrets : ConfigMaps;
        if (generated.TryGetValue(name, out var exact))
        {
            return exact;
        }

        // kustomize appends "-" and a 10-character hash.
        var dash = name.LastIndexOf('-');
        if (dash > 0 && name.Length - dash - 1 == 10 && generated.TryGetValue(name[..dash], out var hashed))
        {
            return hashed;
        }

        return null;
    }

    private static List<GeneratedData> LoadDirectory(string directory, bool allowFilesOutsideFolder, HashSet<string> visiting)
    {
        if (!visiting.Add(directory))
        {
            throw new InvalidOperationException($"Kustomization cycle at {directory}.");
        }

        var file = FileNames.Select(f => Path.Combine(directory, f)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"No kustomization file in {directory}.");
        var display = file;

        YamlNode doc;
        try
        {
            doc = Yaml.LoadDocuments(File.ReadAllText(file)).FirstOrDefault() ?? new YamlMappingNode();
        }
        catch (YamlException ex)
        {
            throw new FormatException($"Could not parse {file}: {ex.Message}", ex);
        }

        var state = new List<GeneratedData>();
        foreach (var resource in doc.Get("resources").Items().Concat(doc.Get("bases").Items()).Concat(doc.Get("components").Items()))
        {
            var rel = resource.Scalar();
            if (rel is null)
            {
                continue;
            }

            var child = Path.GetFullPath(Path.Combine(directory, rel));
            if (Directory.Exists(child))
            {
                foreach (var generated in LoadDirectory(child, allowFilesOutsideFolder, visiting))
                {
                    state.RemoveAll(g => g.Name == generated.Name && g.IsSecret == generated.IsSecret);
                    state.Add(generated);
                }
            }
        }

        ApplyGenerators(doc.Get("configMapGenerator"), isSecret: false, directory, display, allowFilesOutsideFolder, state);
        ApplyGenerators(doc.Get("secretGenerator"), isSecret: true, directory, display, allowFilesOutsideFolder, state);

        var prefix = doc.Str("namePrefix") ?? "";
        var suffix = doc.Str("nameSuffix") ?? "";
        if (prefix.Length > 0 || suffix.Length > 0)
        {
            for (var i = 0; i < state.Count; i++)
            {
                state[i] = state[i] with { Name = prefix + state[i].Name + suffix };
            }
        }

        visiting.Remove(directory);
        return state;
    }

    private static void ApplyGenerators(YamlNode? generators, bool isSecret, string directory, string display, bool allowFilesOutsideFolder, List<GeneratedData> state)
    {
        foreach (var generator in generators.Items())
        {
            var name = generator.Str("name");
            if (string.IsNullOrEmpty(name))
            {
                // kustomize rejects a generator without a name (found by fuzzing: name: '').
                continue;
            }

            var behavior = generator.Str("behavior") ?? "create";
            var entries = ReadEntries(generator, directory, allowFilesOutsideFolder);

            var existing = state.FirstOrDefault(g => g.Name == name && g.IsSecret == isSecret);
            if (existing is null || behavior == "create")
            {
                if (existing is not null)
                {
                    state.Remove(existing);
                }

                existing = new GeneratedData(name, isSecret, new Dictionary<string, List<KustomizeStep>>(StringComparer.Ordinal));
                state.Add(existing);
            }
            else if (behavior == "replace")
            {
                foreach (var (_, history) in existing.Keys)
                {
                    if (history.Count > 0 && !history[^1].IsRemoval)
                    {
                        history.Add(new KustomizeStep(display, generator.Line(), null, "replace", IsRemoval: true));
                    }
                }
            }

            foreach (var (key, value, line, kind) in entries)
            {
                if (!existing.Keys.TryGetValue(key, out var history))
                {
                    existing.Keys[key] = history = [];
                }

                history.Add(new KustomizeStep(display, line, value, $"{behavior} ({kind})", IsRemoval: false));
            }
        }
    }

    private static List<(string Key, string? Value, int Line, string Kind)> ReadEntries(YamlNode generator, string directory, bool allowFilesOutsideFolder)
    {
        var entries = new List<(string, string?, int, string)>();
        foreach (var literal in generator.Get("literals").Items())
        {
            if (literal.Scalar() is { } text && text.IndexOf('=', StringComparison.Ordinal) is var eq and > 0)
            {
                entries.Add((text[..eq], Unquote(text[(eq + 1)..]), literal.Line(), "literal"));
            }
        }

        foreach (var env in generator.Get("envs").Items().Append(generator.Get("env") ?? new YamlScalarNode()))
        {
            var rel = env.Scalar();
            if (rel is null)
            {
                continue;
            }

            var path = Path.Combine(directory, rel);
            if (!File.Exists(path))
            {
                continue;
            }

            if (!allowFilesOutsideFolder && !IsInFolder(path, directory))
            {
                throw new InvalidOperationException(
                    $"The env file {rel} named in {directory} is outside that folder. kustomize refuses this by default; " +
                    "pass allowFilesOutsideFolder: true to read it anyway.");
            }

            var lineNo = 0;
            foreach (var line in File.ReadLines(path))
            {
                lineNo++;
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var eq = trimmed.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    // The step's line points into the kustomization file, at the envs entry; the env file's own line is in How.
                    entries.Add((trimmed[..eq], Unquote(trimmed[(eq + 1)..]), env.Line(), string.Create(CultureInfo.InvariantCulture, $"env file {rel}:{lineNo}")));
                }
            }
        }

        foreach (var fileEntry in generator.Get("files").Items())
        {
            var spec = fileEntry.Scalar();
            if (spec is null)
            {
                continue;
            }

            var eq = spec.IndexOf('=', StringComparison.Ordinal);
            var key = eq > 0 ? spec[..eq] : Path.GetFileName(spec);
            var rel = eq > 0 ? spec[(eq + 1)..] : spec;
            entries.Add((key, null, fileEntry.Line(), $"file {rel}"));
        }

        return entries;
    }

    /// <summary>True when <paramref name="path"/> is in or below <paramref name="folder"/>, after following symbolic links.</summary>
    internal static bool IsInFolder(string path, string folder)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var resolvedFolder = Path.TrimEndingDirectorySeparator(RealPath(folder)) + Path.DirectorySeparatorChar;
        return RealPath(path).StartsWith(resolvedFolder, comparison);
    }

    /// <summary>The full path with every symbolic link along it followed.</summary>
    private static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                current = Path.GetFullPath(target.FullName);
            }
        }

        return current;
    }

    private static string CommonFolder(List<string> files)
    {
        var parts = files.Select(f => Path.GetDirectoryName(f)!.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).ToList();
        var length = parts.Min(p => p.Length);
        var common = 0;
        while (common < length && parts.All(p => string.Equals(p[common], parts[0][common], StringComparison.Ordinal)))
        {
            common++;
        }

        // With one folder, step up once so the folder name stays visible, for example "prod/kustomization.yaml".
        if (files.Select(Path.GetDirectoryName).Distinct(StringComparer.Ordinal).Count() == 1 && common > 1)
        {
            common--;
        }

        var joined = string.Join(Path.DirectorySeparatorChar, parts[0].Take(common));
        return joined.Length == 0 ? Path.GetPathRoot(files[0]) ?? "/" : joined;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;
}

/// <summary>A ConfigMap or Secret produced by a kustomize generator.</summary>
/// <param name="Name">The final name after prefixes and suffixes, without the hash.</param>
/// <param name="IsSecret">True for a <c>secretGenerator</c>.</param>
/// <param name="Keys">The history of each key, lowest priority (deepest base) first.</param>
public sealed record GeneratedData(string Name, bool IsSecret, Dictionary<string, List<KustomizeStep>> Keys);

/// <summary>One kustomization that set or removed a generated key.</summary>
/// <param name="Kustomization">The kustomization file, relative to the parent of the loaded directory.</param>
/// <param name="Line">
/// The line in the kustomization file. For an env file entry this is the line of the <c>envs</c> entry,
/// and <paramref name="How"/> names the env file and the line in it.
/// </param>
/// <param name="Value">The value, or null for file entries and removals.</param>
/// <param name="How">How it was set, for example <c>merge (literal)</c>.</param>
/// <param name="IsRemoval">True when a <c>behavior: replace</c> generator dropped the key.</param>
public sealed record KustomizeStep(string Kustomization, int Line, string? Value, string How, bool IsRemoval);
