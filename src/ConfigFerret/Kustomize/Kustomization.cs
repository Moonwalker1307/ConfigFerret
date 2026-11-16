using System.Globalization;
using System.Text;
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

    // Like kustomize, a base or component is read again for every kustomization that names it, so bases that each name
    // two folders of the next level are read 2^levels times. These limits are far above what a real overlay needs.
    private const int MaxLoads = 10_000;

    // Each nested base or component is a level of recursion, and a stack overflow ends the process.
    private const int MaxDepth = 100;

    // The text of the kustomization and env files one Load reads, counted again each time a file is read again.
    private const long MaxChars = 256L * 1024 * 1024;

    // The text of one kustomization or env file. Real ones hold a few KB, and each is kept whole while it is parsed.
    private const int MaxFileChars = 16 * 1024 * 1024;

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

    /// <summary>Loads the kustomization in <paramref name="directory"/> and all its bases and components.</summary>
    /// <param name="directory">The folder that holds the kustomization file.</param>
    /// <param name="allowFilesOutsideFolder">
    /// Like kustomize, a generator's env files must sit in or below the folder of the kustomization that names them,
    /// after following symbolic links. Pass true to read them from anywhere, as
    /// <c>kustomize build --load-restrictor LoadRestrictionsNone</c> does. Bases, resources and components may be anywhere
    /// on the drive, share or device that the kustomization naming them is on or really lies on; one on another, or behind
    /// a link to another, is read only with true too. Looking at it could already reach another host, such as the server
    /// of a UNC path, with the user's credentials.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// An env file is outside its kustomization's folder, a base, resource or component is on another drive, share or
    /// device, a <c>merge</c> or <c>replace</c> generator without a
    /// <c>namespace</c> of its own fits more than one generated object by its current or an earlier name, or the bases and
    /// components form a cycle. Also when they nest more than 100 deep, when reading them takes more than 10,000
    /// kustomizations, when one kustomization or env file holds more than 16 MiB of text, or when all the kustomization and
    /// env files read hold more than 256 MiB. A base, component or env file counts again each time something names it.
    /// </exception>
    public static Kustomization Load(string directory, bool allowFilesOutsideFolder = false)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var root = Path.GetFullPath(directory);
        var state = new List<GeneratedData>();
        LoadDirectory(root, allowFilesOutsideFolder, [], state, new Budget());

        // Show kustomization files relative to the closest folder that holds all of them.
        var files = state.SelectMany(g => g.Keys.Values).SelectMany(h => h).Select(h => h.Kustomization).Distinct().ToList();
        var common = CommonFolder(files.Count > 0 ? files : [Path.Combine(root, "kustomization.yaml")]);
        foreach (var generated in state)
        {
            foreach (var history in generated.Keys.Values)
            {
                for (var i = 0; i < history.Count; i++)
                {
                    history[i] = history[i] with { Kustomization = Path.GetRelativePath(common, history[i].Kustomization).Replace(Path.DirectorySeparatorChar, '/') };
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
        if (KustomizeHash.Strip(name) is { } unhashed && generated.TryGetValue(unhashed, out var hashed))
        {
            return hashed;
        }

        return null;
    }

    /// <summary>
    /// Applies the kustomization in <paramref name="directory"/> to <paramref name="state"/>: an empty list for a base,
    /// or the list of the kustomization that names it as a component.
    /// </summary>
    private static void LoadDirectory(string directory, bool allowFilesOutsideFolder, HashSet<string> visiting, List<GeneratedData> state, Budget budget)
    {
        if (!visiting.Add(directory))
        {
            throw new InvalidOperationException($"Kustomization cycle at {directory}.");
        }

        if (visiting.Count > MaxDepth)
        {
            throw new InvalidOperationException($"Stopped reading bases and components at {directory}: they nest more than {MaxDepth} deep.");
        }

        if (++budget.Loads > MaxLoads)
        {
            throw new InvalidOperationException(
                $"Stopped reading bases and components at {directory}: that is more than {MaxLoads} kustomizations, " +
                "counting a base or component once for every kustomization that names it.");
        }

        var file = FileNames.Select(f => Path.Combine(directory, f)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"No kustomization file in {directory}.");
        var display = file;

        YamlNode doc;
        try
        {
            doc = Yaml.LoadDocuments(ReadText(file, budget)).FirstOrDefault() ?? new YamlMappingNode();
        }
        catch (YamlException ex)
        {
            throw new FormatException($"Could not parse {file}: {ex.Message}", ex);
        }

        // The drive, share or device this folder really lies on, which is not always the one it is written on, such as a
        // checkout behind a junction to another drive.
        var realRoot = allowFilesOutsideFolder ? null : RealPath(directory, anyRoot: true) is { } real ? Path.GetPathRoot(real) : null;
        foreach (var child in Folders(doc.Get("resources").Items().Concat(doc.Get("bases").Items()), directory, allowFilesOutsideFolder, realRoot))
        {
            var resources = new List<GeneratedData>();
            LoadDirectory(child, allowFilesOutsideFolder, visiting, resources, budget);
            foreach (var generated in resources)
            {
                state.RemoveAll(g => g.Name == generated.Name && g.IsSecret == generated.IsSecret);
                state.Add(generated);
            }
        }

        ApplyGenerators(doc.Get("configMapGenerator"), isSecret: false, directory, display, allowFilesOutsideFolder, realRoot, state, budget);
        ApplyGenerators(doc.Get("secretGenerator"), isSecret: true, directory, display, allowFilesOutsideFolder, realRoot, state, budget);

        // Like kustomize, a component works on what this kustomization holds after its resources and generators, so its
        // generators merge into those objects and its namePrefix renames them; this kustomization's own prefix comes after.
        foreach (var component in Folders(doc.Get("components").Items(), directory, allowFilesOutsideFolder, realRoot))
        {
            LoadDirectory(component, allowFilesOutsideFolder, visiting, state, budget);
        }

        var prefix = doc.Str("namePrefix") ?? "";
        var suffix = doc.Str("nameSuffix") ?? "";
        if (prefix.Length > 0 || suffix.Length > 0)
        {
            for (var i = 0; i < state.Count; i++)
            {
                state[i] = state[i] with { Name = prefix + state[i].Name + suffix, Renames = [.. state[i].Renames, (prefix.Length, suffix.Length)] };
            }
        }

        visiting.Remove(directory);
    }

    /// <summary>
    /// The entries that name a folder, as full paths. Files and URLs are skipped. Unless files outside the folder may be
    /// read, an entry that <see cref="MayLookAt"/> refuses throws before anything looks at it.
    /// </summary>
    private static IEnumerable<string> Folders(IEnumerable<YamlNode> entries, string directory, bool allowFilesOutsideFolder, string? realRoot)
    {
        foreach (var rel in entries.Select(e => e.Scalar()).OfType<string>())
        {
            var path = Path.GetFullPath(Path.Combine(directory, rel));
            if (!allowFilesOutsideFolder && !MayLookAt(path, directory, realRoot))
            {
                throw new InvalidOperationException(
                    $"The entry {rel} named in {directory} is on another drive, share or device, or a link along it leads to one or can't be followed, " +
                    "so it is not looked at; pass allowFilesOutsideFolder: true to read it.");
            }

            if (Directory.Exists(path))
            {
                yield return path;
            }
        }
    }

    // True when a path named in the kustomization in directory may be looked at on disk: it is written on the root
    // (drive, share or device) that folder is written on or really lies on, and every link along it stays on those two.
    // Only the text of the path and links on those roots are looked at to tell.
    internal static bool MayLookAt(string path, string directory, string? realRoot)
    {
        var full = Path.GetFullPath(path);
        var written = Path.GetPathRoot(Path.GetFullPath(directory)) ?? "";
        return (IsOnRoot(full, written) || (realRoot is not null && IsOnRoot(full, realRoot)))
            && RealPath(full, alsoRoot: realRoot ?? written) is not null;
    }

    // Reads a text file and counts it against what one Load may read. Stops early on a device such as /dev/zero.
    private static string ReadText(string path, Budget budget)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = new StringBuilder();
        var buffer = new char[81920];
        for (var read = reader.ReadBlock(buffer, 0, buffer.Length); read > 0; read = reader.ReadBlock(buffer, 0, buffer.Length))
        {
            if (text.Length + read > MaxFileChars)
            {
                throw new InvalidOperationException($"Stopped reading at {path}: it holds more than 16 MiB of text.");
            }

            budget.Chars += read;
            if (budget.Chars > MaxChars)
            {
                throw new InvalidOperationException($"Stopped reading at {path}: the kustomization and env files read so far hold more than 256 MiB of text.");
            }

            text.Append(buffer, 0, read);
        }

        return text.ToString();
    }

    private static void ApplyGenerators(YamlNode? generators, bool isSecret, string directory, string display, bool allowFilesOutsideFolder, string? realRoot, List<GeneratedData> state, Budget budget)
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
            var entries = ReadEntries(generator, directory, allowFilesOutsideFolder, realRoot, budget);

            // Like kustomize, merge and replace also find an object by a name it had before a namePrefix or nameSuffix,
            // and refuse to pick one when several fit.
            var matches = state.Where(g => g.IsSecret == isSecret && (g.Name == name || (behavior != "create" && g.HadName(name)))).ToList();
            if (matches.Count > 1)
            {
                // kustomize also matches by namespace, which is not traced here. A generator with its own namespace may
                // fit just one of them there, so it falls back to the current name only.
                if (string.IsNullOrEmpty(generator.Str("namespace")))
                {
                    throw new InvalidOperationException(
                        $"The {(isSecret ? "secretGenerator" : "configMapGenerator")} {name} in {directory} fits more than one generated object, which kustomize refuses.");
                }

                matches = matches.FindAll(g => g.Name == name);
            }

            var existing = matches.FirstOrDefault();
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

    private static InvalidOperationException EnvFileOutside(string rel, string directory) => new(
        $"The env file {rel} named in {directory} is outside that folder. kustomize refuses this by default; " +
        "pass allowFilesOutsideFolder: true to read it anyway.");

    private static List<(string Key, string? Value, int Line, string Kind)> ReadEntries(YamlNode generator, string directory, bool allowFilesOutsideFolder, string? realRoot, Budget budget)
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

            // An env file on another root is refused before anything looks at it, so even a missing one is refused.
            var path = Path.Combine(directory, rel);
            if (!allowFilesOutsideFolder && !MayLookAt(path, directory, realRoot))
            {
                throw EnvFileOutside(rel, directory);
            }

            if (!File.Exists(path))
            {
                continue;
            }

            if (!allowFilesOutsideFolder && !IsInFolder(path, directory))
            {
                throw EnvFileOutside(rel, directory);
            }

            var lineNo = 0;
            using var lines = new StringReader(ReadText(path, budget));
            for (var line = lines.ReadLine(); line is not null; line = lines.ReadLine())
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

    /// <summary>
    /// True when <paramref name="path"/> is in or below <paramref name="folder"/>, after following symbolic links. The
    /// folder itself counts only with <paramref name="orFolder"/>. A link in the path to another root (drive, share or
    /// device) counts as outside without being followed, unless it is the root the folder really lies on; the folder's
    /// own links are followed to any root, so a checkout behind a junction to another drive still works.
    /// </summary>
    internal static bool IsInFolder(string path, string folder, bool orFolder = false)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (RealPath(folder, anyRoot: true) is not { } realFolder || RealPath(path, alsoRoot: Path.GetPathRoot(realFolder)) is not { } realPath)
        {
            return false;
        }

        // A root such as / or C:\ already ends in a separator, which trimming keeps.
        var trimmed = Path.TrimEndingDirectorySeparator(realFolder);
        var prefix = Path.EndsInDirectorySeparator(trimmed) ? trimmed : trimmed + Path.DirectorySeparatorChar;
        return (realPath.Length > prefix.Length && realPath.StartsWith(prefix, comparison))
            || (orFolder && string.Equals(Path.TrimEndingDirectorySeparator(realPath), trimmed, comparison));
    }

    // As many links as Linux follows in one path before it gives up.
    private const int MaxLinks = 40;

    /// <summary>
    /// The full path with every symbolic link along it followed, the way the file system does: a link's target is
    /// resolved again, links in its folders too. Null after <see cref="MaxLinks"/> links, at a link whose target is
    /// rooted but not fully qualified or holds U+FFFD, or, unless <paramref name="anyRoot"/> is set, at a link to a
    /// root (drive, share or device) other than the one the path is written on and <paramref name="alsoRoot"/>:
    /// looking at that target could already reach another host, such as the server of a UNC path, with the user's
    /// credentials.
    /// </summary>
    internal static string? RealPath(string path, bool anyRoot = false, string? alsoRoot = null)
    {
        // .NET removes ".." from the path it is given as text before it opens it, so that is the path to check.
        var links = 0;
        var full = Path.GetFullPath(path);
        var written = Path.GetPathRoot(full) ?? "";
        string[]? roots = anyRoot ? null : alsoRoot is null ? [written] : [written, alsoRoot];
        return RealPath(full, roots, ref links);
    }

    /// <summary>True when a full path starts from <paramref name="root"/>, ignoring case on Windows.</summary>
    internal static bool IsOnRoot(string fullPath, string root) =>
        string.Equals(Path.GetPathRoot(fullPath), root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? RealPath(string full, string[]? roots, ref int links)
    {
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            current = Path.Join(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is { } target)
            {
                // On Windows a target such as \secrets\file or C:file is rooted but not fully qualified: it starts from
                // the root or the current folder of a drive, not from the link's folder. Such a link is refused, and so
                // is one whose target held bytes that are not UTF-8, which .NET reads as U+FFFD: that is another path.
                if (++links > MaxLinks || (Path.IsPathRooted(target) && !Path.IsPathFullyQualified(target)) || target.Contains('\uFFFD', StringComparison.Ordinal)
                    || (roots is not null && Path.IsPathFullyQualified(target) && !Array.Exists(roots, r => IsOnRoot(target, r))))
                {
                    return null;
                }

                // The file system resolves a link's target itself: a relative one starts from the link's folder,
                // which is already resolved, and ".." steps up from wherever a link in it led.
                var resolved = RealPath(Path.IsPathFullyQualified(target) ? target : Path.Join(Path.GetDirectoryName(current), target), roots, ref links);
                if (resolved is null)
                {
                    return null;
                }

                current = resolved;
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

    /// <summary>How much one Load has read so far.</summary>
    private sealed class Budget
    {
        public int Loads { get; set; }

        public long Chars { get; set; }
    }
}

/// <summary>A ConfigMap or Secret produced by a kustomize generator.</summary>
/// <param name="Name">The final name after prefixes and suffixes, without the hash.</param>
/// <param name="IsSecret">True for a <c>secretGenerator</c>.</param>
/// <param name="Keys">The history of each key, lowest priority (deepest base) first.</param>
public sealed record GeneratedData(string Name, bool IsSecret, Dictionary<string, List<KustomizeStep>> Keys)
{
    /// <summary>
    /// The length of each <c>namePrefix</c> and <c>nameSuffix</c> applied, first the innermost. Every earlier name is a
    /// part of <see cref="Name"/>, so it is not kept as a string of its own.
    /// </summary>
    internal IReadOnlyList<(int Prefix, int Suffix)> Renames { get; init; } = [];

    /// <summary>True when the object had <paramref name="name"/> before one of its prefixes or suffixes.</summary>
    internal bool HadName(string name)
    {
        var (start, end) = (0, Name.Length);
        for (var i = Renames.Count - 1; i >= 0; i--)
        {
            start += Renames[i].Prefix;
            end -= Renames[i].Suffix;
            if (Name.AsSpan(start, end - start).SequenceEqual(name))
            {
                return true;
            }
        }

        return false;
    }
}

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
