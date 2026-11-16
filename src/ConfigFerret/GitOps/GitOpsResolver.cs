using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using ConfigFerret.Helm;
using ConfigFerret.Kubernetes;
using ConfigFerret.Kustomize;
using ConfigFerret.Sources;
using Microsoft.Win32.SafeHandles;

namespace ConfigFerret.GitOps;

/// <summary>
/// Links layers to what a GitOps object adds: the Helm values of an Argo CD Application or Flux HelmRelease, the
/// ConfigMap keys an Argo CD kustomize patch sets, and the <c>${VAR}</c> placeholders a Flux Kustomization substitutes.
/// </summary>
internal sealed class GitOpsResolver : IOriginResolver, IKnowsUses
{
    private readonly GitOpsApplication _app;
    private readonly HelmChart? _chart;
    private readonly HelmResolver? _helm;
    private readonly List<string> _warnings = [];
    private HashSet<string>? _named; // the warnings given so far, so that a skipped object is named once
    private readonly List<KubernetesData?> _substituteFrom = [];
    private readonly Dictionary<(char Kind, string Object, string Key), SubstitutionTemplate> _templates = [];
    private readonly Dictionary<string, bool> _fromSecret = new(StringComparer.Ordinal); // whether the value that wins for a variable is from a Secret
    private readonly HashSet<string> _usedVariables = new(StringComparer.Ordinal); // every ${VAR} written in a manifest under the path
    private readonly List<KubernetesManifests> _data;
    private HashSet<string>? _namedValues; // the values paths the object sets that a chart template names
    private bool _warned;
    private bool _partlyRead; // a part of the Kustomization path, or the whole path, was not read

    /// <summary>Creates a resolver for one GitOps object.</summary>
    /// <param name="application">The GitOps object.</param>
    /// <param name="options">Where its files are.</param>
    /// <param name="fileData">ConfigMaps and Secrets read from a provider's data file, looked up after <see cref="GitOpsOptions.Data"/>.</param>
    public GitOpsResolver(GitOpsApplication application, GitOpsOptions? options = null, KubernetesManifests? fileData = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        options ??= new GitOpsOptions();
        _data = new[] { application.Data, options.Data, fileData }.OfType<KubernetesManifests>().ToList();
        _app = application;
        _warnings.AddRange(application.Notes.Select(n => $"{application.DisplayName}: {n}."));
        _warnings.AddRange(_data.SelectMany(d => d.Warnings));
        if (HasChart(application, options, _warnings))
        {
            _chart = HelmChart.Build(application, options, _data);
            _helm = new HelmResolver(_chart);
            if (_chart.TemplatesNotRead && GitOpsValues().Any())
            {
                _chart.SayNoValueIsListedAsUnused();
            }

            _warnings.AddRange(_chart.Warnings);
        }

        if (application.Kind == GitOpsKind.FluxKustomization)
        {
            LoadSubstitutions(options);
        }
    }

    /// <summary>The chart built from the object, when it deploys one.</summary>
    public HelmChart? Chart => _chart;

    /// <summary>Problems found while reading, and settings that are read but not traced.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!_warned)
        {
            _warned = true;
            foreach (var warning in _warnings)
            {
                diagnostics.Warn(warning);
            }
        }

        var helm = _helm?.Resolve(layer, diagnostics) ?? [];
        if (helm.Count > 0)
        {
            return helm;
        }

