using System.Buffers;
using System.Globalization;
using System.Text.RegularExpressions;
using ConfigFerret.Helm;
using ConfigFerret.Kubernetes;
using ConfigFerret.Kustomize;
using ConfigFerret.Sources;

namespace ConfigFerret.GitOps;

/// <summary>
/// Links layers to what a GitOps object adds: the Helm values of an Argo CD Application or Flux HelmRelease, the
/// ConfigMap keys an Argo CD kustomize patch sets, and the <c>${VAR}</c> placeholders a Flux Kustomization substitutes.
/// </summary>
internal sealed partial class GitOpsResolver : IOriginResolver
{
    private static readonly SearchValues<char> KustomizeHashAlphabet = SearchValues.Create("2456789bcdfghkmt"); // the characters of the hash kustomize appends to a generated name

    private readonly GitOpsApplication _app;
    private readonly HelmChart? _chart;
    private readonly HelmResolver? _helm;
    private readonly List<string> _warnings = [];
    private HashSet<string>? _named; // the warnings given so far, so that a skipped object is named once
    private readonly List<KubernetesData?> _substituteFrom = [];
    private readonly Dictionary<(char Kind, string Object, string Key), SubstitutionTemplate> _templates = [];
    private readonly Dictionary<string, bool> _fromSecret = new(StringComparer.Ordinal); // whether the value that wins for a variable is from a Secret
    private readonly List<KubernetesManifests> _data;
    private bool _warned;

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

    /// <summary>Everything the object sets, so unused entries can be listed.</summary>
    public List<SourceEntry> Entries()
    {
        var entries = new List<SourceEntry>();
        if (_chart is not null)
        {
            entries.AddRange(_chart.History
                .Where(h => h.Value.Any(s => _chart.IsGitOpsSource(s.Source)))
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
        var dash = rendered.LastIndexOf('-');
        var hashed = dash > 0 && rendered.Length - dash - 1 == 10 && rendered.AsSpan(dash + 1).IndexOfAnyExcept(KustomizeHashAlphabet) < 0;
        var unhashed = hashed ? rendered[..dash] : rendered;
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
            _warnings.Add($"{_app.DisplayName} substitutes ${{VAR}} placeholders; pass the repository checkout to link them to the manifests in {_app.Path ?? "the repository"}.");
            return;
        }

        var root = Path.GetFullPath(options.RepositoryRoot);
        var folder = Path.Join(root, _app.Path ?? ".");
        if (!HelmChart.IsInRoot(folder, root))
        {
            _warnings.Add($"The folder {_app.Path} that {_app.DisplayName} deploys is outside the repository checkout, so it was not read.");
            return;
        }

        folder = Path.GetFullPath(folder);

        if (!Directory.Exists(folder))
        {
            _warnings.Add($"The folder {folder} that {_app.DisplayName} deploys does not exist.");
            return;
        }

        // Links are skipped: a link to a parent folder never ends, and one out of the folder reads other files.
        var files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            try
            {
                AddTemplates(KubernetesManifests.Parse(File.ReadAllText(file), relative));
            }
            catch (FormatException ex)
            {
                _warnings.Add(ex.Message);
            }
        }
    }

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

    /// <summary>The <c>${VAR}</c> placeholders in a value, as Flux reads them. <c>$${VAR}</c> is an escaped, literal one.</summary>
    internal static List<(string Name, string? Default)> ReadVariables(string value) =>
        Placeholder().Matches(value)
            .Select(m => (m.Groups["name"].Value, Default(m.Groups["op"].Value)))
            .ToList();

    private static string? Default(string op)
    {
        var trimmed = op.StartsWith(':') ? op[1..] : op;
        return trimmed.Length > 0 && trimmed[0] is '=' or '-' ? trimmed[1..] : null;
    }

    [GeneratedRegex(@"(?<!\$)\$\{(?<name>[_a-zA-Z][_a-zA-Z0-9]*)(?<op>[^}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    private sealed record SubstitutionTemplate(string File, int Line, string Text, List<(string Name, string? Default)> Variables);
}
