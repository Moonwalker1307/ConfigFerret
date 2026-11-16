using System.Globalization;
using System.Text;
using ConfigFerret.Internal;
using ConfigFerret.Kustomize;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ConfigFerret.Compose;

/// <summary>
/// A Docker Compose project: one or more compose files merged in order (<c>compose.yaml</c> then
/// <c>compose.override.yaml</c>, or each <c>-f</c> file), with <c>extends</c> and <c>include</c> followed, and every
/// service's container environment worked out the way <c>docker compose config</c> does.
/// </summary>
/// <remarks>
/// <para>
/// A service's environment is each <c>env_file</c> in order, then its <c>environment</c> entries, which win. A later
/// compose file's <c>environment</c> entry wins over an earlier one's; <c>env_file</c> lists are appended, and a path
/// listed again keeps its first place.
/// <c>!reset</c> and <c>!override</c> tags and YAML merge keys (<c>&lt;&lt;: *anchor</c>) are applied. Like compose, an
/// unquoted value in the map form of <c>environment</c> that YAML reads as a number, a boolean or a date, or a value
/// tagged <c>!!int</c>, <c>!!float</c>, <c>!!bool</c> or <c>!!timestamp</c>, is passed as Go formats it: <c>3.10</c>
/// becomes <c>3.1</c>, <c>0755</c> becomes <c>493</c> and <c>2001-01-01</c> becomes
/// <c>2001-01-01 00:00:00 +0000 UTC</c>. A value that does not fit its tag, which compose refuses, is kept as written
/// with a warning.
/// </para>
/// <para>
/// <c>${VAR}</c> references in the compose files are filled in from <see cref="ComposeOptions.Variables"/> (the
/// shell), then the project's env files (<see cref="ComposeOptions.EnvFiles"/>, else the project directory's
/// <c>.env</c>). The process environment is read only when <see cref="ComposeOptions.UseProcessEnvironment"/> is set.
/// An included file sees the environment of the project that includes it first; its own env files only add variables
/// that are not set there. In a service's <c>env_file</c>, <c>${VAR}</c> and a bare key take a variable from that
/// environment, then from the service's own <c>environment</c> entries.
/// </para>
/// <para>
/// A file that compose would need but that is missing (a required <c>env_file</c>, an <c>extends</c> file or an
/// <c>include</c>) is reported in <see cref="Warnings"/> instead of failing, so the rest can still be traced. So is a
/// file that a compose file names outside the project directory, which is not read unless
/// <see cref="ComposeOptions.AllowFilesOutsideProject"/> is set.
/// </para>
/// </remarks>
public sealed class ComposeProject
{
    private const int MaxIncludeDepth = 8;

    // The most included projects one load reads. A file included again with another environment is another project.
    private const int MaxIncludedProjects = 1_000;
    private const int MaxExtendsDepth = 16;
    private const int MaxMergeDepth = 16;
    private const int MaxMountedFileChars = 1024 * 1024;

    // The most origin steps for ${VAR} references built for one load, included projects and all, like a trace's bound.
    private const int MaxOriginSteps = 250_000;
    private const string OutsideProject = "is outside the project directory";

    private readonly ComposeOptions _options;

    // The root project's directory, which an include shares: paths are shown relative to it, and the files that the
    // compose files name must be in it.
    private readonly string? _displayRoot;
    private readonly Dictionary<string, string> _shell = new(StringComparer.Ordinal);
    private readonly List<ComposeFile> _files = [];
    private readonly Dictionary<string, ComposeFile> _extendsFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Part>> _parts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ComposeService> _services = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ComposeProject> _included = new(StringComparer.Ordinal);
    private readonly List<string> _serviceNames = [];
    private readonly List<string> _warnings = [];
    private readonly Dictionary<string, MountDefinition> _secrets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MountDefinition> _configs = new(StringComparer.Ordinal);
    private readonly Dictionary<YamlNode, Dictionary<string, (YamlScalarNode Key, YamlNode Value)>> _merged = new(ReferenceEqualityComparer.Instance);

    // Shared by the root project and every project it includes.
    private OriginBudget _origins = new(MaxOriginSteps);
    private readonly HashSet<string> _includedWarnings = new(StringComparer.Ordinal);

    // Every included project loaded for the root project, by its files, project directory, env files and depth, so a
    // file included many times, or by many files, is loaded once.
    private Dictionary<string, ComposeProject> _loaded = new(StringComparer.Ordinal);

    // The root the project directory really lies on, after its own links, such as a checkout behind a junction.
    private readonly Lazy<string?> _realRoot;

    // For an included project, the project that includes it: like compose, an include sees that project's environment
    // (the shell, then its env files) first, and its own env files only add what that does not set.
    private readonly ComposeProject? _parent;

    // What this project's environment is made of: the parent's, then the env files read, by path. An included file
    // is loaded once for each of these it is included with.
    private string _environmentKey = string.Empty;

    private ComposeProject(ComposeOptions options, string? projectDirectory, ComposeProject? parent)
    {
        _options = options;
        ProjectDirectory = projectDirectory;
        _parent = parent;
        _displayRoot = parent?._displayRoot ?? projectDirectory;
        _realRoot = new(() => _displayRoot is not null && Kustomization.RealPath(_displayRoot, anyRoot: true) is { } real ? Path.GetPathRoot(real) : null);
        if (options.UseProcessEnvironment)
        {
            foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            {
                _shell[(string)entry.Key] = entry.Value as string ?? string.Empty;
            }
        }

        foreach (var (name, value) in options.Variables.Where(v => v.Value is not null))
        {
            _shell[name] = value!;
        }

        Environment = DotEnvLayers.Build([], OuterLookup);
    }

