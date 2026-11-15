using System.Globalization;
using ConfigFerret.GitOps;
using ConfigFerret.Kubernetes;
using ConfigFerret.Kustomize;

namespace ConfigFerret.Helm;

// Building a chart from an Argo CD Application or a Flux HelmRelease.
public sealed partial class HelmChart
{
    private readonly Dictionary<string, (LayerKind Kind, bool IsSensitive)> _layerInfo = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = [];

    /// <summary>
    /// Problems found while building the chart, such as a chart in a Helm repository that cannot be fetched or a values
    /// file that is missing. <see cref="ConfigTracer.WithHelm"/> adds them to the trace's warnings.
    /// </summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>
    /// Builds the chart values an Argo CD Application or a Flux HelmRelease deploys, merged the way the tool merges them.
    /// Argo CD: the chart's <c>values.yaml</c>, then <c>valueFiles</c> in order, then <c>values</c> or
    /// <c>valuesObject</c>, then <c>parameters</c>. Flux: the chart's <c>valuesFiles</c> (which replace its
    /// <c>values.yaml</c> when listed), then each <c>valuesFrom</c> in order, then <c>spec.values</c>.
    /// </summary>
    /// <remarks>
    /// Inline values and parameters become layers named after the object, for example
    /// <c>Argo CD Application orders-prod (helm.values)</c>. Values from a Secret are sensitive. Problems such as a chart
    /// from a remote repository are listed in <see cref="Warnings"/> instead of failing.
    /// </remarks>
    /// <param name="application">An Argo CD Application or ApplicationSet, or a Flux HelmRelease.</param>
    /// <param name="options">Where the chart, the repository checkout and the <c>valuesFrom</c> objects are.</param>
    /// <exception cref="ArgumentException">The object does not deploy a Helm chart.</exception>
    public static HelmChart ForGitOps(GitOpsApplication application, GitOpsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        options ??= new GitOpsOptions();
        return Build(application, options, new[] { application.Data, options.Data }.OfType<KubernetesManifests>().ToList());
    }

    internal static HelmChart Build(GitOpsApplication application, GitOpsOptions options, List<KubernetesManifests> data)
    {
        var source = (application.HelmSources.Count > 0 ? application.HelmSources[0] : null)
            ?? (application.Kind is GitOpsKind.ArgoCdApplication or GitOpsKind.ArgoCdApplicationSet && application.Path is not null && application.Kustomize is null
                ? new GitOpsHelmSource { Path = application.Path }
                : throw new ArgumentException($"{application.DisplayName} does not deploy a Helm chart.", nameof(application)));

        var warnings = new List<string>();
        if (application.HelmSources.Count > 1)
        {
            warnings.Add($"{application.DisplayName} has {application.HelmSources.Count} Helm sources; only the first ({source.Chart ?? source.Path}) is traced.");
        }

        var chartFolder = ChartFolderFor(application, source, options, warnings);
        var chart = new HelmChart(chartFolder);
        chart._warnings.AddRange(warnings);

        var defaults = chartFolder is null ? null : Path.Join(chartFolder, "values.yaml");
        if (defaults is not null && File.Exists(defaults) && !(source.ValuesFilesReplaceChartValues && source.ValuesFiles.Count > 0))
        {
            chart.AddValuesFile(defaults, "values.yaml (chart default)");
        }

        foreach (var file in source.ValuesFiles)
        {
            chart.AddGitOpsValuesFile(application, source, file, chartFolder, options.RepositoryRoot);
        }

        foreach (var layer in source.Layers)
        {
            chart.AddGitOpsLayer(application, layer, data);
        }

        if (chartFolder is not null)
        {
            chart.AddTemplates(chartFolder);
        }

        return chart;
    }

    /// <summary>The kind of layer a values source is: <see cref="LayerKind.GitOps"/> for values written in a GitOps object.</summary>
    internal LayerKind KindOf(string source) => _layerInfo.TryGetValue(source, out var info) ? info.Kind : LayerKind.Helm;

    /// <summary>True when the values source is a Secret.</summary>
    internal bool IsSensitiveSource(string source) => _layerInfo.TryGetValue(source, out var info) && info.IsSensitive;

    /// <summary>True when the values source is one a GitOps object named, rather than the chart's own defaults.</summary>
    internal bool IsGitOpsSource(string source) => _layerInfo.ContainsKey(source);

    /// <summary>True when the effective value at <paramref name="path"/> comes from a Secret.</summary>
    internal bool IsSensitivePath(string path) =>
        _history.TryGetValue(path, out var steps) && steps.Count > 0 && IsSensitiveSource(steps[^1].Source);

    private static string? ChartFolderFor(GitOpsApplication application, GitOpsHelmSource source, GitOpsOptions options, List<string> warnings)
    {
        string? folder;
        if (!string.IsNullOrWhiteSpace(options.ChartFolder))
        {
            folder = Path.GetFullPath(options.ChartFolder);
        }
        else if (source.IsRemote)
        {
            warnings.Add($"{application.DisplayName} deploys chart {source.Chart ?? "(unnamed)"} from {source.RepoUrl ?? "a remote repository"}, which ConfigFerret cannot fetch. Pull it (helm pull --untar) and pass its folder as the chart folder to trace the chart's templates and defaults.");
            return null;
        }
        else if (!string.IsNullOrWhiteSpace(options.RepositoryRoot) && source.Path is not null)
        {
            folder = Path.GetFullPath(Path.Join(options.RepositoryRoot, source.Path));
        }
        else
        {
            warnings.Add($"{application.DisplayName} deploys the chart in {source.Path ?? "its source path"}; pass the repository checkout or the chart folder to trace the chart's templates and defaults.");
            return null;
        }

        if (!Directory.Exists(folder))
        {
            warnings.Add($"The chart folder {folder} for {application.DisplayName} does not exist.");
            return null;
        }

        return folder;
    }

