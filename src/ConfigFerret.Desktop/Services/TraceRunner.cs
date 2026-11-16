using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using ConfigFerret.Desktop.Credentials;
using ConfigFerret.Desktop.Model;
using ConfigFerret.GitHub;
using ConfigFerret.Helm;
using ConfigFerret.Kubernetes;
using ConfigFerret.Kustomize;
using ConfigFerret.Octopus;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Desktop.Services;

/// <summary>What one source contributed to a trace.</summary>
/// <param name="Source">The source.</param>
/// <param name="Problem">Why the source was left out, or null when it was used.</param>
public sealed record SourceResult(SourceSettings Source, string? Problem);

/// <summary>The result of tracing a profile.</summary>
/// <param name="Report">Every effective setting with its full history.</param>
/// <param name="Unused">Values that never win.</param>
/// <param name="Sources">What happened to each enabled source.</param>
/// <param name="Notes">Notes about the trace that are not warnings from the library, such as variables without a known value.</param>
/// <param name="Secrets">The secrets audit of every setting.</param>
/// <param name="UnusedVariables">GitHub variables that no setting or workflow uses, and Octopus variables that no setting uses.</param>
public sealed record TraceResult(
    ProvenanceReport Report,
    UnusedReport Unused,
    IReadOnlyList<SourceResult> Sources,
    IReadOnlyList<string> Notes,
    IReadOnlyList<SecretFinding> Secrets,
    IReadOnlyList<UnusedVariable> UnusedVariables)
{
    /// <summary>Settings that no code reads: configured, but dead. Empty when there is no app code to read.</summary>
    public IReadOnlyList<DeadSetting> DeadSettings { get; init; } = [];

    /// <summary>Helm values that no template in the chart reads.</summary>
    public IReadOnlyList<DeadHelmValue> DeadHelmValues { get; init; } = [];

    /// <summary>How many C# files were read to find the keys the app reads, or null when no code was read.</summary>
    public int? CodeFilesScanned { get; init; }

    /// <summary>Places in the code that read a key the scan cannot work out, so a dead setting may still be read there.</summary>
    public IReadOnlyList<string> DynamicReads { get; init; } = [];

    /// <summary>GitHub variables that a workflow uses but no setting in the trace does, such as a deploy action's publish profile.</summary>
    public IReadOnlyList<WorkflowVariable> WorkflowVariables { get; init; } = [];
}

/// <summary>A configured setting that no code reads.</summary>
/// <param name="Key">The configuration key.</param>
/// <param name="Reason">Why it counts as not read, for example "No code reads it".</param>
/// <param name="SetBy">The layers that set it, outermost first.</param>
public sealed record DeadSetting(string Key, string Reason, IReadOnlyList<string> SetBy);

/// <summary>A variable in a deployment tool that no configuration setting picks up.</summary>
/// <param name="Tool">GitHub Actions or Octopus Deploy.</param>
/// <param name="Name">The variable name.</param>
/// <param name="Detail">Its scope or variable set, and whether it is secret.</param>
public sealed record UnusedVariable(string Tool, string Name, string Detail)
{
    /// <summary>The value, when the tool returns it and it is not secret (or secrets are revealed); otherwise null.</summary>
    public string? Value { get; init; }

    /// <summary>True for a secret or sensitive variable.</summary>
    public bool IsSecret { get; init; }
}

/// <summary>A GitHub variable that a workflow uses directly, while no configuration setting picks it up.</summary>
/// <param name="Name">The variable name.</param>
/// <param name="Detail">Its scope, and whether it is secret.</param>
/// <param name="UsedBy">Where a workflow first uses it, for example <c>deploy.yml line 12</c>.</param>
public sealed record WorkflowVariable(string Name, string Detail, string UsedBy)
{
    /// <summary>The value, when GitHub returns it (variables, never secrets); otherwise null.</summary>
    public string? Value { get; init; }

    /// <summary>True for a GitHub secret.</summary>
    public bool IsSecret { get; init; }
}