    /// <summary>The project directory, or null when the files were parsed from text without one.</summary>
    public string? ProjectDirectory { get; }

    /// <summary>The compose files, as shown in reports, in the order they were merged.</summary>
    public IReadOnlyList<string> Files => [.. _files.Select(f => f.Display)];

    /// <summary>The project's env files (<c>.env</c> or <c>--env-file</c>) used for interpolation, lowest priority first.</summary>
    public DotEnvLayers Environment { get; private set; }

    /// <summary>The service names, in the order the files define them, then those that <c>include</c> brings in.</summary>
    public IReadOnlyList<string> ServiceNames => _serviceNames;

    /// <summary>Every service, in the order of <see cref="ServiceNames"/>.</summary>
    public IReadOnlyList<ComposeService> Services => [.. _serviceNames.Select(n => FindService(n)!)];

    /// <summary>Problems found while reading the project that don't belong to one service, such as an unset variable or an include that could not be read.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>The service named <paramref name="name"/>, or null.</summary>
    public ComposeService? FindService(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _services.TryGetValue(name, out var service) ? service
            : _included.TryGetValue(name, out var project) ? project.FindService(name)
            : null;
    }

    /// <summary>Reads and merges compose files, like <c>docker compose -f a.yaml -f b.yaml</c>.</summary>
    /// <param name="files">The compose files, lowest priority first, for example <c>compose.yaml</c> and <c>compose.override.yaml</c>.</param>
    /// <exception cref="FormatException">A file is not valid YAML or not a valid compose file, or a <c>${VAR:?error}</c> variable is not set.</exception>
    /// <exception cref="IOException">A compose file or an env file passed in could not be read.</exception>
    public static ComposeProject Load(params string[] files) => Load(files, null);

    /// <summary>Reads and merges compose files, like <c>docker compose -f a.yaml -f b.yaml --env-file x</c>.</summary>
    /// <param name="files">The compose files, lowest priority first.</param>
    /// <param name="options">The project directory, env files and variables. The process environment is not read unless the options say so.</param>
    /// <exception cref="FormatException">A file is not valid YAML or not a valid compose file, or a <c>${VAR:?error}</c> variable is not set.</exception>
    /// <exception cref="IOException">A compose file or an env file passed in could not be read.</exception>
    public static ComposeProject Load(IEnumerable<string> files, ComposeOptions? options)
    {
        ArgumentNullException.ThrowIfNull(files);
        var paths = files.Select(Path.GetFullPath).ToList();
        if (paths.Count == 0)
        {
            throw new ArgumentException("Pass at least one compose file.", nameof(files));
        }

        options ??= new ComposeOptions();
        var directory = options.ProjectDirectory is { Length: > 0 } given ? Path.GetFullPath(given) : Path.GetDirectoryName(paths[0])!;
        var texts = paths.Select(p => new ComposeText(p, File.ReadAllText(p))).ToList();
        return Create(texts, options, directory, null, 0, paths, new(StringComparer.Ordinal), new(MaxOriginSteps));
    }

    /// <summary>
    /// Parses compose files from text, lowest priority first. Relative paths (<c>env_file</c>, <c>extends</c>,
    /// <c>include</c>, secret files) are read from <see cref="ComposeOptions.ProjectDirectory"/>; without one, no
    /// other file is read and each one that would be is reported in the warnings.
    /// </summary>
    /// <param name="files">Each file's name as shown in reports, and its text.</param>
    /// <param name="options">The project directory, env files and variables.</param>
    /// <exception cref="FormatException">A file is not valid YAML or not a valid compose file.</exception>
    public static ComposeProject Parse(IEnumerable<(string Name, string Text)> files, ComposeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        options ??= new ComposeOptions();
        var directory = options.ProjectDirectory is { Length: > 0 } given ? Path.GetFullPath(given) : null;
        var texts = files.Select(f => new ComposeText(null, f.Text, f.Name)).ToList();
        return Create(texts, options, directory, null, 0, [], new(StringComparer.Ordinal), new(MaxOriginSteps));
    }

    private static ComposeProject Create(List<ComposeText> texts, ComposeOptions options, string? directory, ComposeProject? parent, int depth, IReadOnlyList<string> including, Dictionary<string, ComposeProject> loaded, OriginBudget origins)
    {
        var project = new ComposeProject(options, directory, parent) { _loaded = loaded, _origins = origins };
        project.LoadEnvironment(fromCaller: depth == 0);
        foreach (var text in texts)
        {
            project._files.Add(project.ParseFile(text, directory));
        }

        foreach (var file in project._files)
        {
            project.ReadDefinitions(file, "secrets", project._secrets);
            project.ReadDefinitions(file, "configs", project._configs);
            foreach (var (name, node) in project.Pairs(file.Services))
            {
                if (node.Value is not YamlMappingNode service)
                {
                    throw new FormatException($"{Yaml.Location(file.Display, node.Key)}: service {name} must be a mapping.");
                }

                if (!project._parts.TryGetValue(name, out var parts))
                {
                    project._parts[name] = parts = [];
                    project._serviceNames.Add(name);
                }

                parts.Add(new Part(file, name, service));
            }
        }

        foreach (var file in project._files)
        {
            project.ReadIncludes(file, depth, including);
        }

        foreach (var (name, parts) in project._parts)
        {
            project._services[name] = project.Compute(name, parts);
        }

        return project;
    }