        return layer.Reference switch
        {
            ConfigMapKeyRef cm => [.. ResolvePatches(cm), .. ResolveTemplate(('c', cm.Name, cm.Key))],
            SecretKeyRef s => ResolveTemplate(('s', s.Name, s.Key)),
            WorkloadEnvRef env => ResolveTemplate(('e', env.Workload + "/" + env.Container, env.Name)),
            GitOpsVariableRef v when v.Kustomization == _app.Name => ResolveVariable(v.Name),
            _ => [],
        };
    }

    /// <summary>
    /// True for a values path that a chart template names, itself, a parent or a value below it, and for a variable that
    /// a manifest under the Kustomization path names: a template reads a value for replicas or an image too, and Flux
    /// substitutes a variable anywhere, in an Ingress host or an image tag too, where no trace step can show the use.
    /// When the path, or a part of it, was not read, every variable is taken as used, and when the chart's templates were
    /// not read, every value.
    /// </summary>
    public bool IsUsed(SourceRef reference) => reference switch
    {
        GitOpsVariableRef v => v.Kustomization == _app.Name && (_partlyRead || _usedVariables.Contains(v.Name)),
        HelmValueRef h when _chart is not null => _chart.TemplatesNotRead || (_namedValues ??= _chart.NamedByTemplates(GitOpsValues().Select(v => v.Key))).Contains(h.Path),
        _ => false,
    };

    // The values paths the object itself sets, not the chart's defaults.
    private IEnumerable<KeyValuePair<string, List<HelmValueStep>>> GitOpsValues() =>
        _chart!.History.Where(h => h.Value.Any(s => _chart.IsGitOpsSource(s.Source)));

    /// <summary>Everything the object sets, so unused entries can be listed.</summary>
    public List<SourceEntry> Entries()
    {
        var entries = new List<SourceEntry>();
        if (_chart is not null)
        {
            entries.AddRange(GitOpsValues()
                .OrderBy(h => h.Key, StringComparer.Ordinal)
                .Select(h => new SourceEntry(h.Key, new HelmValueRef(h.Key), _chart.IsSensitivePath(h.Key), h.Value[^1].Source, _app.DisplayName)));
        }

        entries.AddRange((_app.Kustomize?.Patches ?? [])
            .SelectMany(p => p.Values)
            .Select(v => new SourceEntry($"ConfigMap {v.ConfigMap} key {v.Key}", new GitOpsPatchRef(_app.Name, v.ConfigMap, v.Key), IsSensitive: false, "kustomize patch", _app.DisplayName))
            .Distinct());

        var fromData = _app.SubstituteFrom
            .Zip(_substituteFrom)
            .Where(z => z.Second is not null)
            .SelectMany(z => z.Second!.Data.Keys.Select(k => (Name: k, Detail: $"substituteFrom {(z.First.IsSecret ? "Secret" : "ConfigMap")} {z.First.Name}", Secret: z.First.IsSecret)));
        var variables = fromData
            .Concat(_app.Substitutions.Select(s => (s.Name, Detail: "postBuild.substitute", Secret: false)))
            .GroupBy(v => v.Name, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new SourceEntry(g.Key, new GitOpsVariableRef(_app.Name, g.Key), g.Any(v => v.Secret), g.Last().Detail, _app.DisplayName));
        entries.AddRange(variables);
        return entries;
    }

    private static bool HasChart(GitOpsApplication application, GitOpsOptions options, List<string> warnings)
    {
        if (application.HelmSources.Count > 0)
        {
            return true;
        }

        // An Argo CD source with a plain path is a Helm chart when the folder holds a Chart.yaml.
        if (application.Kind == GitOpsKind.FluxKustomization || application.Kustomize is not null || application.Path is null)
        {
            return false;
        }

        var folder = !string.IsNullOrWhiteSpace(options.ChartFolder) ? options.ChartFolder
            : !string.IsNullOrWhiteSpace(options.RepositoryRoot) ? Path.Join(options.RepositoryRoot, application.Path)
            : null;
        if (folder is null)
        {
            return false;
        }

        // Checked before it is looked at, so that whether a chart is found never tells what exists outside the checkout.
        var chartFile = Path.Join(folder, "Chart.yaml");
        if (string.IsNullOrWhiteSpace(options.ChartFolder))
        {
            if (!HelmChart.IsInRoot(folder, options.RepositoryRoot!))
            {
                warnings.Add($"The source path {application.Path} of {application.DisplayName} is outside the repository checkout, so it was not read.");
                return false;
            }

            if (!Kustomization.IsInFolder(chartFile, options.RepositoryRoot!))
            {
                return false;
            }
        }

        return File.Exists(chartFile);
    }

    private List<ProvenanceStep> ResolvePatches(ConfigMapKeyRef cm)
    {
        var kustomize = _app.Kustomize;
        if (kustomize is null)
        {
            return [];
        }

        return kustomize.Patches
            .SelectMany(p => p.Values)
            .Where(v => v.Key == cm.Key && NameMatches(v.ConfigMap, cm.Name, kustomize))
            .Select(v => new ProvenanceStep
            {
                Kind = LayerKind.GitOps,
                Source = $"{_app.DisplayName} (kustomize.patches)",
                Location = Yaml(_app.SourceName, v.Line),
                Value = v.Value,
                IsRemoval = v.IsRemoval,
                Note = $"kustomize patch on ConfigMap {v.ConfigMap}",
                Reference = new GitOpsPatchRef(_app.Name, v.ConfigMap, v.Key),
            })
            .ToList();
    }

    /// <summary>
    /// True when <paramref name="rendered"/> is <paramref name="name"/>, with the source's prefix and suffix, and with or without kustomize's hash.
    /// The hash is ten characters from kustomize's alphabet, so a last word such as production is not one.
    /// </summary>
    private static bool NameMatches(string name, string rendered, GitOpsKustomize kustomize)
    {
        var full = (kustomize.NamePrefix ?? "") + name + (kustomize.NameSuffix ?? "");
        var unhashed = KustomizeHash.Strip(rendered) ?? rendered;
        return rendered == name || rendered == full || unhashed == name || unhashed == full;
    }

    private void LoadSubstitutions(GitOpsOptions options)
    {
        foreach (var from in _app.SubstituteFrom)
        {
            // Flux reads substituteFrom from the Kustomization's own namespace.
            var data = _data
                .Select(d => d.FindLoaded(from.IsSecret, _app.Namespace, from.Name))
                .FirstOrDefault(d => d is not null);
            if (data is null && !from.Optional)
            {
                _warnings.Add($"{_app.DisplayName} reads substituteFrom {(from.IsSecret ? "Secret" : "ConfigMap")} {from.Name}, which was not loaded; pass it with the GitOps data manifests.");
            }

            _substituteFrom.Add(data);
            foreach (var name in data?.Data.Keys ?? [])
            {
                _fromSecret[name] = from.IsSecret;
            }
        }

        // The inline substitute wins over substituteFrom, and a later substituteFrom over an earlier one.
        foreach (var substitution in _app.Substitutions)
        {
            _fromSecret[substitution.Name] = false;
        }

        if (_app.Substitutions.Count == 0 && _app.SubstituteFrom.Count == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.RepositoryRoot))
        {
            // The manifests that were not read may name any variable.
            _partlyRead = true;
            _warnings.Add($"{_app.DisplayName} substitutes ${{VAR}} placeholders; pass the repository checkout to link them to the manifests in {_app.Path ?? "the repository"}. Until then no variable is listed as unused.");
            return;
        }

        var root = Path.GetFullPath(options.RepositoryRoot);
        var folder = Path.Join(root, _app.Path ?? ".");
        if (!HelmChart.IsInRoot(folder, root))
        {
            _partlyRead = true;
            _warnings.Add($"The folder {_app.Path} that {_app.DisplayName} deploys is outside the repository checkout, so it was not read and no variable is listed as unused.");
            return;
        }

        folder = Path.GetFullPath(folder);

        if (!Directory.Exists(folder))
        {
            _warnings.Add($"The folder {folder} that {_app.DisplayName} deploys does not exist.");
            return;
        }

        // Links are not followed: a link to a parent folder never ends, and one out of the folder reads other files. Each
        // folder is read on its own, so one that cannot be read is a warning and the others are still read. The scan
        // stops at the limits, since the folder may hold anything.
        var limits = options.ScanLimits;
        var top = Relative(root, folder);
        var enumeration = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.None };
        var files = new List<string>();
        var links = new List<string>();
        var visited = new List<string>();
        var entries = 0;
        var tooDeep = false;
        var folders = new Stack<(string Path, int Depth)>([(folder, 0)]);
        while (folders.Count > 0)
        {
            var (current, depth) = folders.Pop();
            try
            {
                var found = new FileSystemEnumerable<(string Path, bool IsFolder, bool IsLink)>(
                    current,
                    (ref FileSystemEntry e) => (e.ToFullPath(), e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0),
                    enumeration);
                foreach (var (path, isFolder, isReparsePoint) in found)
                {
                    if (++entries > limits.Entries)
                    {
                        break;
                    }

                    if (isReparsePoint && IsLink(path))
                    {
                        // A file or folder link, or a Windows junction. Other reparse points, such as deduplicated
                        // files, are read as files and folders.
                        links.Add(path);
                    }
                    else if (!isFolder)
                    {
                        if (path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                        {
                            files.Add(path);
                        }
                    }
                    else if (depth < limits.Depth)
                    {
                        folders.Push((path, depth + 1));
                        visited.Add(path);
                    }
                    else if (!tooDeep)
                    {
                        tooDeep = true;
                        Limit(string.Create(CultureInfo.InvariantCulture, $"{Relative(root, path)} is more than {limits.Depth} folders below {top}, so it was not read"));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                NotRead($"the folder {Relative(root, current)}", ex);
            }

            if (entries > limits.Entries)
            {
                Limit(string.Create(CultureInfo.InvariantCulture, $"{top} holds more than {limits.Entries} files and folders, so the rest were not read"));
                break;
            }
        }

        // A link whose text leads to a file or folder the walk read is read there under its own name. Any other link,
        // one out of the folder, a dangling one or one to a file the walk skips, leads to a part not read. One is
        // enough to say so, and the checks stop there or past MaxLinkChecks, since each may follow a chain of links.
        if (links.Count > MaxLinkChecks)
        {
            _partlyRead = true;
            _warnings.Add(string.Create(CultureInfo.InvariantCulture, $"{_app.DisplayName}: links under {top} are not followed, and there are more than {MaxLinkChecks} to check, so no variable is listed as unused."));
        }
        else if (links.Count > 0)
        {
            var read = new HashSet<string>(files, PathComparer) { folder };
            read.UnionWith(visited);
            var realFolder = RealPathOf(folder);
            if (realFolder is null || links.Exists(l => !LeadsToRead(l, folder, realFolder, read)))
            {
                _partlyRead = true;
                _warnings.Add($"{_app.DisplayName}: links under {top} are not followed, and one leads to a file or folder the scan does not read, so no variable is listed as unused.");
            }
        }

        long bytes = 0;
        // Sorted by the path with / as the separator, so the same files come first on every system.
        foreach (var (file, relative) in files.Select(f => (f, Relative(root, f))).OrderBy(f => f.Item2, StringComparer.Ordinal))
        {
            try
            {
                // A pipe or a device has no size, and opening a pipe to read waits for a writer, so nothing of size 0 is
                // opened; an empty file holds nothing either.
                if (new FileInfo(file).Length == 0)
                {
                    continue;
                }

                using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                long length;
                try
                {
                    length = RandomAccess.GetLength(handle);
                }
                catch (NotSupportedException)
                {
                    Limit($"{relative} is not a regular file, so it was not read");
                    continue;
                }

                if (length > limits.FileBytes)
                {
                    Limit(string.Create(CultureInfo.InvariantCulture, $"{relative} is larger than {limits.FileBytes} bytes, so it was not read"));
                    continue;
                }

                bytes += length;
                if (bytes > limits.Bytes)
                {
                    Limit(string.Create(CultureInfo.InvariantCulture, $"the YAML files under {top} hold more than {limits.Bytes} bytes, so {relative} and the files after it were not read"));
                    break;
                }

                // The file may have grown since its size was taken, so the read stops past the cap too.
                if (ReadAtMost(handle, limits.FileBytes) is not { } content)
                {
                    Limit(string.Create(CultureInfo.InvariantCulture, $"{relative} is larger than {limits.FileBytes} bytes, so it was not read"));
                    continue;
                }

                using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = reader.ReadToEnd();
                _usedVariables.UnionWith(ReadVariables(text).Select(v => v.Name));
                AddTemplates(KubernetesManifests.Parse(text, relative));
            }
            catch (FormatException ex)
            {
                _warnings.Add(ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                NotRead(relative, ex);
            }
        }
    }

    // What was not read may name any variable, so from then on none is called unused.
    private void NotRead(string what, Exception ex)
    {
        _partlyRead = true;
        _warnings.Add($"Could not read {what}, so no variable of {_app.DisplayName} is listed as unused: {ex.Message}");
    }

    // Past a limit the rest may name any variable too.
    private void Limit(string what)
    {
        _partlyRead = true;
        _warnings.Add($"{_app.DisplayName}: {what} and no variable is listed as unused.");
    }

    /// <summary>The bytes of an open file, or null when it holds more than <paramref name="cap"/>; at most one byte more is read.</summary>
    internal static byte[]? ReadAtMost(SafeFileHandle handle, long cap)
    {
        using var content = new MemoryStream();
        var chunk = new byte[81920];
        while (content.Length <= cap)
        {
            var read = RandomAccess.Read(handle, chunk.AsSpan(0, (int)Math.Min(chunk.Length, cap + 1 - content.Length)), content.Length);
            if (read == 0)
            {
                return content.ToArray();
            }

            content.Write(chunk, 0, read);
        }

        return null;
    }

    // More links than this are not checked one by one: each check may follow a chain of up to 40 links.
    private const int MaxLinkChecks = 1000;

    // As Kustomization.IsInFolder compares paths.
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // LinkTarget reads the entry's own link text and does not follow it.
    private static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // not read either way, and a link is counted
        }
    }

    private static string? RealPathOf(string folder)
    {
        try
        {
            return Kustomization.RealPath(folder, anyRoot: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="link"/> leads to a file or folder in <paramref name="read"/>. A link whose own text
    /// leads out of the folder is not read, and nothing it names is looked at. A link whose text stays in the folder is
    /// then followed to its end, judging each further link's text before it is followed, so a link to another root,
    /// such as a share, is never opened; only then is the target placed in the walk.
    /// </summary>
    private static bool LeadsToRead(string link, string folder, string realFolder, HashSet<string> read)
    {
        try
        {
            // The link's own text first: when it leads out of the folder, nothing it names is looked at.
            if (new FileInfo(link).LinkTarget is not { } text)
            {
                return false;
            }

            var named = Path.GetFullPath(text, Path.GetDirectoryName(link)!);
            if (!HelmChart.IsBelowAsText(named, folder) && !HelmChart.IsBelowAsText(named, realFolder))
            {
                return false;
            }

            if (!Kustomization.IsInFolder(link, folder, orFolder: true)
                || Kustomization.RealPath(link, alsoRoot: Path.GetPathRoot(realFolder)) is not { } target)
            {
                return false;
            }

            var relative = Path.GetRelativePath(realFolder, target);
            return read.Contains(relative == "." ? folder : Path.Join(folder, relative));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Finds the ConfigMap, Secret and env values in source manifests that hold <c>${VAR}</c> placeholders.</summary>
    internal void AddTemplates(KubernetesManifests manifests)
    {
        // Objects the manifests left out, such as one whose name holds a slash, are named once. The set keeps that
        // linear however many objects a folder holds.
        _named ??= new HashSet<string>(_warnings, StringComparer.Ordinal);
        foreach (var warning in manifests.Warnings)
        {
            if (_named.Add(warning))
            {
                _warnings.Add(warning);
            }
        }

        var data = manifests.ConfigMaps.Values.Select(d => (Kind: 'c', Data: d))
            .Concat(manifests.Secrets.Values.Select(d => (Kind: 's', Data: d)))
            .SelectMany(x => x.Data.Data.Select(kv => (x.Kind, Object: x.Data.Name, Key: kv.Key, Value: kv.Value.Value, kv.Value.Line, File: x.Data.SourceName)));
        var env = manifests.Workloads
            .SelectMany(w => w.Containers.SelectMany(c => c.Env.Select(e => (Kind: 'e', Object: w.Name + "/" + c.Name, Key: e.Name, Value: (e.Source as EnvValueSource.Literal)?.Value, e.Line, File: w.SourceName))));
        var templates = data.Concat(env)
            .Where(x => x.Value is not null)
            .Select(x => (Key: (x.Kind, x.Object, x.Key), Template: new SubstitutionTemplate(x.File, x.Line, x.Value!, ReadVariables(x.Value!))))
            .Where(x => x.Template.Variables.Count > 0);
        foreach (var (key, template) in templates)
        {
            _templates.TryAdd(key, template);
        }
    }

    private List<ProvenanceStep> ResolveTemplate((char Kind, string Object, string Key) key)
    {
        if (!_templates.TryGetValue(key, out var template))
        {
            return [];
        }

        var names = template.Variables.Select(v => v.Name).Distinct(StringComparer.Ordinal).ToList();

        // With several variables the step links to none of them, so it is secret itself when a Secret fills one of them.
        var secret = key.Kind == 's' || names.Any(n => _fromSecret.GetValueOrDefault(n));
        var placeholders = string.Join(", ", names.Select(n => $"${{{n}}}"));
        var note = $"{_app.DisplayName} replaces {placeholders} after kustomize build";
        SourceRef? reference = null;
        if (names.Count == 1)
        {
            reference = new GitOpsVariableRef(_app.Name, names[0]);
            if (ResolveVariable(names[0]).Count == 0)
            {
                // Notes are not redacted, and whether the key is secret by its name is only decided after the resolvers ran,
                // so the default is never written into one. It is part of the template, this step's value.
                note += template.Variables.Any(v => v.Default is not null)
                    ? $"; no loaded substitute sets {names[0]}, so Flux uses the placeholder's default"
                    : $"; no loaded substitute sets {names[0]}, so Flux uses the placeholder's default, or an empty string";
            }
        }
        else
        {
            note += "; a value with several variables is not traced further";
        }

        var objectName = key.Kind switch
        {
            'c' => $"ConfigMap {key.Object}",
            's' => $"Secret {key.Object}",
            _ => "workload " + key.Object.Replace("/", ", container ", StringComparison.Ordinal),
        };
        return
        [
            new ProvenanceStep
            {
                Kind = LayerKind.GitOps,
                Source = template.File,
                Location = string.Create(CultureInfo.InvariantCulture, $"line {template.Line}, {objectName} {(key.Kind == 'e' ? "env" : "key")} {key.Key}"),
                Value = template.Text,
                IsSensitive = secret,
                Note = note,
                Reference = reference,
            },
        ];
    }

    // The steps carry no reference: the template step above them already points at the variable.
    private List<ProvenanceStep> ResolveVariable(string name)
    {
        var steps = new List<ProvenanceStep>();
        for (var i = 0; i < _app.SubstituteFrom.Count; i++)
        {
            var from = _app.SubstituteFrom[i];
            if (_substituteFrom[i] is { } data && data.Data.TryGetValue(name, out var value))
            {
                steps.Add(new ProvenanceStep
                {
                    Kind = LayerKind.GitOps,
                    Source = $"{_app.DisplayName} (substituteFrom {(from.IsSecret ? "Secret" : "ConfigMap")} {from.Name})",
                    Location = Yaml(data.SourceName, value.Line),
                    Value = value.Value,
                    IsSensitive = from.IsSecret,
                });
            }
        }

        // Inline substitute wins over substituteFrom.
        steps.AddRange(_app.Substitutions
            .Where(s => s.Name == name)
            .Select(s => new ProvenanceStep
            {
                Kind = LayerKind.GitOps,
                Source = $"{_app.DisplayName} (postBuild.substitute)",
                Location = Yaml(_app.SourceName, s.Line),
                Value = s.Value,
            }));
        return steps;
    }

    private static string Yaml(string file, int line) => string.Create(CultureInfo.InvariantCulture, $"{file}:{line}");

    /// <summary>
    /// The <c>${VAR}</c> placeholders in a value, as Flux reads them: a name, then anything up to the next <c>}</c>, such as
    /// <c>:=default</c>. <c>$${VAR}</c> is an escaped, literal one, and a bare <c>$VAR</c> is not one.
    /// </summary>
    /// <remarks>
    /// The search for the closing <c>}</c> only moves forward, so a long run of <c>${A</c> with no <c>}</c> is read in
    /// linear time: once no <c>}</c> is left, no placeholder can follow.
    /// </remarks>
    internal static List<(string Name, string? Default)> ReadVariables(string value)
    {
        var variables = new List<(string Name, string? Default)>();
        var close = -1;
        var start = 0;
        while ((start = value.IndexOf("${", start, StringComparison.Ordinal)) >= 0)
        {
            var name = start + 2;
            if ((start > 0 && value[start - 1] == '$') || name == value.Length || !(char.IsAsciiLetter(value[name]) || value[name] == '_'))
            {
                start++;
                continue;
            }

            var end = name + 1;
            while (end < value.Length && (char.IsAsciiLetterOrDigit(value[end]) || value[end] == '_'))
            {
                end++;
            }

            if (close < end)
            {
                close = value.IndexOf('}', end);
                if (close < 0)
                {
                    break;
                }
            }

            variables.Add((value[name..end], Default(value[end..close])));
            start = close + 1;
        }

        return variables;
    }

    private static string? Default(string op)
    {
        var trimmed = op.StartsWith(':') ? op[1..] : op;
        return trimmed.Length > 0 && trimmed[0] is '=' or '-' ? trimmed[1..] : null;
    }

    private sealed record SubstitutionTemplate(string File, int Line, string Text, List<(string Name, string? Default)> Variables);
}
