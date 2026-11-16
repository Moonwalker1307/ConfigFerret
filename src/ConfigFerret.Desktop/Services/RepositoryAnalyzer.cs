using System.ComponentModel;
using System.Diagnostics;
using ConfigFerret.Desktop.Model;
using ConfigFerret.Kubernetes;

namespace ConfigFerret.Desktop.Services;

/// <summary>The choices the user makes for one repository: which app, which environment, which values file, which Octopus project.</summary>
public sealed class AnalysisChoices
{
    /// <summary>The app folder, as a path from the repository root.</summary>
    public string? App { get; set; }

    /// <summary>The hosting environment, for example Production.</summary>
    public string Environment { get; set; } = "Production";

    /// <summary>The Helm chart, as a path from the repository root.</summary>
    public string? HelmChart { get; set; }

    /// <summary>The release's values file, as a path from the repository root, on top of the chart's values.yaml.</summary>
    public string? HelmValues { get; set; }

    /// <summary>The Kustomize folder, as a path from the repository root.</summary>
    public string? Kustomization { get; set; }

    /// <summary>The GitHub deployment environment, or null for repository and organization variables only.</summary>
    public string? GitHubEnvironment { get; set; }

    /// <summary>The Octopus project, or null to leave Octopus out.</summary>
    public string? OctopusProject { get; set; }

    /// <summary>The Octopus environment, or null to match any.</summary>
    public string? OctopusEnvironment { get; set; }

    /// <summary>The kinds of source turned off in the Sources drop-down, such as "Helm chart". The trace leaves them out.</summary>
    public List<string> TurnedOff { get; set; } = [];
}

/// <summary>The account connections a repository analysis can use.</summary>
/// <param name="GitHubRepository">owner/name of the repository whose Actions variables to read, or null for a local folder.</param>
/// <param name="GitHubApiUrl">The GitHub API address, or empty for github.com.</param>
/// <param name="GitHubCredentialAccount">The credential store account that holds the GitHub token.</param>
/// <param name="OctopusServer">The Octopus server, or empty when Octopus is not connected.</param>
/// <param name="OctopusSpace">The Octopus space, or empty for the default.</param>
/// <param name="OctopusCredentialAccount">The credential store account that holds the Octopus API key.</param>
public sealed record AnalysisConnections(
    string? GitHubRepository,
    string GitHubApiUrl,
    string GitHubCredentialAccount,
    string OctopusServer,
    string OctopusSpace,
    string OctopusCredentialAccount);

/// <summary>
/// Turns a scanned repository and the user's choices into a <see cref="Profile"/> for <see cref="TraceRunner"/>. When
/// <c>helm</c> or <c>kubectl</c> is installed it renders the chart or overlay first, so values link all the way to the
/// app's settings; the tools run without a shell, without network access and without this process's other environment
/// variables (see <see cref="RunTool"/>).
/// </summary>
public sealed partial class RepositoryAnalyzer
{
    private readonly Func<string, IReadOnlyList<string>, string?> _render;

    /// <summary>Creates an analyzer.</summary>
    /// <param name="render">Runs a tool and returns its output, or null when the tool is not installed or fails. Defaults to running the real process.</param>
    public RepositoryAnalyzer(Func<string, IReadOnlyList<string>, string?>? render = null) => _render = render ?? RunTool;

    /// <summary>Fills in choices the user hasn't made, from what the scan found.</summary>
    public static void SuggestChoices(RepositoryScan scan, AnalysisChoices choices, IReadOnlyList<string> gitHubEnvironments)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(gitHubEnvironments);
        var app = scan.Apps.FirstOrDefault(a => a.RelativePath == choices.App) ?? (scan.Apps.Count > 0 ? scan.Apps[0] : null);
        choices.App = app?.RelativePath;
        if (app is not null && !app.Environments.Contains(choices.Environment, StringComparer.OrdinalIgnoreCase))
        {
            choices.Environment = app.Environments.FirstOrDefault(e => e.Equals("Production", StringComparison.OrdinalIgnoreCase))
                ?? app.Environments.FirstOrDefault(e => !e.Equals("Development", StringComparison.OrdinalIgnoreCase))
                ?? choices.Environment;
        }