    private void LoadEnvironment(bool fromCaller)
    {
        // The env files the caller passes are read as they are; an include's env files and the project directory's
        // .env are named by a compose file, or stand in for one. An include's project directory may be on another root,
        // so its .env is passed over before anything looks at it on disk, as a missing one is. A .env that is there but
        // is refused, such as a folder or a link to another drive, is kept so that the warning below names it.
        var named = !fromCaller || _options.EnvFiles.Count == 0;
        var paths = (_options.EnvFiles.Count > 0
            ? _options.EnvFiles.Select(Path.GetFullPath).ToList()
            : ProjectDirectory is null ? [] : [.. new[] { Path.Join(ProjectDirectory, ".env") }
                .Where(p => (_options.AllowFilesOutsideProject || (_displayRoot is not null && SharesRoot(p, _displayRoot))) && (Existing(p) is not null || (HasEntry(p) && Refusal(p) is not null)))])
            .Where(p => !named || !Refuses(p, $"The env file {Display(p)}", _warnings)).ToList();
        Environment = DotEnvLayers.Build(
            paths.Select(p => new DotEnvSourceText(Display(p), p, () => (named ? ReadNamed(p) : ReadCapped(p)) ?? throw new FileNotFoundException($"The env file {Display(p)} does not exist or is larger than 1 MiB.", p))),
            OuterLookup);
        _environmentKey = string.Join('\0', [_parent?._environmentKey ?? string.Empty, .. paths, string.Empty]);
        _warnings.AddRange(Environment.Warnings);
    }

    private LookupResult? ShellLookup(string name) => _shell.TryGetValue(name, out var value) ? new LookupResult(value, null) : null;

    // What a variable is before this project's own env files: the parent project's environment for an include, which
    // starts with the shell, else the shell.
    private LookupResult? OuterLookup(string name) => _parent is not null ? _parent.Lookup(name) : ShellLookup(name);

    /// <summary>
    /// Looks a variable up the way compose does: the shell first, then the project's env files. An included project
    /// asks the project that includes it before its own env files.
    /// </summary>
    internal LookupResult? Lookup(string name)
    {
        if (OuterLookup(name) is { } shell)
        {
            return shell;
        }

        var history = Environment.History(name);
        return history.Count > 0 ? new LookupResult(history[^1].Value, history[^1]) : null;
    }

    private ComposeFile ParseFile(ComposeText text, string? baseDirectory)
    {
        var display = text.Path is null ? text.Name! : Display(text.Path);
        List<YamlNode> documents;
        try
        {
            documents = Yaml.LoadDocuments(text.Text);
        }
        catch (YamlException ex)
        {
            throw new FormatException($"Could not parse the compose file {display}: {ex.Message}", ex);
        }

        if (documents.Count > 1)
        {
            _warnings.Add($"{display} holds {documents.Count} YAML documents; only the first is read.");
        }

        var root = documents.Count == 0 || documents[0].IsNull() ? new YamlMappingNode() : documents[0] as YamlMappingNode
            ?? throw new FormatException($"{display}: a compose file must be a mapping at the top level.");
        var services = root.Get("services");
        if (!services.IsNull() && services is not YamlMappingNode)
        {
            throw new FormatException($"{Yaml.Location(display, services!)}: services must be a mapping.");
        }

        return new ComposeFile(text.Path ?? display, display, baseDirectory, root, services as YamlMappingNode);
    }

    private void ReadDefinitions(ComposeFile file, string section, Dictionary<string, MountDefinition> into)
    {
        foreach (var (name, node) in Pairs(file.Root.Get(section)))
        {
            var at = Yaml.Location(file.Display, node.Key);
            var fileName = InterpolatedName(node.Value.Str("file"), at, _warnings);
            (string Value, bool IsSensitive, IReadOnlyList<DotEnvReference>)? content = node.Value.Str("content") is { } text ? InterpolatedContent(text, at, _warnings) : null;
            into[name] = new MountDefinition(
                name,
                at,
                fileName,
                fileName is null ? null : FullPath(file.Base, fileName),
                InterpolatedName(node.Value.Str("environment"), at, _warnings),
                content?.Value,
                string.Equals(node.Value.Str("external"), "true", StringComparison.OrdinalIgnoreCase) || node.Value.Get("external") is YamlMappingNode,
                content?.IsSensitive ?? false);
        }
    }

    private void ReadIncludes(ComposeFile file, int depth, IReadOnlyList<string> including)
    {
        foreach (var item in file.Root.Get("include").Items())
        {
            var at = Yaml.Location(file.Display, item);
            var paths = ItemsOrSelf(item is YamlMappingNode ? item.Get("path") : item).Select(p => p.Scalar()).ToList();
            if (paths.Count == 0 || paths.Exists(p => p is null))
            {
                throw new FormatException($"{at}: an include needs a path.");
            }

            if (file.Base is null)
            {
                _warnings.Add($"{at}: the include of {string.Join(", ", paths)} was not read, because the compose files were parsed from text without a project directory.");
                continue;
            }

            if (depth >= MaxIncludeDepth)
            {
                throw new FormatException($"{at}: includes nest more than {MaxIncludeDepth} deep.");
            }

            string Resolve(string text, string what) =>
                FullPath(file.Base, InterpolatedName(text, at, _warnings)!) ?? throw new FormatException($"{at}: the include's {what} is empty or not a valid path.");

            var full = paths.Select(p => Resolve(p!, "path")).ToList();
            if (full.Find(p => including.Contains(p, StringComparer.Ordinal)) is { } cycle)
            {
                throw new FormatException($"{at}: {Display(cycle)} includes itself.");
            }

            if (full.Exists(p => Refuses(p, $"{at}: the included file {Display(p)}", _warnings)))
            {
                continue;
            }

            var missing = full.Find(p => Existing(p) is null);
            if (missing is not null)
            {
                _warnings.Add($"{at}: the included file {Display(missing)} does not exist, so its services are not listed.");
                continue;
            }

            // Like compose, an empty project_directory stands for the default: the first included file's folder.
            var projectDirectory = item.Str("project_directory") is { Length: > 0 } dir && InterpolatedName(dir, at, _warnings) is { Length: > 0 } interpolated
                ? FullPath(file.Base, interpolated) ?? throw new FormatException($"{at}: the include's project_directory is not a valid path.")
                : Path.GetDirectoryName(full[0])!;
            var envFiles = ItemsOrSelf(item.Get("env_file")).Select(e => e.Scalar()).Where(e => e is not null)
                .Select(e => Resolve(e!, "env_file")).ToList();
            var key = string.Join('\0', [depth.ToString(CultureInfo.InvariantCulture), projectDirectory, .. full, string.Empty, .. envFiles, string.Empty, _environmentKey]);
            if (!_loaded.TryGetValue(key, out var sub))
            {
                if (_loaded.Count >= MaxIncludedProjects)
                {
                    throw new FormatException($"{at}: stopped reading includes, because they bring in more than {MaxIncludedProjects} projects.");
                }

                var texts = full.Select(p => new ComposeText(p, ReadNamed(p) ?? throw new FormatException($"{at}: the included file {Display(p)} is larger than 1 MiB."))).ToList();
                _loaded[key] = sub = Create(texts, _options.Copy(projectDirectory, envFiles), projectDirectory, this, depth + 1, [.. including, .. full], _loaded, _origins);
            }

            foreach (var name in sub.ServiceNames)
            {
                if (_parts.ContainsKey(name) || _included.ContainsKey(name))
                {
                    throw new FormatException($"{at}: the include brings in service {name}, which is already defined.");
                }

                _included[name] = sub;
                _serviceNames.Add(name);
            }

            // A file that several includes bring in reports its problems once.
            _warnings.AddRange(sub.Warnings.Where(_includedWarnings.Add));
        }
    }

