using System.Collections;
using System.Text.RegularExpressions;

namespace ConfigFerret.Desktop.Services;

/// <summary>An app folder: one that holds appsettings.json.</summary>
/// <param name="Folder">The full path.</param>
/// <param name="RelativePath">The path from the repository root, "." for the root.</param>
/// <param name="Environments">The environments with their own appsettings.{Environment}.json, sorted.</param>
public sealed record AppFolder(string Folder, string RelativePath, IReadOnlyList<string> Environments)
{
    /// <inheritdoc />
    public override string ToString() => RelativePath;
}

/// <summary>A Helm chart: a folder with Chart.yaml.</summary>
/// <param name="Folder">The full path.</param>
/// <param name="RelativePath">The path from the repository root.</param>
/// <param name="ValuesFiles">Values files next to the chart or elsewhere in the repository, other than the chart's own values.yaml, as paths from the root.</param>
public sealed record HelmChartInfo(string Folder, string RelativePath, IReadOnlyList<string> ValuesFiles)
{
    /// <inheritdoc />
    public override string ToString() => RelativePath;
}

/// <summary>What a repository holds that ConfigFerret can trace.</summary>
/// <param name="Root">The repository folder.</param>
/// <param name="Apps">Folders with appsettings.json.</param>
/// <param name="HelmCharts">Helm charts.</param>
/// <param name="Kustomizations">Folders with kustomization.yaml, as paths from the root. Overlays come before bases.</param>
/// <param name="Manifests">Plain Kubernetes manifest files (not Helm templates), as paths from the root.</param>
/// <param name="Workflows">GitHub Actions workflow files, as paths from the root.</param>
/// <param name="HasOctopusConfigAsCode">True when the repository has an .octopus folder (Octopus config as code).</param>
public sealed record RepositoryScan(
    string Root,
    IReadOnlyList<AppFolder> Apps,
    IReadOnlyList<HelmChartInfo> HelmCharts,
    IReadOnlyList<string> Kustomizations,
    IReadOnlyList<string> Manifests,
    IReadOnlyList<string> Workflows,
    bool HasOctopusConfigAsCode)
{
    /// <summary>The full path of a path from the root.</summary>
    public string Full(string relativePath) => Path.GetFullPath(Path.Join(Root, relativePath));
}