        SuggestDeployment(scan, choices);
        if (choices.GitHubEnvironment is null || !gitHubEnvironments.Contains(choices.GitHubEnvironment))
        {
            choices.GitHubEnvironment = MatchEnvironment(gitHubEnvironments, choices.Environment, e => e);
        }
    }

    /// <summary>
    /// Fills in the chart, its values file and the Kustomize folder for the chosen app and environment. A repository can
    /// hold a chart and an overlay for each of its apps, so the ones named most like the app win, and a choice that one of
    /// those beats is replaced. Values files inside or next to the chart come before the rest. A Kustomize folder for the
    /// environment comes first, among those named like the app and else among all, since overlays are often shared by
    /// the apps and only their bases are named like one. A name holding the whole environment name beats one that only
    /// holds its short form, which can be part of an app's name, as prod is of products.
    /// </summary>
    internal static void SuggestDeployment(RepositoryScan scan, AnalysisChoices choices)
    {
        // An app at the root is named after the folder the repository was downloaded to, which ends in a random id.
        var app = scan.Apps.FirstOrDefault(a => a.RelativePath == choices.App);
        var appWords = app is null || app.RelativePath == "." ? [] : Words(Path.GetFileName(app.Folder));
        var charts = BestNamed(scan.HelmCharts, c => c.RelativePath, appWords);
        var chart = charts.FirstOrDefault(c => c.RelativePath == choices.HelmChart) ?? charts.FirstOrDefault();
        var chartChanged = choices.HelmChart is not null && chart?.RelativePath != choices.HelmChart;
        choices.HelmChart = chart?.RelativePath;
        if (chart is not null && (chartChanged || choices.HelmValues is null || !chart.ValuesFiles.Contains(choices.HelmValues)))
        {
            var near = chart.ValuesFiles.Where(f => IsNextToChart(chart.RelativePath, f)).ToList();
            choices.HelmValues = MatchEnvironment(choices.Environment, f => Path.GetFileNameWithoutExtension(f), near, chart.ValuesFiles.Except(near));
        }

        var kustomizations = BestNamed(scan.Kustomizations, k => k, appWords);
        var match = MatchEnvironment(choices.Environment, k => k, kustomizations, scan.Kustomizations);
        var forApp = match is not null && kustomizations.Contains(match) ? match : null;
        if (choices.Kustomization is null || !scan.Kustomizations.Contains(choices.Kustomization) || (forApp is not null && !kustomizations.Contains(choices.Kustomization)))
        {
            choices.Kustomization = match ?? kustomizations.FirstOrDefault();
        }
    }

    // The items whose path shares the most words with the app's folder name, such as charts/orders for src/Orders.Api, in
    // their order. Whole words count, so that k8s/base is not taken for an app named Database.
    private static List<T> BestNamed<T>(IReadOnlyList<T> items, Func<T, string> path, HashSet<string> appWords)
    {
        int Score(T item) => Words(path(item)).Count(appWords.Contains);
        var best = items.Count == 0 ? 0 : items.Max(Score);
        return items.Where(i => Score(i) == best).ToList();
    }

    // A values file inside the chart's folder or in the folder that holds it, such as deploy/values-prod.yaml for deploy/chart.
    private static bool IsNextToChart(string chart, string file)
    {
        static string Folder(string path) => path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : ".";
        return chart == "." || file.StartsWith(chart + "/", StringComparison.Ordinal) || Folder(file) == Folder(chart);
    }

    private static HashSet<string> Words(string name) =>
        new(NameWord().Matches(name).Select(m => m.Value.ToLowerInvariant()).Where(w => w.Length > 1), StringComparer.Ordinal);

    // Words of a name, PascalCase ones too: OrdersApi and orders-api both give orders and api.
    [System.Text.RegularExpressions.GeneratedRegex("[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex NameWord();

    /// <summary>Builds the profile to trace.</summary>
    /// <param name="scan">The scanned repository.</param>
    /// <param name="choices">What to trace.</param>
    /// <param name="connections">The GitHub and Octopus connections.</param>
    /// <param name="notes">Notes for the user, such as a chart that could not be rendered.</param>
    public Profile BuildProfile(RepositoryScan scan, AnalysisChoices choices, AnalysisConnections connections, List<string> notes)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(notes);
        var profile = new Profile { Name = Path.GetFileName(scan.Root) };
        var app = scan.Apps.FirstOrDefault(a => a.RelativePath == choices.App);

        // Rendered manifests: a Helm release, else a Kustomize overlay, else plain manifests in the repository. Rendered
        // ones stay in memory and are never written to disk: they can hold secrets from the chart's values.
        (string Name, string Text)? rendered = null;
        HelmChartInfo? chart = scan.HelmCharts.FirstOrDefault(c => c.RelativePath == choices.HelmChart);
        if (chart is not null)
        {
            if (scan.HelmCharts.Count > 1)
            {
                notes.Add($"The repository holds {scan.HelmCharts.Count} Helm charts; traced {chart.RelativePath}.");
            }

            var arguments = new List<string> { "template", "release", chart.Folder };
            var valuesFiles = new List<string>();
            if (choices.HelmValues is not null)
            {
                valuesFiles.Add(scan.Full(choices.HelmValues));
                arguments.AddRange(["-f", scan.Full(choices.HelmValues)]);
            }

            if (_render("helm", arguments) is { } output)
            {
                rendered = ("helm-template.yaml", output);
            }
            else
            {
                notes.Add("Install helm to render the chart, so its values link to the app's settings. Without it the chart's values are listed only where a ConfigMap or env entry in the repository uses them.");
            }

            profile.Sources.Add(new HelmSource { Name = $"Helm chart {chart.RelativePath}", ChartFolder = chart.Folder, ValuesFiles = valuesFiles });
        }

        var kustomization = choices.Kustomization;
        if (kustomization is not null)
        {
            if (rendered is null)
            {
                if ((_render("kubectl", ["kustomize", scan.Full(kustomization)]) ?? _render("kustomize", ["build", scan.Full(kustomization)])) is { } output)
                {
                    rendered = ("kustomize-build.yaml", output);
                }
                else
                {
                    notes.Add("Install kubectl or kustomize to build the overlay, so its generators link to the app's settings.");
                }
            }

            profile.Sources.Add(new KustomizeSource { Name = $"Kustomize {kustomization}", Folder = scan.Full(kustomization) });
        }

        var manifestPaths = rendered is not null ? [] : scan.Manifests.Select(scan.Full).ToList();
        var prefixes = new List<string>();
        var hasManifests = rendered is not null || manifestPaths.Count > 0;
        if (hasManifests)
        {
            var kubernetes = new KubernetesSource { Name = rendered is null ? "Kubernetes manifests" : "Rendered manifests", Paths = manifestPaths, Rendered = rendered };
            PickContainer(kubernetes, scan, app, prefixes, notes);
            profile.Sources.Insert(0, kubernetes);
        }

        if (app is not null && prefixes.Count == 0)
        {
            prefixes.AddRange(FindPrefixesInCode(app.Folder));
        }

        if (app is not null)
        {
            profile.Mode = TraceMode.App;
            profile.Sources.Insert(0, new AppSettingsSource
            {
                Name = $"App settings {app.RelativePath}",
                Folder = app.Folder,
                EnvironmentName = choices.Environment,
                UseUserSecrets = false,
                EnvironmentPrefixes = prefixes,
            });
        }
        else if (hasManifests)
        {
            profile.Mode = TraceMode.Container;
            notes.Add("No appsettings.json was found, so the container's environment variables and files are traced from the manifests.");
        }

        if (connections.GitHubRepository is not null)
        {
            profile.Sources.Add(new GitHubSource
            {
                Name = "GitHub Actions variables",
                Repository = connections.GitHubRepository,
                Environment = choices.GitHubEnvironment ?? string.Empty,
                ApiUrl = connections.GitHubApiUrl,
                Workflows = scan.Workflows.Select(scan.Full).ToList(),
                CredentialAccountName = connections.GitHubCredentialAccount,
            });
        }

        if (!string.IsNullOrWhiteSpace(connections.OctopusServer) && !string.IsNullOrWhiteSpace(choices.OctopusProject))
        {
            profile.Sources.Add(new OctopusSource
            {
                Name = $"Octopus project {choices.OctopusProject}",
                Server = connections.OctopusServer,
                Space = connections.OctopusSpace,
                Project = choices.OctopusProject,
                Environment = choices.OctopusEnvironment ?? string.Empty,
                CredentialAccountName = connections.OctopusCredentialAccount,
            });
        }

        return profile;
    }

    // The manifests usually hold one app workload, maybe with sidecars. Pick the workload and container named most
    // like the app, and the env prefixes its envFrom entries use, which the app strips with AddEnvironmentVariables(prefix).
    private static void PickContainer(KubernetesSource source, RepositoryScan scan, AppFolder? app, List<string> prefixes, List<string> notes)
    {
        KubernetesManifests manifests;
        try
        {
            manifests = source.Rendered is { } rendered ? KubernetesManifests.Parse(rendered.Text, rendered.Name) : KubernetesManifests.LoadFiles([.. source.Paths]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or YamlDotNet.Core.YamlException)
        {
            return;
        }

        var hints = new[] { app is null ? null : Path.GetFileName(app.Folder), Path.GetFileName(scan.Root) }
            .OfType<string>()
            .Where(h => h.Length > 1 && h != ".")
            .Select(h => h.ToLowerInvariant())
            .ToList();
        int Score(string name) => hints.Count(h => name.Contains(h, StringComparison.OrdinalIgnoreCase) || h.Contains(name, StringComparison.OrdinalIgnoreCase));

        var workloads = manifests.Workloads.Where(w => w.Containers.Any(c => !c.IsInitContainer)).ToList();
        if (workloads.Count == 0)
        {
            return;
        }

        var workload = workloads.OrderByDescending(w => Score(w.Name)).ThenByDescending(w => w.Containers.Sum(c => c.Env.Count + c.EnvFrom.Count)).First();
        var containers = workload.Containers.Where(c => !c.IsInitContainer).ToList();
        var container = containers.OrderByDescending(c => Score(c.Name) + (string.Equals(c.Name, workload.Name, StringComparison.OrdinalIgnoreCase) ? 2 : 0))
            .ThenByDescending(c => c.Env.Count + c.EnvFrom.Count)
            .First();
        if (workloads.Count > 1)
        {
            source.Workload = workload.Name;
            notes.Add($"The manifests hold {workloads.Count} workloads; traced {workload.DisplayName}.");
        }

        if (containers.Count > 1)
        {
            source.Container = container.Name;
        }

        prefixes.AddRange(container.EnvFrom.Select(e => e.Prefix).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal));
    }

    // AddEnvironmentVariables("ORDERS_") in the app's code.
    private static IEnumerable<string> FindPrefixesInCode(string folder)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.cs", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 }).Take(200).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var found = new List<string>();
        foreach (var file in files)
        {
            try
            {
                if (new FileInfo(file).Length > 512 * 1024)
                {
                    continue;
                }

                foreach (System.Text.RegularExpressions.Match match in EnvironmentPrefixCall().Matches(File.ReadAllText(file)))
                {
                    found.Add(match.Groups["prefix"].Value);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip unreadable files.
            }
        }

        return found.Distinct(StringComparer.Ordinal);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"AddEnvironmentVariables\(\s*(?:prefix:\s*)?""(?<prefix>[A-Za-z0-9_]+)""\s*\)", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex EnvironmentPrefixCall();

    private static string? MatchEnvironment(IEnumerable<string> candidates, string environment, Func<string, string> name) =>
        MatchEnvironment(environment, name, candidates);

    // The first candidate named for the environment, from the first group that has one. The whole name in any group comes
    // before the short form in the first, so the prod in k8s/products does not beat k8s/overlays/production.
    private static string? MatchEnvironment(string environment, Func<string, string> name, params IEnumerable<string>[] groups)
    {
        var env = environment.ToLowerInvariant();
        var shortName = env switch
        {
            "production" => "prod",
            "development" => "dev",
            "staging" => "stag",
            _ => env,
        };
        foreach (var form in new[] { env, shortName })
        {
            foreach (var group in groups)
            {
                if (group.FirstOrDefault(c => name(c).Contains(form, StringComparison.OrdinalIgnoreCase)) is { } match)
                {
                    return match;
                }
            }
        }

        return null;
    }

    // What helm and kubectl need to start and find their own folders on Windows, Linux and macOS. Nothing else of this
    // process's environment reaches them: kustomize fills an env file line without '=' from its environment, and the
    // web app's environment holds the server's own tokens, such as GITHUB_TOKEN and OCTOPUS_API_KEY.
    private static readonly string[] ToolEnvironment =
    [
        "PATH", "PATHEXT", "SystemRoot", "windir", "SystemDrive",
        "HOME", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA",
        "TMPDIR", "TEMP", "TMP",
        "XDG_CACHE_HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME",
        "HELM_CACHE_HOME", "HELM_CONFIG_HOME", "HELM_DATA_HOME",
    ];

    // Sends HTTP(S) through a proxy that does not exist, and lets git use local repositories only.
    private static readonly (string Name, string Value)[] OfflineEnvironment =
    [
        ("HTTP_PROXY", "http://127.0.0.1:9"),
        ("HTTPS_PROXY", "http://127.0.0.1:9"),
        ("ALL_PROXY", "http://127.0.0.1:9"),
        ("http_proxy", "http://127.0.0.1:9"),
        ("https_proxy", "http://127.0.0.1:9"),
        ("all_proxy", "http://127.0.0.1:9"),
        ("NO_PROXY", string.Empty),
        ("no_proxy", string.Empty),
        ("GIT_ALLOW_PROTOCOL", "file"),
        ("GIT_TERMINAL_PROMPT", "0"),
    ];

    /// <summary>
    /// Runs <paramref name="tool"/> with no shell and returns its output, or null when it is missing, fails or takes over
    /// a minute. The repository decides what the tool reads, so the tool gets no network: a kustomization may name remote
    /// bases (git or http URLs), which would otherwise be fetched, with the user's git credentials, from any host. For the
    /// same reason it gets only the environment variables it needs to run (see <see cref="StartInfo"/>).
    /// </summary>
    internal static string? RunTool(string tool, IReadOnlyList<string> arguments)
    {
        var start = StartInfo(tool, arguments, Environment.GetEnvironmentVariable);
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>How <see cref="RunTool"/> starts <paramref name="tool"/>: the variables in <see cref="ToolEnvironment"/> that are set, and the offline settings.</summary>
    internal static ProcessStartInfo StartInfo(string tool, IReadOnlyList<string> arguments, Func<string, string?> getEnvironmentVariable)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Clear();
        foreach (var name in ToolEnvironment)
        {
            if (getEnvironmentVariable(name) is { } value)
            {
                start.Environment[name] = value;
            }
        }

        foreach (var (name, value) in OfflineEnvironment)
        {
            start.Environment[name] = value;
        }

        return start;
    }
}
