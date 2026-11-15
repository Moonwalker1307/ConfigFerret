using System.Globalization;
using System.Text.RegularExpressions;
using ConfigFerret.Helm;
using ConfigFerret.Kubernetes;
using ConfigFerret.Sources;

namespace ConfigFerret.GitOps;

/// <summary>
/// Links layers to what a GitOps object adds: the Helm values of an Argo CD Application or Flux HelmRelease, the
/// ConfigMap keys an Argo CD kustomize patch sets, and the <c>${VAR}</c> placeholders a Flux Kustomization substitutes.
/// </summary>
internal sealed partial class GitOpsResolver : IOriginResolver
{
    private readonly GitOpsApplication _app;
    private readonly HelmChart? _chart;
    private readonly HelmResolver? _helm;
    private readonly List<string> _warnings = [];
    private readonly List<KubernetesData?> _substituteFrom = [];
    private readonly Dictionary<(char Kind, string Object, string Key), SubstitutionTemplate> _templates = [];
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
        if (HasChart(application, options))
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

    private static bool HasChart(GitOpsApplication application, GitOpsOptions options)
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
        return folder is not null && File.Exists(Path.Join(folder, "Chart.yaml"));
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

    /// <summary>True when <paramref name="rendered"/> is <paramref name="name"/>, with the source's prefix and suffix, and with or without kustomize's hash.</summary>
    private static bool NameMatches(string name, string rendered, GitOpsKustomize kustomize)
    {
        var full = (kustomize.NamePrefix ?? "") + name + (kustomize.NameSuffix ?? "");
        var dash = rendered.LastIndexOf('-');
        var unhashed = dash > 0 && rendered.Length - dash - 1 == 10 ? rendered[..dash] : rendered;
        return rendered == name || rendered == full || unhashed == name || unhashed == full;
    }

    private void LoadSubstitutions(GitOpsOptions options)
    {
        foreach (var from in _app.SubstituteFrom)
        {
            var data = _data
                .Select(d => (from.IsSecret ? d.Secrets : d.ConfigMaps).GetValueOrDefault(from.Name))
                .FirstOrDefault(d => d is not null);
            if (data is null && !from.Optional)
            {
                _warnings.Add($"{_app.DisplayName} reads substituteFrom {(from.IsSecret ? "Secret" : "ConfigMap")} {from.Name}, which was not loaded; pass it with the GitOps data manifests.");
            }

            _substituteFrom.Add(data);
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
        var folder = Path.GetFullPath(Path.Join(root, _app.Path ?? "."));
        if (!Directory.Exists(folder))
        {
            _warnings.Add($"The folder {folder} that {_app.DisplayName} deploys does not exist.");
            return;
        }

        var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
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

        var secret = key.Kind == 's';
        var names = template.Variables.Select(v => v.Name).Distinct(StringComparer.Ordinal).ToList();
        var placeholders = string.Join(", ", names.Select(n => $"${{{n}}}"));
        var note = $"{_app.DisplayName} replaces {placeholders} after kustomize build";
        SourceRef? reference = null;
        if (names.Count == 1)
        {
            reference = new GitOpsVariableRef(_app.Name, names[0]);
            if (ResolveVariable(names[0]).Count == 0)
            {
                var fallback = template.Variables.Select(v => v.Default).FirstOrDefault(d => d is not null);
                note += fallback is null || secret
                    ? $"; no loaded substitute sets {names[0]}, so Flux uses the placeholder's default, or an empty string"
                    : $"; no loaded substitute sets {names[0]}, so Flux uses the default '{fallback}'";
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