/// <summary>The profile cannot be traced as set up, for example because it has no app settings source.</summary>
public sealed class TraceSetupException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TraceSetupException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public TraceSetupException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the error behind it.</summary>
    public TraceSetupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Builds a <see cref="ConfigTracer"/> from a <see cref="Profile"/> and runs it. A source that cannot be read (a missing
/// values file, a GitHub token without access) is left out and reported; the trace goes on with the rest.
/// </summary>
public sealed class TraceRunner
{
    private readonly ICredentialStore _credentials;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<HttpClient>? _httpClientFactory;

    /// <summary>Creates a runner.</summary>
    /// <param name="credentials">Where stored connection credentials are read from.</param>
    /// <param name="getEnvironmentVariable">Reads this process's environment variables, for credentials kept there and for the user secrets folder. Defaults to <see cref="Environment.GetEnvironmentVariable(string)"/>.</param>
    /// <param name="httpClientFactory">Creates the HTTP client for GitHub and Octopus. Null lets each library create its own.</param>
    public TraceRunner(ICredentialStore credentials, Func<string, string?>? getEnvironmentVariable = null, Func<HttpClient>? httpClientFactory = null)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Traces <paramref name="profile"/>.</summary>
    /// <exception cref="TraceSetupException">The profile has no usable starting point, such as an app folder or Kubernetes manifests.</exception>
    public async Task<TraceResult> RunAsync(Profile profile, bool revealSecrets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var sources = profile.Sources.Where(s => s.Enabled).ToList();
        var results = new Dictionary<SourceSettings, string?>();
        var notes = new List<string>();
        var githubVariables = new List<GitHubVariables>();
        var octopusVariables = new List<OctopusVariables>();

        var kubernetes = sources.OfType<KubernetesSource>().ToList();
        if (kubernetes.Count > 1)
        {
            throw new TraceSetupException("A profile can use one Kubernetes source. Add all the manifest files to that one.");
        }

        var manifests = kubernetes.Count == 1 ? LoadManifests(kubernetes[0]) : null;
        if (manifests is not null)
        {
            results[kubernetes[0]] = null;
        }

        // GitHub variables load first: in an app trace, the workflows' env entries become the app's environment variables.
        var github = new Dictionary<GitHubSource, GitHubVariables>();
        foreach (var source in sources.OfType<GitHubSource>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                github[source] = await LoadGitHubAsync(source, cancellationToken).ConfigureAwait(false);
                githubVariables.Add(github[source]);
                results[source] = null;
            }
            catch (Exception ex) when (IsInputError(ex, cancellationToken))
            {
                results[source] = ex.Message;
            }
        }

        ConfigTracer tracer;
        if (profile.Mode == TraceMode.Container)
        {
            if (manifests is null)
            {
                throw new TraceSetupException("Tracing a container needs a Kubernetes source with the workload and its ConfigMaps and Secrets.");
            }

            foreach (var appOnly in sources.Where(s => s is AppSettingsSource or EnvironmentVariablesSource))
            {
                results[appOnly] = "Not used when tracing a container. Switch the profile to App to use it.";
            }

            var k = kubernetes[0];
            tracer = ConfigTracer.ForContainer(manifests, Blank(k.Workload), Blank(k.Container));
        }
        else
        {
            tracer = BuildAppTracer(sources, manifests, kubernetes.FirstOrDefault(), github, results, notes);
        }

        tracer.WithOptions(o => o.RevealSensitiveValues = revealSecrets);

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                switch (source)
                {
                    case HelmSource helm:
                        AddHelm(tracer, helm);
                        results[helm] = null;
                        break;
                    case KustomizeSource kustomize:
                        AddKustomize(tracer, kustomize);
                        results[kustomize] = null;
                        break;
                    case GitHubSource gitHubSource when github.TryGetValue(gitHubSource, out var variables):
                        AddGitHub(tracer, gitHubSource, variables);
                        break;
                    case OctopusSource octopus:
                        await AddOctopusAsync(tracer, octopus, octopusVariables, cancellationToken).ConfigureAwait(false);
                        results[octopus] = null;
                        break;
                }
            }
            catch (Exception ex) when (IsInputError(ex, cancellationToken))
            {
                results[source] = ex.Message;
            }
        }

        // The audit looks at the values, so it runs on a revealed trace that is dropped right after. The findings hold no values.
        tracer.WithOptions(o => o.RevealSensitiveValues = true);
        var secrets = SecretAudit.Audit(tracer.Trace());
        tracer.WithOptions(o => o.RevealSensitiveValues = revealSecrets);
        var report = tracer.Trace();

        var sourceResults = sources.Select(s => new SourceResult(s, results.GetValueOrDefault(s))).ToList();

        // Dead settings: configured keys that no code in the app reads. Only an app trace has code to read.
        var app = profile.Mode == TraceMode.App ? sources.OfType<AppSettingsSource>().FirstOrDefault(a => Directory.Exists(a.Folder)) : null;
        CodeReadScanner? code = app is null ? null : CodeReadScanner.Scan(app.Folder);
        UnusedReport unused;
        var dead = new List<DeadSetting>();
        if (code is { FilesScanned: > 0 })
        {
            var verdicts = report.Settings.ToDictionary(s => s.Key, s => code.Check(s.Key), StringComparer.OrdinalIgnoreCase);
            unused = report.FindUnused(verdicts.Where(v => v.Value.IsRead).Select(v => v.Key).ToList());
            foreach (var setting in unused.NotRead ?? [])
            {
                dead.Add(new DeadSetting(setting.Key, verdicts[setting.Key].Reason, setting.Steps.Select(Where).Distinct(StringComparer.Ordinal).ToList()));
            }
        }
        else
        {
            unused = report.FindUnused();
        }

        var deadHelm = new List<DeadHelmValue>();
        foreach (var helm in sources.OfType<HelmSource>().Where(h => results.GetValueOrDefault(h) is null && Directory.Exists(h.ChartFolder)))
        {
            deadHelm.AddRange(HelmValueUsage.FindDead(helm.ChartFolder, helm.ValuesFiles));
        }

        var workflowVariables = new List<WorkflowVariable>();
        return new TraceResult(report, unused, sourceResults, notes, secrets, FindUnusedVariables(report, github, octopusVariables, workflowVariables, revealSecrets))
        {
            WorkflowVariables = workflowVariables,
            DeadSettings = dead,
            DeadHelmValues = deadHelm,
            CodeFilesScanned = code?.FilesScanned,
            DynamicReads = code?.DynamicReads ?? [],
        };

        static string Where(ProvenanceStep step) => step.Location is null ? step.Source : $"{step.Source} ({step.Location})";
    }

    private static List<UnusedVariable> FindUnusedVariables(
        ProvenanceReport report,
        Dictionary<GitHubSource, GitHubVariables> github,
        List<OctopusVariables> octopus,
        List<WorkflowVariable> workflowOnly,
        bool revealSecrets)
    {
        var used = new HashSet<SourceRef>();
        foreach (var step in report.Settings.SelectMany(s => s.Steps))
        {
            Collect(step, used);
        }

        var unused = new List<UnusedVariable>();
        foreach (var (source, variables) in github)
        {
            // A variable any workflow refers to (a deploy action's with: input, a run: script, an env: entry for a
            // differently named setting) is in use, even when no traced setting picks it up.
            var workflows = WorkflowReferences.Read(source.Workflows);
            foreach (var variable in variables.Entries)
            {
                if (used.Contains(new GitHubVariableRef(variable.Kind, variable.Scope, variable.Owner, variable.Name)))
                {
                    continue;
                }

                var isSecret = variable.Kind == GitHubVariableKind.Secret;
                var detail = $"{variable.Scope.ToString().ToLowerInvariant()} {(variable.Kind == GitHubVariableKind.Secret ? "secret" : "variable")} ({variable.Owner})";
                if (workflows.FirstUse(variable.Kind, variable.Name) is { } usedBy)
                {
                    workflowOnly.Add(new WorkflowVariable(variable.Name, detail, usedBy) { Value = isSecret ? null : variable.Value, IsSecret = isSecret });
                }
                else
                {
                    unused.Add(new UnusedVariable("GitHub Actions", variable.Name, detail) { Value = isSecret ? null : variable.Value, IsSecret = isSecret });
                }
            }
        }

        foreach (var variable in octopus.SelectMany(v => v.Entries).DistinctBy(v => (v.SetName, v.Name)))
        {
            if (!used.Contains(new OctopusVariableRef(variable.SetName, variable.Name)))
            {
                unused.Add(new UnusedVariable("Octopus Deploy", variable.Name, $"{variable.SetName}{(variable.IsSensitive ? ", sensitive" : string.Empty)}")
                {
                    Value = variable.IsSensitive && !revealSecrets ? null : variable.Value,
                    IsSecret = variable.IsSensitive,
                });
            }
        }

        return unused;

        static void Collect(ProvenanceStep step, HashSet<SourceRef> used)
        {
            if (step.Reference is GitHubVariableRef or OctopusVariableRef)
            {
                used.Add(step.Reference);
            }

            foreach (var origin in step.Origins)
            {
                Collect(origin, used);
            }
        }
    }

    /// <summary>
    /// Reads the credential for <paramref name="source"/> from where the source says it is.
    /// </summary>
    /// <exception cref="TraceSetupException">No credential is set.</exception>
    public string ReadCredential(ConnectionSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.CredentialSource == CredentialSource.EnvironmentVariable)
        {
            var name = source.CredentialVariable.Trim();
            if (name.Length == 0)
            {
                throw new TraceSetupException($"Name the environment variable that holds the {source.CredentialName}.");
            }

            var value = _getEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value)
                ? throw new TraceSetupException($"The environment variable {name} is not set. Set it before starting ConfigFerret, or save the {source.CredentialName} in the {_credentials.DisplayName} instead.")
                : value;
        }

        var stored = _credentials.Read(source.CredentialAccount);
        return string.IsNullOrEmpty(stored)
            ? throw new TraceSetupException($"No {source.CredentialName} is saved for this connection. Enter it and choose Save.")
            : stored;
    }

    private static KubernetesManifests LoadManifests(KubernetesSource source)
    {
        if (source.Paths.Count == 0)
        {
            throw new TraceSetupException($"{source.Name}: add at least one manifest file or folder.");
        }

        try
        {
            foreach (var path in source.Paths.Where(p => !File.Exists(p) && !Directory.Exists(p)))
            {
                throw new FileNotFoundException($"{path} does not exist.");
            }

            return KubernetesManifests.LoadFiles([.. source.Paths]);
        }
        catch (Exception ex) when (IsInputError(ex, CancellationToken.None))
        {
            throw new TraceSetupException($"{source.Name}: {ex.Message}", ex);
        }
    }

    private ConfigTracer BuildAppTracer(
        List<SourceSettings> sources,
        KubernetesManifests? manifests,
        KubernetesSource? kubernetes,
        Dictionary<GitHubSource, GitHubVariables> github,
        Dictionary<SourceSettings, string?> results,
        List<string> notes)
    {
        var apps = sources.OfType<AppSettingsSource>().ToList();
        if (apps.Count == 0)
        {
            throw new TraceSetupException("Add an App settings source with the folder that holds appsettings.json, or switch the profile to Container to trace Kubernetes manifests on their own.");
        }

        // The app's environment: what the container gets from the manifests, then each environment variables source.
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (manifests is not null)
        {
            var container = ConfigTracer.ForContainer(manifests, Blank(kubernetes!.Workload), Blank(kubernetes.Container))
                .WithOptions(o => o.RevealSensitiveValues = true)
                .Trace();
            foreach (var setting in container.Settings.Where(s => !s.Key.StartsWith('/')))
            {
                environment[setting.Key] = setting.Value;
            }
        }

        foreach (var source in sources.OfType<EnvironmentVariablesSource>())
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(source.File))
                {
                    foreach (var (name, value) in DotEnv.Parse(File.ReadAllText(source.File), Path.GetFileName(source.File)))
                    {
                        environment[name] = value;
                    }
                }

                foreach (var (name, value) in DotEnv.Parse(source.Variables, source.Name))
                {
                    environment[name] = value;
                }

                results[source] = null;
            }
            catch (Exception ex) when (IsInputError(ex, CancellationToken.None))
            {
                results[source] = ex.Message;
            }
        }

        var unknown = environment.Where(e => e.Value is null).Select(e => e.Key).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            notes.Add("The manifests don't hold the value of " + string.Join(", ", unknown)
                + " (set at run time, or from a ConfigMap or Secret that isn't in the manifests). Add them in an Environment variables source to include them.");
            foreach (var name in unknown)
            {
                environment.Remove(name);
            }
        }

        var builder = new ConfigurationBuilder();
        foreach (var app in apps)
        {
            AddAppSettings(builder, app);
            results[app] = null;
        }

        var prefixes = apps.SelectMany(a => a.EnvironmentPrefixes).Distinct(StringComparer.Ordinal).ToList();
        AddWorkflowEnvironment(environment, github, builder, prefixes, notes);
        foreach (var prefix in prefixes.Count == 0 ? [string.Empty] : prefixes)
        {
            builder.Add(new SimulatedEnvironmentVariablesSource(environment, prefix));
        }

        IConfigurationRoot configuration;
        try
        {
            configuration = builder.Build();
        }
        catch (Exception ex) when (IsInputError(ex, CancellationToken.None))
        {
            throw new TraceSetupException("Could not read the app's settings: " + ex.Message, ex);
        }

        var tracer = ConfigTracer.For(configuration).WithOptions(o => o.Environment = environment);
        if (manifests is not null)
        {
            tracer.WithKubernetes(manifests, Blank(kubernetes!.Workload), Blank(kubernetes.Container));
        }

        return tracer;
    }

    private void AddAppSettings(ConfigurationBuilder builder, AppSettingsSource app)
    {
        if (string.IsNullOrWhiteSpace(app.Folder) || !Directory.Exists(app.Folder))
        {
            throw new TraceSetupException($"{app.Name}: the app folder '{app.Folder}' does not exist.");
        }

        var directory = Path.GetFullPath(app.Folder);
        var environmentName = string.IsNullOrWhiteSpace(app.EnvironmentName) ? "Production" : app.EnvironmentName.Trim();
        var files = new[] { Path.Join(directory, "appsettings.json"), Path.Join(directory, $"appsettings.{environmentName}.json") }
            .Where(File.Exists)
            .ToList();
        foreach (var extra in app.ExtraFiles)
        {
            if (!File.Exists(extra))
            {
                throw new TraceSetupException($"{app.Name}: the settings file '{extra}' does not exist.");
            }

            files.Add(Path.GetFullPath(extra));
        }

        if (files.Count == 0)
        {
            throw new TraceSetupException($"{app.Name}: there is no appsettings.json in {directory}.");
        }

        foreach (var file in files)
        {
            builder.AddJsonFile(file, optional: false);
        }

        if (app.UseUserSecrets && string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
            && UserSecrets.FindFile(directory, _getEnvironmentVariable) is { } secrets && File.Exists(secrets))
        {
            builder.AddJsonFile(secrets, optional: false);
        }
    }

    private static void AddHelm(ConfigTracer tracer, HelmSource helm)
    {
        if (!Directory.Exists(helm.ChartFolder))
        {
            throw new DirectoryNotFoundException($"The chart folder '{helm.ChartFolder}' does not exist.");
        }

        foreach (var file in helm.ValuesFiles.Where(f => !File.Exists(f)))
        {
            throw new FileNotFoundException($"The values file '{file}' does not exist.");
        }

        tracer.WithHelm(HelmChart.Load(helm.ChartFolder, helm.ValuesFiles, helm.SetValues));
    }

    private static void AddKustomize(ConfigTracer tracer, KustomizeSource kustomize)
    {
        if (!Directory.Exists(kustomize.Folder))
        {
            throw new DirectoryNotFoundException($"The Kustomize folder '{kustomize.Folder}' does not exist.");
        }

        tracer.WithKustomize(Kustomization.Load(kustomize.Folder, kustomize.AllowFilesOutsideFolder));
    }

    private async Task<GitHubVariables> LoadGitHubAsync(GitHubSource source, CancellationToken cancellationToken)
    {
        var parts = source.Repository.Trim().Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"The repository must be owner/name, got '{source.Repository}'.");
        }

        foreach (var workflow in source.Workflows.Where(w => !File.Exists(w)))
        {
            throw new FileNotFoundException($"The workflow file '{workflow}' does not exist.");
        }

        var options = new GitHubVariablesOptions
        {
            Owner = parts[0].Trim(),
            Repository = parts[1].Trim(),
            Environment = Blank(source.Environment),
            Token = ReadCredential(source),
        };
        if (Blank(source.ApiUrl) is { } apiUrl)
        {
            options.ApiBaseAddress = ParseUrl(apiUrl, "The GitHub API address");
        }

        if (_httpClientFactory is not null)
        {
            using var client = _httpClientFactory();
            return await GitHubVariables.LoadAsync(options, client, cancellationToken).ConfigureAwait(false);
        }

        return await GitHubVariables.LoadAsync(options, cancellationToken).ConfigureAwait(false);
    }

    private static void AddGitHub(ConfigTracer tracer, GitHubSource source, GitHubVariables variables) =>
        tracer.WithGitHub(variables, o =>
        {
            foreach (var workflow in source.Workflows)
            {
                o.AddWorkflow(workflow);
            }
        });

    // A workflow env entry such as Db__Host: ${{ vars.DB_HOST }} is an environment variable of the app the workflow runs
    // or deploys. Add the entries that look like app settings, unless something else already sets them, so the trace
    // shows them and links them to the GitHub variable. Others, such as NODE_VERSION, are left out.
    private static void AddWorkflowEnvironment(
        Dictionary<string, string?> environment,
        Dictionary<GitHubSource, GitHubVariables> github,
        ConfigurationBuilder appSettings,
        List<string> prefixes,
        List<string> notes)
    {
        if (github.Count == 0)
        {
            return;
        }

        HashSet<string> keys;
        try
        {
            keys = appSettings.Build().AsEnumerable().Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (IsInputError(ex, CancellationToken.None))
        {
            keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var secrets = new List<string>();
        foreach (var (source, variables) in github)
        {
            var options = new GitHubTraceOptions();
            foreach (var workflow in source.Workflows)
            {
                try
                {
                    options.AddWorkflow(workflow);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The trace reports an unreadable workflow when it reads the workflows again.
                }
            }

            foreach (var mapping in options.Mappings)
            {
                if (environment.ContainsKey(mapping.Name) || !LooksLikeAppSetting(mapping.Name, prefixes, keys))
                {
                    continue;
                }

                if (mapping.Kind == GitHubVariableKind.Secret)
                {
                    // GitHub never returns a secret's value. Show the setting with a stand-in, so it is still traced.
                    environment[mapping.Name] = SecretStandIn;
                    secrets.Add(mapping.Name);
                }
                else
                {
                    // Lowest priority first, so the last one is the value the workflow passes.
                    var found = variables.Find(mapping.VariableName, GitHubVariableKind.Variable);
                    environment[mapping.Name] = found.Count > 0 ? found[^1].Value ?? string.Empty : string.Empty;
                }
            }
        }

        if (secrets.Count > 0)
        {
            notes.Add($"GitHub doesn't return secret values, so {string.Join(", ", secrets.Order(StringComparer.Ordinal))} {(secrets.Count == 1 ? "shows" : "show")} {SecretStandIn} instead.");
        }
    }

    /// <summary>The value shown for a setting a workflow sets from a GitHub secret.</summary>
    internal const string SecretStandIn = "(GitHub secret)";

    private static bool LooksLikeAppSetting(string name, List<string> prefixes, HashSet<string> keys)
    {
        var rest = name;
        if (prefixes.Count > 0)
        {
            var prefix = prefixes.FirstOrDefault(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (prefix is null)
            {
                return false;
            }

            rest = name[prefix.Length..];
        }

        return rest.Contains("__", StringComparison.Ordinal)
            || rest.Contains("CONNSTR_", StringComparison.OrdinalIgnoreCase)
            || keys.Contains(rest);
    }

    private async Task AddOctopusAsync(ConfigTracer tracer, OctopusSource source, List<OctopusVariables> loaded, CancellationToken cancellationToken)
    {
        if (Blank(source.Server) is not { } server)
        {
            throw new ArgumentException("Enter the Octopus server address.");
        }

        if (Blank(source.Project) is not { } project)
        {
            throw new ArgumentException("Enter the Octopus project.");
        }

        var options = new OctopusVariablesOptions
        {
            ServerUri = ParseUrl(server, "The Octopus server address"),
            ApiKey = ReadCredential(source),
            Project = project,
            Space = Blank(source.Space),
            IncludeLibraryVariableSets = source.IncludeLibraryVariableSets,
        };

        OctopusVariables variables;
        if (_httpClientFactory is not null)
        {
            using var client = _httpClientFactory();
            variables = await OctopusVariables.LoadAsync(options, client, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            variables = await OctopusVariables.LoadAsync(options, cancellationToken).ConfigureAwait(false);
        }

        loaded.Add(variables);

        tracer.WithOctopus(variables, o =>
        {
            o.Environment = Blank(source.Environment);
            o.Machine = Blank(source.Machine);
            o.Channel = Blank(source.Channel);
            o.Step = Blank(source.Step);
            foreach (var role in source.Roles)
            {
                o.Roles.Add(role);
            }

            foreach (var tag in source.TenantTags)
            {
                o.TenantTags.Add(tag);
            }
        });
    }

    // Relative API paths resolve under the base address only when it ends with a slash.
    private static Uri ParseUrl(string text, string what) =>
        Uri.TryCreate(text.EndsWith('/') ? text : text + "/", UriKind.Absolute, out var uri)
            ? uri
            : throw new ArgumentException($"{what} must be an absolute URL, got '{text}'.");

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static bool IsInputError(Exception ex, CancellationToken cancellationToken) =>
        ex is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or ArgumentException
            or InvalidDataException or HttpRequestException or JsonException or XmlException or TraceSetupException or CredentialStoreException
            or YamlDotNet.Core.YamlException
        || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);
}

/// <summary>Reads dotenv-style text: NAME=VALUE per line, blank lines and # comments skipped, an optional export, quotes removed.</summary>
internal static class DotEnv
{
    public static IEnumerable<(string Name, string Value)> Parse(string? text, string sourceName)
    {
        var lines = (text ?? string.Empty).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                throw new FormatException($"{sourceName} line {i + 1}: expected NAME=VALUE.");
            }

            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }

            yield return (line[..eq].Trim(), value);
        }
    }
}

