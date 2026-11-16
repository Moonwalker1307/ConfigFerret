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

        // A chart in the checkout may hold links, and Argo CD and Flux read nothing outside the checkout through them.
        // A chart folder that was passed is read as it is, like a chart for helm install. Each file is checked before
        // it is looked at, so that the warnings never tell whether something outside the checkout exists.
        var repositoryRoot = string.IsNullOrWhiteSpace(options.RepositoryRoot) ? null : options.RepositoryRoot;
        var checkout = string.IsNullOrWhiteSpace(options.ChartFolder) ? repositoryRoot : null;
        var defaults = chartFolder is null ? null : Path.Join(chartFolder, "values.yaml");
        if (defaults is not null && !(source.ValuesFilesReplaceChartValues && source.ValuesFiles.Count > 0))
        {
            if (checkout is not null && !Kustomization.IsInFolder(defaults, checkout))
            {
                chart._warnings.Add($"The chart's values.yaml for {application.DisplayName} is outside {checkout}, so it was not read.");
            }
            else if (File.Exists(defaults))
            {
                chart.AddValuesFile(defaults, "values.yaml (chart default)");
            }
        }

        foreach (var file in source.ValuesFiles)
        {
            chart.AddGitOpsValuesFile(application, source, file, chartFolder, repositoryRoot);
        }

        foreach (var layer in source.Layers)
        {
            chart.AddGitOpsLayer(application, layer, data);
        }

        if (chartFolder is not null)
        {
            if (checkout is not null && !Kustomization.IsInFolder(Path.Join(chartFolder, "templates"), checkout))
            {
                chart._warnings.Add($"The chart's templates folder for {application.DisplayName} is outside {checkout}, so it was not read.");
            }
            else
            {
                chart.AddTemplates(chartFolder);
            }
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

    /// <summary>The values paths whose effective value comes from a Secret, sorted for <see cref="HoldsPathOrBelow"/>.</summary>
    internal string[] SensitivePaths()
    {
        var paths = _history.Keys.Where(IsSensitivePath).ToArray();
        Array.Sort(paths, StringComparer.Ordinal);
        return paths;
    }

    /// <summary>
    /// True when <paramref name="path"/>, or a path below it, is in <paramref name="sortedPaths"/>. A template that
    /// renders a parent, for example with <c>toJson</c>, renders every value below it. The paths below sort right
    /// after <c>path.</c>, so one search finds them. Parents are never written out: that would cost the square of a
    /// path's length.
    /// </summary>
    internal static bool HoldsPathOrBelow(string[] sortedPaths, string path)
    {
        if (Array.BinarySearch(sortedPaths, path, StringComparer.Ordinal) >= 0)
        {
            return true;
        }

        var prefix = path + ".";
        var next = LowerBound(sortedPaths, prefix);
        return next < sortedPaths.Length && sortedPaths[next].StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// The values paths of <paramref name="paths"/> that a template names: the path itself, a parent such as
    /// <c>.Values.resources</c> in <c>toYaml</c>, or a value below it. A template that names a path reads it, whatever it
    /// renders it into.
    /// </summary>
    internal HashSet<string> NamedByTemplates(IEnumerable<string> paths)
    {
        var sorted = paths.Order(StringComparer.Ordinal).ToArray();
        var named = _named.Order(StringComparer.Ordinal).ToArray();

        // The paths below a named one sort from "name." up to "name/", as '/' follows '.'. Each named path marks that
        // range once, so nothing costs more than a search per path.
        var marks = new int[sorted.Length + 1];
        foreach (var name in named)
        {
            marks[LowerBound(sorted, name + ".")]++;
            marks[LowerBound(sorted, name + "/")]--;
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        var below = 0;
        for (var i = 0; i < sorted.Length; i++)
        {
            below += marks[i];
            if (below > 0 || HoldsPathOrBelow(named, sorted[i]))
            {
                result.Add(sorted[i]);
            }
        }

        return result;
    }

    private static int LowerBound(string[] sorted, string value)
    {
        var index = Array.BinarySearch(sorted, value, StringComparer.Ordinal);
        return index < 0 ? ~index : index;
    }

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="root"/> or in it, after following symbolic links. Paths
    /// come from the manifest, and Argo CD and Flux read nothing outside the checkout.
    /// </summary>
    /// <remarks>
    /// The text is checked first: a path that <c>..</c> takes out of the root is never looked at on disk, and text
    /// that is no path, such as one with a NUL character, is outside.
    /// </remarks>
    internal static bool IsInRoot(string path, string root) =>
        IsBelowAsText(path, root) && Kustomization.IsInFolder(path, root, orFolder: true);

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or in it as text, without looking at the disk.</summary>
    private static bool IsBelowAsText(string path, string root)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(root, path);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return !(Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

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
            folder = Path.Join(options.RepositoryRoot, source.Path);
            if (!IsInRoot(folder, options.RepositoryRoot))
            {
                warnings.Add($"The chart folder {source.Path} for {application.DisplayName} is outside the repository checkout, so it was not read.");
                return null;
            }

            folder = Path.GetFullPath(folder);
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

        // Argo CD reads a values file anywhere in the repository, also one named with .. from the chart folder. Each
        // place is checked before it is looked at, so the warnings never tell whether a file outside exists: one that
        // .. takes out of every root is dropped, and one in a root that links outside makes the file outside.
        var inText = relative.Length == 0 ? [] : roots
            .Select(r => Path.Join(r, relative))
            .Where(c => roots.Any(r => IsBelowAsText(c, r)))
            .ToList();
        var inside = inText.Where(c => roots.Any(r => IsInRoot(c, r))).ToList();
        var found = inside.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        if (found is null && relative.Length > 0 && roots.Count > 0 && (inText.Count == 0 || inside.Count < inText.Count))
        {
            _warnings.Add($"{where} names the values file {file.Path}, which is outside {string.Join(" and ", roots)}, so it was not read.");
            return;
        }

        if (found is null)
        {
            if (!source.IgnoreMissingValuesFiles)
            {
                var looked = roots.Count == 0 ? "; pass the repository checkout to read it" : $" under {string.Join(" or ", roots)}";
                _warnings.Add($"{where} names the values file {file.Path}, which was not found{looked}.");
            }

            return;
        }

        try
        {
            AddValuesFile(found, file.Path);
            _layerInfo[file.Path] = (LayerKind.Helm, false);
        }
        catch (FormatException ex)
        {
            _warnings.Add(ex.Message);
        }
    }

    private void AddGitOpsLayer(GitOpsApplication application, GitOpsValuesLayer layer, List<KubernetesManifests> data)
    {
        // A valuesFrom object read from another file names it, so its lines are not taken for lines of the manifest.
        var found = layer.Kind is GitOpsValuesKind.ConfigMap or GitOpsValuesKind.Secret ? FindValuesFrom(application, layer, data) : null;
        var file = found is not null && found.SourceName != application.SourceName ? $" in {found.SourceName}" : "";
        var name = $"{application.DisplayName} ({layer.Name}{file})";
        var sensitive = layer.Kind == GitOpsValuesKind.Secret;
        _layerInfo[name] = (LayerKind.GitOps, sensitive);
        switch (layer.Kind)
        {
            case GitOpsValuesKind.Inline when layer.Node is not null:
                Walk(layer.Node, prefix: null, name);
                break;
            case GitOpsValuesKind.Inline:
                AddGitOpsValues(name, layer.Text ?? "", line => line + layer.LineOffset);
                break;
            case GitOpsValuesKind.Parameter:
                SetValue(layer.TargetPath!, layer.ForceString ? layer.Value ?? "" : SetList(layer.Value ?? ""), name, layer.Line, canRemove: !layer.ForceString);
                break;
            default:
                AddValuesFrom(application, layer, found, name);
                break;
        }
    }

    // Flux reads valuesFrom from the HelmRelease's own namespace.
    private static KubernetesData? FindValuesFrom(GitOpsApplication application, GitOpsValuesLayer layer, List<KubernetesManifests> data) => data
        .Select(d => d.FindLoaded(layer.Kind == GitOpsValuesKind.Secret, application.Namespace, layer.ObjectName!))
        .FirstOrDefault(d => d is not null);

    private void AddValuesFrom(GitOpsApplication application, GitOpsValuesLayer layer, KubernetesData? found, string name)
    {
        var isSecret = layer.Kind == GitOpsValuesKind.Secret;
        var what = $"{(isSecret ? "Secret" : "ConfigMap")} {layer.ObjectName}";
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
            // A literal block keeps its lines, the first one below the key. Other text, such as base64 Secret data or a
            // quoted string, has no lines in the file, so every value in it is on the key's line.
            AddGitOpsValues(name, value.Value, value.IsLiteralBlock ? line => line + value.Line : _ => value.Line);
        }
    }

    private void AddGitOpsValues(string name, string yaml, Func<int, int> fileLine)
    {
        try
        {
            AddValues(name, yaml, fileLine);
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
