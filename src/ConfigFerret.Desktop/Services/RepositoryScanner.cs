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

        var chartInfos = charts.Select(chart =>
        {
            var relative = Relative(root, chart);
            var values = yamlFiles
                .Where(f => IsValuesFile(f) && !string.Equals(Path.GetFullPath(f), Path.Join(chart, "values.yaml"), StringComparison.Ordinal)
                    && !IsUnder(f, Path.Join(chart, "templates")) && !IsUnder(f, Path.Join(chart, "charts")))
                .Select(f => Relative(root, f))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new HelmChartInfo(chart, relative, values);
        }).ToList();

        var manifests = yamlFiles
            .Where(f => !charts.Any(c => IsUnder(f, c)) && !IsValuesFile(f) && LooksLikeManifest(f))
            .Select(f => Relative(root, f))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var workflowsFolder = Path.Join(root, ".github", "workflows");
        var workflows = Directory.Exists(workflowsFolder)
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
            manifests,
            workflows,
            Directory.Exists(Path.Join(root, ".octopus")));
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

    private static bool IsUnder(string path, string folder)
    {
        var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(full, StringComparison.Ordinal);
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