    private ComposeService Compute(string service, List<Part> parts)
    {
        var warnings = new List<string>();
        var expanded = new List<Part>();
        foreach (var part in parts)
        {
            Expand(part, expanded, [], warnings);
        }

        var envFiles = new List<EnvFileDeclaration>();
        var environment = new Dictionary<string, List<EnvDeclaration>>(StringComparer.Ordinal);
        var secretMounts = new Dictionary<string, MountUse>(StringComparer.Ordinal);
        var configMounts = new Dictionary<string, MountUse>(StringComparer.Ordinal);
        foreach (var part in expanded)
        {
            var pairs = MergedPairs(part.Node, 0);
            if (pairs.TryGetValue("env_file", out var envFile))
            {
                AddEnvFiles(part, envFile.Value, envFiles, warnings);
            }

            if (pairs.TryGetValue("environment", out var env))
            {
                AddEnvironment(part, env.Value, environment, warnings);
            }

            if (pairs.TryGetValue("secrets", out var secrets))
            {
                AddMounts(part, secrets.Value, isSecret: true, secretMounts);
            }

            if (pairs.TryGetValue("configs", out var configs))
            {
                AddMounts(part, configs.Value, isSecret: false, configMounts);
            }
        }

        // Like compose, the service's environment entries are worked out first, and an env_file's ${VAR} and bare keys
        // take a variable from the project's environment, then from those entries. Such a value's origin is the entry.
        var fromEnvironment = new Dictionary<string, LookupResult>(StringComparer.Ordinal);
        foreach (var (name, declarations) in environment)
        {
            if (declarations[^1] is { Value: { } value } last)
            {
                fromEnvironment[name] = new LookupResult(value, null) { Environment = new ComposeEnvironmentOrigin(service, name, last.At, last.References) };
            }
        }

        var layers = new Dictionary<string, List<ProvenanceStep>>(StringComparer.Ordinal);
        foreach (var step in ReadEnvFiles(service, envFiles, name => Lookup(name) ?? fromEnvironment.GetValueOrDefault(name), warnings))
        {
            Layer(layers, ((DotEnvEntryRef)step.Reference!).Name).Add(step);
        }

        foreach (var (name, declarations) in environment)
        {
            Layer(layers, name).AddRange(declarations.Select(d => EnvironmentStep(service, d)));
        }

        // A secret and a config at the same path: the secret is listed, as it is mounted last.
        var mounts = new Dictionary<string, MountUse>(configMounts, StringComparer.Ordinal);
        foreach (var (path, mount) in secretMounts)
        {
            mounts[path] = mount;
        }

        var mountSteps = mounts.Values.ToDictionary(m => m.Path, m => MountStep(service, m, warnings), StringComparer.Ordinal);
        return new ComposeService(service, layers, mountSteps, mounts.Values.Select(m => new ComposeMount(m.Path, m.Name, m.IsSecret)).ToList(), warnings);
    }

    private static List<ProvenanceStep> Layer(Dictionary<string, List<ProvenanceStep>> layers, string name)
    {
        if (!layers.TryGetValue(name, out var list))
        {
            layers[name] = list = [];
        }

        return list;
    }

