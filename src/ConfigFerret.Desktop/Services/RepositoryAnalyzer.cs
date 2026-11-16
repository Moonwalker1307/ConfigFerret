using System.ComponentModel;
using System.Diagnostics;
using System.Text;
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
    // The most output read from a tool, in characters (64 MB as .NET holds them). A chart's templates can loop to write
    // gigabytes, and the web app's server would hold all of it in memory.
    private const int MaxToolOutput = 32 * 1024 * 1024;

    private readonly Func<string, IReadOnlyList<string>, CancellationToken, ToolRun> _render;

    /// <summary>Creates an analyzer.</summary>
    /// <param name="render">Runs a tool and returns its output, or null when the tool is not installed or fails. Defaults to running the real process.</param>
    public RepositoryAnalyzer(Func<string, IReadOnlyList<string>, string?>? render = null)
        : this(render is null ? (tool, arguments, cancellationToken) => RunTool(tool, arguments, cancellationToken: cancellationToken) : (tool, arguments, _) => new ToolRun(render(tool, arguments)))
    {
    }

    private RepositoryAnalyzer(Func<string, IReadOnlyList<string>, CancellationToken, ToolRun> render) => _render = render;

    /// <summary>Creates an analyzer whose tools can also say why they failed.</summary>
    internal static RepositoryAnalyzer WithTools(Func<string, IReadOnlyList<string>, ToolRun> render) => new((tool, arguments, _) => render(tool, arguments));

    /// <summary>Creates an analyzer whose tools can also say why they failed and see the trace's cancellation.</summary>
    internal static RepositoryAnalyzer WithTools(Func<string, IReadOnlyList<string>, CancellationToken, ToolRun> render) => new(render);

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
    public Profile BuildProfile(RepositoryScan scan, AnalysisChoices choices, AnalysisConnections connections, List<string> notes) =>
        BuildProfile(scan, choices, connections, notes, CancellationToken.None);

    /// <summary>Builds the profile to trace, and stops helm or kubectl when <paramref name="cancellationToken"/> is cancelled.</summary>
    /// <exception cref="OperationCanceledException">The trace was cancelled while a tool ran.</exception>
    internal Profile BuildProfile(RepositoryScan scan, AnalysisChoices choices, AnalysisConnections connections, List<string> notes, CancellationToken cancellationToken)
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

            var helm = _render("helm", arguments, cancellationToken);
            if (helm.Output is not null)
            {
                rendered = ("helm-template.yaml", helm.Output);
            }
            else if (helm.Problem is null)
            {
                notes.Add("Install helm to render the chart, so its values link to the app's settings. Without it the chart's values are listed only where a ConfigMap or env entry in the repository uses them.");
            }
            else
            {
                notes.Add($"helm could not render the chart {chart.RelativePath}: {helm.Problem}. Run helm template on the chart to see why. Without the rendered chart its values are listed only where a ConfigMap or env entry in the repository uses them.");
            }

            profile.Sources.Add(new HelmSource { Name = $"Helm chart {chart.RelativePath}", ChartFolder = chart.Folder, ValuesFiles = valuesFiles });
        }

        var kustomization = choices.Kustomization;
        if (kustomization is not null)
        {
            if (rendered is null)
            {
                // kustomize also when kubectl failed: kubectl bundles its own kustomize, often an older one. Not after a
                // timeout or too much output, which kustomize would only repeat.
                var kubectl = _render("kubectl", ["kustomize", scan.Full(kustomization)], cancellationToken);
                var kustomize = kubectl.Output is null && (kubectl.Problem is null || kubectl.Failed) ? _render("kustomize", ["build", scan.Full(kustomization)], cancellationToken) : ToolRun.Missing;
                if ((kubectl.Output ?? kustomize.Output) is { } output)
                {
                    rendered = ("kustomize-build.yaml", output);
                }
                else if (kubectl.Problem is null && kustomize.Problem is null)
                {
                    notes.Add("Install kubectl or kustomize to build the overlay, so its generators link to the app's settings.");
                }
                else
                {
                    var (tool, command, problem) = kubectl.Problem is not null ? ("kubectl", "kubectl kustomize", kubectl.Problem) : ("kustomize", "kustomize build", kustomize.Problem);
                    notes.Add($"{tool} could not build the overlay {kustomization}: {problem}."
                        + (kubectl.Problem is not null && kustomize.Problem is not null ? $" kustomize could not build it either: {kustomize.Problem}." : string.Empty)
                        + $" Run {command} on the folder to see why. Remote bases (git or http URLs) are not fetched, so an overlay that needs one cannot be built.");
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
    /// Runs <paramref name="tool"/> with no shell and returns its output, or why there is none: missing, failed, over a
    /// minute or over <paramref name="maxOutput"/> characters of output. The repository decides what the tool reads, so
    /// the tool gets no network: a kustomization may name remote bases (git or http URLs), which would otherwise be
    /// fetched, with the user's git credentials, from any host. For the same reason it gets only the environment
    /// variables it needs to run (see <see cref="StartInfo"/>).
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; the tool and every process it started are stopped.</exception>
    internal static ToolRun RunTool(string tool, IReadOnlyList<string> arguments, int maxOutput = MaxToolOutput, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = StartInfo(tool, arguments, Environment.GetEnvironmentVariable);
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return ToolRun.Missing;
            }

            var output = ReadAtMostAsync(process.StandardOutput, maxOutput);
            var error = ReadAtMostAsync(process.StandardError, 4096);

            // A cancelled trace, such as one replaced by the next drop-down change, stops the tool at once instead of
            // leaving it to run out its minute next to the new one. Nothing waits for its output after that.
            bool exited;
            using (cancellationToken.Register(() => Stop(process)))
            {
                exited = process.WaitForExit(TimeSpan.FromSeconds(60));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!exited)
            {
                Stop(process);
                return new ToolRun(null, "it took over a minute, so it was stopped");
            }

            if (process.ExitCode != 0)
            {
                error.Wait(cancellationToken);
                return new ToolRun(null, $"it exited with code {process.ExitCode}" + Reason(error.GetAwaiter().GetResult().Text), Failed: true);
            }

            output.Wait(cancellationToken);
            var (text, cut) = output.GetAwaiter().GetResult();
            return cut ? new ToolRun(null, $"it wrote more than {maxOutput / 1024 / 1024} MB") : new ToolRun(text);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ToolRun.Missing;
        }
    }

    // Stops the tool and every process it started, such as a git clone or a plugin.
    private static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException)
        {
            // It has exited already, or a process it started refused to be killed. This runs from the cancellation
            // callback too, where a throw would reach whoever cancelled and stop the next trace from starting.
        }
    }

    // The error output is never shown: it quotes values from the repository (a template's fail message, an env file line)
    // and paths on this machine. Only fixed text is, for errors that are easy to tell.
    private static string Reason(string error) =>
        error.Contains("missing in charts/", StringComparison.Ordinal) || error.Contains("helm dependency build", StringComparison.Ordinal)
            ? ", because the chart's dependencies are not in its charts folder (helm dependency build fetches them)"
            : error.Contains("127.0.0.1:9", StringComparison.Ordinal) || error.Contains("127.0.0.1 port 9", StringComparison.Ordinal) || error.Contains("transport '", StringComparison.Ordinal)
                ? ", because it needs a remote base, which is not fetched"
                : string.Empty;

    // Reads to the end and keeps the first max characters, so a tool that writes without end cannot fill the memory.
    private static async Task<(string Text, bool Cut)> ReadAtMostAsync(StreamReader reader, int max)
    {
        var text = new StringBuilder();
        var buffer = new char[81920];
        var cut = false;
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            var keep = Math.Min(read, max - text.Length);
            text.Append(buffer, 0, keep);
            cut |= keep < read;
        }

        return (text.ToString(), cut);
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

/// <summary>What running a tool gave.</summary>
/// <param name="Output">The tool's output, or null when it is not installed or gave none.</param>
/// <param name="Problem">Why a tool that is installed gave no output, such as "it exited with code 1", or null.</param>
/// <param name="Failed">True when the tool ran to the end and exited with an error, not stopped by a limit.</param>
internal sealed record ToolRun(string? Output, string? Problem = null, bool Failed = false)
{
    /// <summary>The tool is not installed.</summary>
    public static ToolRun Missing { get; } = new(Output: null);
}