/// <summary>Finds an app's user secrets file the way <c>dotnet user-secrets</c> does.</summary>
internal static class UserSecrets
{
    /// <summary>The secrets.json for the UserSecretsId in the one project file in <paramref name="directory"/>, or null.</summary>
    public static string? FindFile(string directory, Func<string, string?> getEnvironmentVariable)
    {
        var projects = Directory.GetFiles(directory, "*.csproj");
        if (projects.Length != 1)
        {
            return null;
        }

        string? id;
        try
        {
            id = XDocument.Load(projects[0]).Descendants().FirstOrDefault(e => e.Name.LocalName == "UserSecretsId")?.Value.Trim();
        }
        catch (XmlException)
        {
            return null;
        }

        // User secrets ids are usually GUIDs; anything that could leave the usersecrets folder is refused.
        if (string.IsNullOrEmpty(id) || id.Trim('.').Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            return null;
        }

        var appData = getEnvironmentVariable("APPDATA");
        if (OperatingSystem.IsWindows() && appData is not null)
        {
            return Path.Join(appData, "Microsoft", "UserSecrets", id, "secrets.json");
        }

        var home = getEnvironmentVariable("HOME");
        return home is null ? null : Path.Join(home, ".microsoft", "usersecrets", id, "secrets.json");
    }
}