    // Puts the parts of the services that part extends before it, recursively, so later parts win.
    private void Expand(Part part, List<Part> into, List<string> visiting, List<string> warnings)
    {
        var key = part.File.Id + "#" + part.Service;
        if (visiting.Contains(key, StringComparer.Ordinal))
        {
            throw new FormatException($"Service {part.Service} in {part.File.Display} extends itself.");
        }

        if (visiting.Count >= MaxExtendsDepth)
        {
            throw new FormatException($"Service {part.Service} in {part.File.Display}: extends nests more than {MaxExtendsDepth} deep.");
        }

        if (MergedPairs(part.Node, 0).TryGetValue("extends", out var extends))
        {
            var at = Yaml.Location(part.File.Display, extends.Key);
            var baseName = InterpolatedName(extends.Value is YamlMappingNode ? extends.Value.Str("service") : extends.Value.Scalar(), at, warnings)
                ?? throw new FormatException($"{at}: extends needs a service.");
            var baseFile = InterpolatedName(extends.Value.Str("file"), at, warnings);
            visiting.Add(key);
            if (baseFile is not null)
            {
                var file = LoadExtendsFile(part.File, baseFile, at, warnings);
                if (file is not null && MergedPairs(file.Services, 0).TryGetValue(baseName, out var found) && found.Value is YamlMappingNode node)
                {
                    Expand(new Part(file, baseName, node), into, visiting, warnings);
                }
                else if (file is not null)
                {
                    warnings.Add($"{at}: {file.Display} has no service {baseName} to extend.");
                }
            }
            else if (MergedPairs(part.File.Services, 0).TryGetValue(baseName, out var same) && same.Value is YamlMappingNode node)
            {
                Expand(new Part(part.File, baseName, node), into, visiting, warnings);
            }
            else if (_parts.TryGetValue(baseName, out var baseParts))
            {
                foreach (var basePart in baseParts)
                {
                    Expand(basePart, into, visiting, warnings);
                }
            }
            else
            {
                warnings.Add($"{at}: service {part.Service} extends {baseName}, which is not defined.");
            }

            visiting.RemoveAt(visiting.Count - 1);
        }

        into.Add(part);
    }

    private ComposeFile? LoadExtendsFile(ComposeFile from, string path, string at, List<string> warnings)
    {
        var full = FullPath(from.Base, path);
        if (full is null)
        {
            warnings.Add($"{at}: the extends file {path} was not read, because the compose files were parsed from text without a project directory.");
            return null;
        }

        if (string.Equals(full, from.Id, StringComparison.Ordinal))
        {
            return from;
        }

        if (_extendsFiles.TryGetValue(full, out var known))
        {
            return known;
        }

        if (Refuses(full, $"{at}: the extends file {Display(full)}", warnings))
        {
            return null;
        }

        var text = ReadNamed(full);
        if (text is null)
        {
            warnings.Add($"{at}: the extends file {Display(full)} does not exist.");
            return null;
        }

        var file = ParseFile(new ComposeText(full, text), Path.GetDirectoryName(full));
        _extendsFiles[full] = file;
        return file;
    }

    private void AddEnvFiles(Part part, YamlNode node, List<EnvFileDeclaration> envFiles, List<string> warnings)
    {
        if (ResetsOrOverrides(node, envFiles.Clear))
        {
            return;
        }

        foreach (var item in ItemsOrSelf(node).Where(i => !i.IsNull()))
        {
            var at = Yaml.Location(part.File.Display, item);
            var path = InterpolatedName(item is YamlMappingNode ? item.Str("path") : item.Scalar(), at, warnings)
                ?? throw new FormatException($"{at}: an env_file entry needs a path.");
            var required = !string.Equals(item.Str("required"), "false", StringComparison.OrdinalIgnoreCase);
            var raw = string.Equals(item.Str("format"), "raw", StringComparison.Ordinal);
            var full = FullPath(part.File.Base, path);
            var declaration = new EnvFileDeclaration(full is null ? path : Display(full), full, required, raw, at);
            // Like compose, a path listed again keeps its first place, with what its last entry says.
            var index = envFiles.FindIndex(e => string.Equals(e.Display, declaration.Display, StringComparison.Ordinal));
            if (index >= 0)
            {
                envFiles[index] = declaration;
            }
            else
            {
                envFiles.Add(declaration);
            }
        }
    }

    private void AddEnvironment(Part part, YamlNode node, Dictionary<string, List<EnvDeclaration>> environment, List<string> warnings)
    {
        if (ResetsOrOverrides(node, environment.Clear))
        {
            return;
        }

        List<(string Name, string? Value, YamlNode Node, bool Typed, string? Tag)> entries = node switch
        {
            YamlMappingNode map => [.. MergedPairs(map, 0).Values.Select(p => MapEntry(part, p.Key, p.Value, warnings))],
            YamlSequenceNode seq => [.. seq.Children.Select(c => SplitListEntry(part, c)).Select(e => (e.Name, e.Value, e.Node, false, (string?)null))],
            _ when node.IsNull() => [],
            _ => throw new FormatException($"{Yaml.Location(part.File.Display, node)}: environment must be a mapping or a list."),
        };

        foreach (var (name, value, at, typed, tag) in entries.Select(e => (e.Name, e.Value, Yaml.Location(part.File.Display, e.Node), e.Typed, e.Tag)))
        {
            if (name.Length == 0)
            {
                throw new FormatException($"{at}: an environment entry needs a name.");
            }

            // Like compose, !reset on one entry's value drops what earlier files set for it and adds nothing, and
            // !override drops it and adds this entry.
            if (tag is "!reset" or "!override")
            {
                environment.Remove(name);
                if (tag == "!reset")
                {
                    continue;
                }
            }

            var interpolation = new Interpolation(Lookup, at);
            var expanded = value is null ? null : interpolation.Expand(value);
            warnings.AddRange(interpolation.Unset.Select(v => UnsetWarning(at, v)));
            if (!environment.TryGetValue(name, out var list))
            {
                environment[name] = list = [];
            }

            list.Add(new EnvDeclaration(name, expanded, interpolation.References, at) { Typed = typed });
        }
    }