    private void AddGitOpsValuesFile(GitOpsApplication application, GitOpsHelmSource source, GitOpsValuesFile file, string? chartFolder, string? repositoryRoot)
    {
        var where = string.Create(CultureInfo.InvariantCulture, $"{application.DisplayName} (line {file.Line})");
        if (file.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || file.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _warnings.Add($"{where} names the remote values file {file.Path}, which ConfigFerret does not fetch.");
            return;
        }

        List<string> roots;
        var relative = file.Path;
        if (relative.StartsWith('$'))
        {
            // Argo CD multi-source: $ref/path is a file in the source with that ref. It is looked up in the repository checkout.
            var slash = relative.IndexOf('/', StringComparison.Ordinal);
            relative = slash < 0 ? "" : relative[(slash + 1)..];
            roots = repositoryRoot is null ? [] : [repositoryRoot];
        }
        else
        {
            roots = new[] { chartFolder, repositoryRoot }.OfType<string>().ToList();
        }

        var found = roots
            .Select(r => (Root: Path.GetFullPath(r), File: Path.GetFullPath(Path.Join(r, relative))))
            .FirstOrDefault(c => relative.Length > 0 && File.Exists(c.File));
        if (found.File is null)
        {
            if (!source.IgnoreMissingValuesFiles)
            {
                var looked = roots.Count == 0 ? "; pass the repository checkout to read it" : $" under {string.Join(" or ", roots)}";
                _warnings.Add($"{where} names the values file {file.Path}, which was not found{looked}.");
            }

            return;
        }

        if (!Kustomization.IsInFolder(found.File, found.Root))
        {
            _warnings.Add($"{where} names the values file {file.Path}, which is outside {found.Root}, so it was not read.");
            return;
        }

        try
        {
            AddValuesFile(found.File, file.Path);
            _layerInfo[file.Path] = (LayerKind.Helm, false);
        }
        catch (FormatException ex)
        {
            _warnings.Add(ex.Message);
        }
    }

    private void AddGitOpsLayer(GitOpsApplication application, GitOpsValuesLayer layer, List<KubernetesManifests> data)
    {
        var name = $"{application.DisplayName} ({layer.Name})";
        var sensitive = layer.Kind == GitOpsValuesKind.Secret;
        _layerInfo[name] = (LayerKind.GitOps, sensitive);
        switch (layer.Kind)
        {
            case GitOpsValuesKind.Inline when layer.Node is not null:
                Walk(layer.Node, prefix: null, name);
                break;
            case GitOpsValuesKind.Inline:
                AddGitOpsValues(name, layer.Text ?? "", layer.LineOffset);
                break;
            case GitOpsValuesKind.Parameter:
                SetValue(layer.TargetPath!, layer.ForceString ? layer.Value ?? "" : SetList(layer.Value ?? ""), name, layer.Line, canRemove: !layer.ForceString);
                break;
            default:
                AddValuesFrom(application, layer, name, data);
                break;
        }
    }

    private void AddValuesFrom(GitOpsApplication application, GitOpsValuesLayer layer, string name, List<KubernetesManifests> data)
    {
        var isSecret = layer.Kind == GitOpsValuesKind.Secret;
        var what = $"{(isSecret ? "Secret" : "ConfigMap")} {layer.ObjectName}";
        var found = data
            .Select(d => (isSecret ? d.Secrets : d.ConfigMaps).GetValueOrDefault(layer.ObjectName!))
            .FirstOrDefault(d => d is not null);
        if (found is null)
        {
            if (!layer.Optional)
            {
                _warnings.Add($"{application.DisplayName} reads valuesFrom {what}, which was not loaded; pass it with the GitOps data manifests.");
            }

            return;
        }

        if (!found.Data.TryGetValue(layer.Key!, out var value) || value.Value is null)
        {
            if (!layer.Optional)
            {
                _warnings.Add($"{application.DisplayName} reads key {layer.Key} of valuesFrom {what}, which has no such key with a text value.");
            }

            return;
        }

        if (layer.TargetPath is not null)
        {
            SetValue(layer.TargetPath, value.Value, name, value.Line, canRemove: false);
        }
        else
        {
            AddGitOpsValues(name, value.Value, value.Line);
        }
    }

    private void AddGitOpsValues(string name, string yaml, int lineOffset)
    {
        try
        {
            AddValues(name, yaml, lineOffset);
        }
        catch (FormatException ex)
        {
            // The parser's message never holds the values themselves, only where the YAML broke.
            _warnings.Add(ex.Message);
        }
    }

    private void SetValue(string path, string value, string source, int line, bool canRemove)
    {
        if (canRemove && value == "null")
        {
            Remove(path, source, line);
            return;
        }

        RemoveDescendants(path, source, line);
        Record(path, new HelmValueStep(source, line, value, IsRemoval: false));
    }
}