/// <summary>Finds the configuration in a repository folder: app settings, Helm charts, Kustomize, manifests and workflows.</summary>
public static partial class RepositoryScanner
{
    private const int MaxDepth = 12;
    private const long MaxManifestBytes = 2 * 1024 * 1024;

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", "packages", ".vs", ".idea", "TestResults", "artifacts", "dist", "vendor",
    };

    /// <summary>Scans <paramref name="root"/>.</summary>
    public static RepositoryScan Scan(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"{root} does not exist.");
        }

        var apps = new List<AppFolder>();
        var charts = new List<string>();
        var kustomizations = new List<string>();
        var yamlFiles = new List<string>();
        Walk(root, root, 0, apps, charts, kustomizations, yamlFiles);

        // Each file is looked at once, and its folders up to the root are looked up among the chart folders, so the work
        // grows with the files, not with charts times files. Every chart is offered every values file but the ones in its
        // own folder that are part of it, so the charts share one list and each keeps only what it leaves out. The chart
        // folders and each file's folders come from the same listing, so they are spelled alike and compared exactly.
        var chartIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < charts.Count; i++)
        {
            chartIndex.TryAdd(Path.TrimEndingDirectorySeparator(charts[i]), i);
        }

        var rootFolder = Path.TrimEndingDirectorySeparator(root);
        var values = new List<string>();
        var leftOut = new List<int>?[charts.Count];
        var manifests = new List<string>();
        foreach (var file in yamlFiles)
        {
            var isValues = IsValuesFile(file);
            var inChart = false;
            string? below = null;
            for (var folder = Path.GetDirectoryName(file); folder is not null; below = folder, folder = Path.GetDirectoryName(folder))
            {
                if (chartIndex.TryGetValue(folder, out var chart))
                {
                    inChart = true;
                    if (isValues && IsPartOfChart(file, below))
                    {
                        (leftOut[chart] ??= []).Add(values.Count);
                    }
                }

                if (string.Equals(folder, rootFolder, StringComparison.Ordinal))
                {
                    break;
                }
            }

            if (isValues)
            {
                values.Add(Relative(root, file));
            }
            else if (!inChart && LooksLikeManifest(file))
            {
                manifests.Add(Relative(root, file));
            }
        }

        // A stable sort, so each chart's files keep the order they had when every chart sorted its own list.
        var order = Enumerable.Range(0, values.Count).OrderBy(i => values[i], StringComparer.OrdinalIgnoreCase).ToArray();
        var sorted = Array.ConvertAll(order, i => values[i]);
        var position = new int[order.Length];
        for (var i = 0; i < order.Length; i++)
        {
            position[order[i]] = i;
        }

        var chartInfos = charts.Select((chart, i) => new HelmChartInfo(
            chart,
            Relative(root, chart),
            new SharedValuesFiles(sorted, leftOut[i] is { } left ? [.. left.Select(v => position[v]).Order()] : []))).ToList();

        // As in Walk, a linked folder is not followed, and its link is checked before anything looks at its target.
        var workflowsFolder = Path.Join(root, ".github", "workflows");
        var workflows = !IsLink(Path.Join(root, ".github")) && !IsLink(workflowsFolder) && Directory.Exists(workflowsFolder)
            ? Directory.EnumerateFiles(workflowsFolder)
                .Where(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                .Select(f => Relative(root, f))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        return new RepositoryScan(
            root,
            apps.OrderBy(a => a.RelativePath.Count(c => c == '/')).ThenBy(a => a.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
            chartInfos.OrderBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
            kustomizations.Select(k => Relative(root, k)).OrderBy(k => k.Contains("overlay", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
            manifests.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            workflows,
            !IsLink(Path.Join(root, ".octopus")) && Directory.Exists(Path.Join(root, ".octopus")));
    }

    private static void Walk(string root, string folder, int depth, List<AppFolder> apps, List<string> charts, List<string> kustomizations, List<string> yamlFiles)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        string[] files;
        string[] folders;
        try
        {
            files = Directory.GetFiles(folder);
            folders = Directory.GetDirectories(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var names = files.Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Contains("appsettings.json"))
        {
            var environments = names
                .Select(n => AppSettingsEnvironment().Match(n))
                .Where(m => m.Success)
                .Select(m => m.Groups["env"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            apps.Add(new AppFolder(folder, Relative(root, folder), environments));
        }

        if (names.Contains("Chart.yaml"))
        {
            charts.Add(folder);
        }

        if (names.Contains("kustomization.yaml") || names.Contains("kustomization.yml") || names.Contains("Kustomization"))
        {
            kustomizations.Add(folder);
        }

        yamlFiles.AddRange(files.Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)));

        foreach (var child in folders.Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(child);
            if (SkippedFolders.Contains(name) || (name.StartsWith('.') && name is not ".github" and not ".octopus" and not ".deploy"))
            {
                continue;
            }

            // Don't follow links: a repository's link could point anywhere on this machine.
            if (new DirectoryInfo(child).LinkTarget is not null)
            {
                continue;
            }

            Walk(root, child, depth + 1, apps, charts, kustomizations, yamlFiles);
        }
    }

    private static bool IsLink(string path) => new DirectoryInfo(path).LinkTarget is not null;

    private static bool IsValuesFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("values", StringComparison.OrdinalIgnoreCase) && (name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeManifest(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxManifestBytes || info.LinkTarget is not null)
            {
                return false;
            }

            var text = File.ReadAllText(path);
            return text.Contains("apiVersion:", StringComparison.Ordinal)
                && ManifestKind().IsMatch(text)
                && !text.Contains("{{", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // A chart's own values.yaml, or a values file in its templates or charts folder (a subchart's). below is the folder
    // inside the chart that holds the file, or null for a file in the chart's folder itself. On Windows and macOS the
    // names match regardless of case, the way the chart loader, which opens them by path, finds them on those systems.
    private static bool IsPartOfChart(string file, string? below)
    {
        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return below is null
            ? comparer.Equals(Path.GetFileName(file), "values.yaml")
            : Path.GetFileName(below) is var name && (comparer.Equals(name, "templates") || comparer.Equals(name, "charts"));
    }

    // Paths from the root use "/" everywhere. Only the system's own separator is replaced: on Linux and macOS a "\" is
    // part of a file name, and turned into "/" it would point somewhere else, even outside the repository.
    private static string Relative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        return relative.Length == 0 ? "." : relative;
    }

    [GeneratedRegex(@"^appsettings\.(?<env>[^.]+)\.json$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AppSettingsEnvironment();

    [GeneratedRegex(@"^kind:\s*(Deployment|StatefulSet|DaemonSet|Pod|Job|CronJob|ReplicaSet|ConfigMap|Secret|List)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ManifestKind();
}

/// <summary>
/// The values files offered to one chart: the repository's sorted list, which every chart shares, less the positions of
/// the files that are part of this chart.
/// </summary>
internal sealed class SharedValuesFiles(string[] all, int[] leftOut) : IReadOnlyList<string>
{
    /// <inheritdoc />
    public int Count => all.Length - leftOut.Length;

    /// <inheritdoc />
    public string this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

            // The item sits at index plus the number of left-out positions before it: the first k where leftOut[k] lies
            // past index + k. leftOut is sorted and has no repeats, so that holds from k on once it holds for k.
            int low = 0, high = leftOut.Length;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (leftOut[middle] <= index + middle)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return all[index + low];
        }
    }

    /// <inheritdoc />
    public IEnumerator<string> GetEnumerator()
    {
        var next = 0;
        for (var i = 0; i < all.Length; i++)
        {
            if (next < leftOut.Length && leftOut[next] == i)
            {
                next++;
                continue;
            }

            yield return all[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