    // Like compose, a plain value that YAML reads as a number, a boolean or a date, or a value tagged !!int, !!float,
    // !!bool or !!timestamp, is passed as compose formats it. go-yaml refuses the whole file for a value that does not
    // fit its tag; it is kept as written here, with a warning that names the tag and not the value. Tag is the value's
    // tag, such as !reset, or null.
    private static (string Name, string? Value, YamlNode Node, bool Typed, string? Tag) MapEntry(Part part, YamlScalarNode key, YamlNode value, List<string> warnings)
    {
        if (!value.Tag.IsEmpty && value.Tag.Value == "!reset")
        {
            return (key.Value!, null, key, false, "!reset");
        }

        if (value is not YamlScalarNode scalar)
        {
            throw new FormatException($"{Yaml.Location(part.File.Display, value)}: the value of {key.Value} must be a string, a number or a boolean.");
        }

        if (scalar.Value is { } text && TypedTag(scalar) is (true, var typedTag))
        {
            if (ComposeScalar.Format(text, typedTag) is { } formatted)
            {
                if (formatted != text)
                {
                    return (key.Value!, formatted, key, true, null);
                }
            }
            else if (typedTag is not null)
            {
                warnings.Add($"{Yaml.Location(part.File.Display, key)}: the value of {key.Value} in service {part.Service} does not fit its tag {typedTag.Replace("tag:yaml.org,2002:", "!!", StringComparison.Ordinal)}, so docker compose refuses the file; it is shown as written.");
            }
        }

        return (key.Value!, value.Scalar(), key, false, scalar.Tag.IsEmpty ? null : scalar.Tag.Value);
    }

    // Whether go-yaml types a scalar, and by which tag: a plain one without a tag by what it reads as, any one tagged
    // !!int, !!float, !!bool or !!timestamp by its tag. Others, such as a quoted one, are text.
    private static (bool Typed, string? Tag) TypedTag(YamlScalarNode scalar) =>
        scalar.Tag.IsEmpty ? (scalar.Style == ScalarStyle.Plain, null)
        : scalar.Tag.Value is ComposeScalar.IntTag or ComposeScalar.FloatTag or ComposeScalar.BoolTag or ComposeScalar.TimestampTag ? (true, scalar.Tag.Value)
        : (false, null);

    private static (string Name, string? Value, YamlNode Node) SplitListEntry(Part part, YamlNode item)
    {
        var text = item is YamlScalarNode ? item.Scalar() : null;
        if (text is null)
        {
            throw new FormatException($"{Yaml.Location(part.File.Display, item)}: an environment list entry must be NAME=VALUE or NAME.");
        }

        var eq = text.IndexOf('=', StringComparison.Ordinal);
        return eq < 0 ? (text.Trim(), null, item) : (text[..eq].Trim(), text[(eq + 1)..], item);
    }

    private static void AddMounts(Part part, YamlNode node, bool isSecret, Dictionary<string, MountUse> mounts)
    {
        if (ResetsOrOverrides(node, mounts.Clear))
        {
            return;
        }

        foreach (var item in node.Items().Where(i => !i.IsNull()))
        {
            var at = Yaml.Location(part.File.Display, item);
            var source = (item is YamlMappingNode ? item.Str("source") : item.Scalar())
                ?? throw new FormatException($"{at}: a {(isSecret ? "secret" : "config")} entry needs a source.");
            var target = item.Str("target");
            var folder = isSecret ? "/run/secrets/" : "/";
            var path = target is null ? folder + source : target.StartsWith('/') ? target : folder + target;
            mounts[path] = new MountUse(source, isSecret, path, at);
        }
    }

    // !reset clears what earlier files set and adds nothing; !override clears it and adds this node's entries.
    private static bool ResetsOrOverrides(YamlNode node, Action clear)
    {
        var tag = node.Tag.IsEmpty ? null : node.Tag.Value;
        if (tag is "!reset" or "!override")
        {
            clear();
        }

        return tag == "!reset";
    }

    private List<ProvenanceStep> ReadEnvFiles(string service, List<EnvFileDeclaration> declarations, Func<string, LookupResult?> lookup, List<string> warnings)
    {
        var steps = new List<ProvenanceStep>();
        var earlier = new Dictionary<string, DotEnvEntry>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            if (declaration.FullPath is null)
            {
                warnings.Add($"{declaration.At}: env_file {declaration.Display} was not read, because the compose files were parsed from text without a project directory.");
                continue;
            }

            if (Refuses(declaration.FullPath, $"{declaration.At}: env_file {declaration.Display}", warnings))
            {
                continue;
            }

            var text = ReadNamed(declaration.FullPath);
            if (text is null)
            {
                if (declaration.Required)
                {
                    warnings.Add($"{declaration.At}: env_file {declaration.Display} does not exist (or is larger than 1 MiB); docker compose stops with an error when a required env_file is missing.");
                }

                continue;
            }

            var file = DotEnvFile.Parse(text, declaration.Display, declaration.FullPath, lookup, declaration.Raw, v => earlier.TryGetValue(v, out var e) ? new LookupResult(e.Value, e) : null);
            warnings.AddRange(file.Warnings);
            steps.AddRange(file.Entries.Select(e => ComposeSteps.Entry(e, $"env_file {declaration.Display}", $"env_file of service {service} ({declaration.At})", _origins)));
            foreach (var entry in file.Entries)
            {
                earlier[entry.Name] = entry;
            }
        }

        return steps;
    }

    private ProvenanceStep EnvironmentStep(string service, EnvDeclaration declaration)
    {
        var step = new ProvenanceStep
        {
            Kind = LayerKind.DockerCompose,
            Source = $"compose service {service} env {declaration.Name}",
            Location = declaration.At,
            Reference = new ComposeEnvironmentRef(service, declaration.Name),
        };
        IReadOnlyList<DotEnvReference> references;
        if (declaration.Value is not null)
        {
            // The note never shows the value, since notes are not redacted.
            references = declaration.References;
            step = step with
            {
                Value = declaration.Value,
                Note = declaration.Typed ? "YAML reads this value as a number, a boolean or a date, so compose passes it formatted; quote it to keep the text as written" : null,
            };
        }
        else if (Lookup(declaration.Name) is { } found)
        {
            references = [new DotEnvReference(declaration.Name, found.Entry, IsSet: true) { Value = found.Value }];
            step = step with { Value = found.Value, Note = "a name without a value, so the value comes from the shell or the project's env file" };
        }
        else
        {
            references = [];
            step = step with { IsRemoval = true, Note = "a name without a value that the shell and the project's env file don't set, so the container does not get it" };
        }

        var origins = ComposeSteps.References(references, declaration.Value is null ? null : declaration.At, _origins, out var cut);
        return step with
        {
            IsSensitive = ComposeSteps.IsSensitiveName(declaration.Name) || references.Any(ComposeSteps.IsSensitive),
            Note = ComposeSteps.Join(step.Note, cut ? ComposeSteps.CutNote : null),
            Origins = origins,
        };
    }

    private ProvenanceStep MountStep(string service, MountUse mount, List<string> warnings)
    {
        var kind = mount.IsSecret ? "secret" : "config";
        var step = new ProvenanceStep
        {
            Kind = LayerKind.DockerCompose,
            Source = $"compose {kind} {mount.Name}",
            Location = mount.At,
            IsSensitive = mount.IsSecret || ComposeSteps.IsSensitiveName(mount.Name),
            Note = $"mounted at {mount.Path} on service {service}",
            Reference = new ComposeMountRef(mount.Name, mount.IsSecret),
        };
        if (!(mount.IsSecret ? _secrets : _configs).TryGetValue(mount.Name, out var definition))
        {
            warnings.Add($"{mount.At}: service {service} uses {kind} {mount.Name}, which is not defined under the top-level {kind}s.");
            return step with { Note = step.Note + "; it is not defined" };
        }

        if (definition.FileName is not null)
        {
            step = step with { Location = definition.FullPath is null ? definition.FileName : Display(definition.FullPath) };
            if (mount.IsSecret && !_options.ReadSecretFiles)
            {
                return step with { Note = step.Note + "; the file is not read" };
            }

            if (definition.FullPath is not null && Refusal(definition.FullPath) is { } refusal)
            {
                warnings.Add(NotRead($"{definition.At}: the {kind} file {Display(definition.FullPath)}", refusal));
                return step with { Note = step.Note + $"; the file {refusal}, so it is not read" };
            }

            var content = definition.FullPath is null ? null : ReadNamed(definition.FullPath);
            return content is null
                ? step with { Note = step.Note + "; the file could not be read" }
                : step with { Value = content };
        }

        if (definition.EnvironmentVariable is not null)
        {
            var found = Lookup(definition.EnvironmentVariable);
            return step with
            {
                Location = definition.At,
                Value = found?.Value,
                IsSensitive = step.IsSensitive || ComposeSteps.IsSensitiveName(definition.EnvironmentVariable) || (found?.Entry is { } entry && ComposeSteps.IsSensitive(entry)),
                Note = step.Note + $"; from the variable {definition.EnvironmentVariable}" + (found is null ? ", which is not set" : string.Empty),
            };
        }

        if (definition.Content is not null)
        {
            return step with { Location = definition.At, Value = definition.Content, IsSensitive = step.IsSensitive || definition.ContentIsSensitive };
        }

        return step with { Location = definition.At, Note = step.Note + (definition.External ? "; it is external, so its contents are not known" : "; its definition has no file, environment or content") };
    }

    // A path or a name: it ends up in sources, locations and warnings, which are never redacted, so it may not
    // take its value from a variable named like a secret.
    private string? InterpolatedName(string? text, string at, List<string> warnings)
    {
        if (text is null)
        {
            return null;
        }

        var expanded = InterpolatedContent(text, at, warnings);
        if (expanded.IsSensitive)
        {
            throw new FormatException($"{at}: a path or a name may not refer to a variable named like a secret ({string.Join(", ", expanded.References.Where(ComposeSteps.IsSensitive).Select(r => r.Name))}).");
        }

        return expanded.Value;
    }

    private (string Value, bool IsSensitive, IReadOnlyList<DotEnvReference> References) InterpolatedContent(string text, string at, List<string> warnings)
    {
        var interpolation = new Interpolation(Lookup, at);
        var value = interpolation.Expand(text);
        warnings.AddRange(interpolation.Unset.Select(v => UnsetWarning(at, v)));
        var sensitive = interpolation.References.Any(ComposeSteps.IsSensitive);
        return (value, sensitive, interpolation.References);
    }

    private static string UnsetWarning(string at, string name) => $"{at}: the {name} variable is not set, so an empty string is used.";

    // The keys of a mapping with YAML merge keys (<<: *anchor) applied: the mapping's own keys win over merged ones,
    // and an earlier merged mapping wins over a later one.
    private Dictionary<string, (YamlScalarNode Key, YamlNode Value)> MergedPairs(YamlNode? node, int depth)
    {
        if (node is not YamlMappingNode map)
        {
            return new(StringComparer.Ordinal);
        }

        if (_merged.TryGetValue(map, out var known))
        {
            return known;
        }

        if (depth > MaxMergeDepth)
        {
            throw new FormatException($"Line {map.Line().ToString(CultureInfo.InvariantCulture)}: merge keys nest more than {MaxMergeDepth} deep.");
        }

        var result = new Dictionary<string, (YamlScalarNode, YamlNode)>(StringComparer.Ordinal);
        var own = map.Children.Select(c => (Key: c.Key as YamlScalarNode, c.Value)).Where(c => c.Key?.Value is not null).ToList();
        static bool IsMerge(YamlScalarNode key) => key.Value == "<<" && key.Style == ScalarStyle.Plain;
        foreach (var source in own.Where(c => IsMerge(c.Key!)).SelectMany(m => ItemsOrSelf(m.Value)).Reverse())
        {
            foreach (var (name, pair) in MergedPairs(source, depth + 1))
            {
                result[name] = pair;
            }
        }

        foreach (var (key, value) in own.Where(c => !IsMerge(c.Key!)))
        {
            result[key!.Value!] = (key, value);
        }

        _merged[map] = result;
        return result;
    }

    private IEnumerable<(string Name, (YamlScalarNode Key, YamlNode Value) Node)> Pairs(YamlNode? node) =>
        MergedPairs(node, 0).Where(p => !string.IsNullOrEmpty(p.Key)).Select(p => (p.Key, p.Value));

    private static IEnumerable<YamlNode> ItemsOrSelf(YamlNode? node) => node switch
    {
        null => [],
        YamlSequenceNode seq => seq.Children,
        _ => [node],
    };

    private static string? FullPath(string? baseDirectory, string path)
    {
        if (baseDirectory is null || path.Length == 0 || path.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        // Compose accepts absolute paths (env_file: /etc/app.env) as well as paths relative to the file.
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Join(baseDirectory, path));
    }

    /// <summary>A path as shown in reports: relative to the project directory when it is inside it, with / separators.</summary>
    internal string Display(string fullPath)
    {
        if (_displayRoot is null)
        {
            return fullPath.Replace('\\', '/');
        }

        var relative = Path.GetRelativePath(_displayRoot, fullPath);
        return (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? fullPath : relative).Replace('\\', '/');
    }

    // Why a file that a compose file names is not read, or null when it may be. With every link followed it has to be
    // in or below the root project directory, unless the options allow files outside it, and it may not be a folder.
    // A path on another root is refused before anything looks at it on disk.
    private string? Refusal(string fullPath) =>
        !_options.AllowFilesOutsideProject && (_displayRoot is null || !SharesRoot(fullPath, _displayRoot) || !Kustomization.IsInFolder(fullPath, _displayRoot)) ? OutsideProject
        : Directory.Exists(fullPath) ? "is not a regular file"
        : null;

    // True when two full paths start from the same root: the same drive, share or device. Looking at a path on another
    // root could already reach out, such as to the server of a UNC path, with the user's credentials.
    internal static bool SharesRoot(string fullPath, string folder) =>
        string.Equals(Path.GetPathRoot(fullPath), Path.GetPathRoot(folder), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // True, with a warning that starts with what, when a file that a compose file names is not read.
    private bool Refuses(string fullPath, string what, List<string> warnings)
    {
        if (Refusal(fullPath) is not { } refusal)
        {
            return false;
        }

        warnings.Add(NotRead(what, refusal));
        return true;
    }

    private static string NotRead(string what, string refusal) => refusal == OutsideProject
        ? $"{what} {refusal}, so it is not read; set AllowFilesOutsideProject (--compose-allow-files-outside-project) to read it."
        : $"{what} {refusal}, so it is not read.";

    // The file that a path leads to with every link followed, the way the file system resolves it, or null when there
    // is none, as for a link whose target is missing. A link to a root other than the path's and the one the project
    // directory really lies on is followed only when files outside the project may be read.
    private FileInfo? Existing(string fullPath) =>
        Kustomization.RealPath(fullPath, anyRoot: _options.AllowFilesOutsideProject, alsoRoot: _options.AllowFilesOutsideProject ? null : _realRoot.Value) is { } real
        && new FileInfo(real) is { Exists: true } file ? file : null;

    // True when the folder of a path resolves, by the rule Existing uses, and holds an entry of that name: a file, a
    // folder or a link, whose own link is not followed. A folder that does not resolve has nothing in it to look at.
    private bool HasEntry(string fullPath) =>
        Path.GetDirectoryName(fullPath) is { } folder
        && Kustomization.RealPath(folder, anyRoot: _options.AllowFilesOutsideProject, alsoRoot: _options.AllowFilesOutsideProject ? null : _realRoot.Value) is { } real
        && (int)new FileInfo(Path.Join(real, Path.GetFileName(fullPath))).Attributes != -1;

    // Reads a file that a compose file names, once Refusal allows it, from the path its links lead to. A file of size 0
    // is taken as empty without opening it: a FIFO, a socket and a device report size 0 too, .NET can't tell them from
    // an empty file without opening them, and opening a FIFO waits for a writer.
    private string? ReadNamed(string fullPath) => Existing(fullPath) switch
    {
        null => null,
        { Length: 0 } => string.Empty,
        var file => ReadCapped(file.FullName),
    };

    // Reads a text file of at most 1 MiB, or returns null when it does not exist or is bigger. Stops early on a
    // device such as /dev/zero.
    private static string? ReadCapped(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxMountedFileChars + 1];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        return read > MaxMountedFileChars ? null : new string(buffer, 0, read);
    }

    private sealed record ComposeText(string? Path, string Text, string? Name = null);

    private sealed record ComposeFile(string Id, string Display, string? Base, YamlMappingNode Root, YamlMappingNode? Services);

    private sealed record Part(ComposeFile File, string Service, YamlMappingNode Node);

    private sealed record EnvFileDeclaration(string Display, string? FullPath, bool Required, bool Raw, string At);

    // Typed is true for a value that YAML reads as a number, a boolean or a date and that compose passes formatted.
    private sealed record EnvDeclaration(string Name, string? Value, IReadOnlyList<DotEnvReference> References, string At)
    {
        public bool Typed { get; init; }
    }

    private sealed record MountUse(string Name, bool IsSecret, string Path, string At);

    private sealed record MountDefinition(string Name, string At, string? FileName, string? FullPath, string? EnvironmentVariable, string? Content, bool External, bool ContentIsSensitive);
}
