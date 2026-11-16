using System.Globalization;
using ConfigFerret.Compose;
using ConfigFerret.DotNet;
using ConfigFerret.GitOps;
using ConfigFerret.Helm;
using ConfigFerret.Kubernetes;
using ConfigFerret.Kustomize;
using ConfigFerret.Terraform;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret;

/// <summary>
/// Traces every effective value in an <see cref="IConfigurationRoot"/> back through its layers.
/// </summary>
/// <example>
/// <code>
/// var report = ConfigTracer.For(builder.Configuration)
///     .WithKubernetes(KubernetesManifests.LoadFile("rendered.yaml"), workload: "api")
///     .WithHelm(HelmChart.Load("chart", valuesFiles: ["values-prod.yaml"]))
///     .Trace();
/// Console.WriteLine(report.Explain("Db:Timeout"));
/// </code>
/// </example>
public sealed class ConfigTracer
{
    // Exactly one of these is set: the app's own configuration (For), or a container seen from outside (ForContainer).
    private readonly IConfigurationRoot? _root;
    private readonly KubernetesResolver? _container;
    private readonly ComposeResolver? _compose;
    private readonly List<IOriginResolver> _resolvers = [];

    private ConfigTracer(IConfigurationRoot root) => _root = root;

    private ConfigTracer(KubernetesResolver container)
    {
        _container = container;
        // The container's own manifests also link the Secret keys it reads to the ExternalSecrets and
        // SecretProviderClasses that fill them.
        _resolvers.Add(container);
    }

    private ConfigTracer(ComposeResolver compose) => _compose = compose;

    /// <summary>The trace options.</summary>
    public TraceOptions Options { get; private set; } = new();

    /// <summary>How many origin steps resolvers may add to one setting before its trace is cut off.</summary>
    internal int MaxStepsPerSetting { get; set; } = 1_000;

    /// <summary>How many origin steps resolvers may add to the whole trace before the settings still to come are cut off.</summary>
    internal int MaxStepsPerTrace { get; set; } = 250_000;

    /// <summary>Starts a trace of <paramref name="configuration"/>.</summary>
    public static ConfigTracer For(IConfigurationRoot configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new ConfigTracer(configuration);
    }

    /// <summary>
    /// Starts a trace of the environment variables and mounted files a Kubernetes container gets, seen from outside
    /// the app: from the workload spec, its ConfigMaps and Secrets, and (with <see cref="WithHelm"/> or
    /// <see cref="WithKustomize"/>) the Helm values or Kustomize generators behind them. Use this when you have the
    /// manifests but not the app's own configuration, for example in a cluster browser.
    /// </summary>
    /// <remarks>
    /// The report lists the environment variables first, sorted by name, then the files that ConfigMap and Secret
    /// volumes mount, sorted by absolute path. Keys are matched case-sensitively. A file's value is its full content.
    /// A variable whose value Kubernetes sets at run time (a downward API field), or whose source is not in the
    /// manifests, is listed with a null value.
    /// </remarks>
    /// <param name="manifests">The workload and the ConfigMaps and Secrets it uses. Variables from an <c>envFrom</c> source, and files from a volume source without <c>items</c>, that is not in them are not listed.</param>
    /// <param name="workload">The workload name. Needed only when the manifests hold more than one.</param>
    /// <param name="container">The container name. Needed only when the workload has more than one.</param>
    /// <example>
    /// <code>
    /// var report = ConfigTracer.ForContainer(KubernetesManifests.Parse(podAndConfigMapsYaml), workload: "api-7d9f", container: "api")
    ///     .WithHelm(HelmChart.FromText([("values.yaml (chart default)", chartValues), ("release values", userValues)], templates))
    ///     .Trace();
    /// Console.WriteLine(report.Explain("DB__Timeout"));
    /// </code>
    /// </example>
    public static ConfigTracer ForContainer(KubernetesManifests manifests, string? workload = null, string? container = null)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        return new ConfigTracer(new KubernetesResolver(manifests, workload, container));
    }

    /// <summary>
    /// Starts a trace of the environment variables and mounted files a Docker Compose service's containers get, seen
    /// from outside the app, the way <c>docker compose config</c> works them out: each <c>env_file</c> line, then each
    /// <c>environment</c> entry, with the <c>.env</c> line or shell variable behind every <c>${VAR}</c>.
    /// </summary>
    /// <remarks>
    /// The report lists the environment variables first, sorted by name, then the files that <c>secrets</c> and
    /// <c>configs</c> mount, sorted by path. Names are matched case-sensitively. A name listed in <c>environment</c>
    /// without a value that neither the shell nor the project's env file sets is listed with a null value.
    /// </remarks>
    /// <param name="project">The compose project.</param>
    /// <param name="service">The service name. Needed only when the project has more than one.</param>
    public static ConfigTracer ForCompose(ComposeProject project, string? service = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new ConfigTracer(new ComposeResolver(project, service));
    }

    /// <summary>Sets the trace options.</summary>
    public ConfigTracer WithOptions(TraceOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        return this;
    }

    /// <summary>Changes the trace options in place.</summary>
    public ConfigTracer WithOptions(Action<TraceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Options);
        return this;
    }

    /// <summary>
    /// Links environment variables and mounted files to the Kubernetes workload, ConfigMaps and Secrets in <paramref name="manifests"/>.
    /// </summary>
    /// <param name="manifests">Rendered manifests (<c>helm template</c>, <c>kustomize build</c>, <c>kubectl get -o yaml</c>) or the live pod.</param>
    /// <param name="workload">The workload name. Needed only when the manifests hold more than one.</param>
    /// <param name="container">The container name. Needed only when the workload has more than one.</param>
    /// <exception cref="InvalidOperationException">The trace was started with <see cref="ForContainer"/>, which already has its manifests.</exception>
    public ConfigTracer WithKubernetes(KubernetesManifests manifests, string? workload = null, string? container = null)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        if (_container is not null)
        {
            throw new InvalidOperationException("WithKubernetes links the app's configuration to Kubernetes. A trace started with ForContainer already reads the container from its manifests; add every ConfigMap and Secret to those instead.");
        }

        _resolvers.Add(new KubernetesResolver(manifests, workload, container));
        return this;
    }

    /// <summary>Links ConfigMap keys and workload env entries to the Helm values that rendered them.</summary>
    public ConfigTracer WithHelm(HelmChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        _resolvers.Add(new HelmResolver(chart));
        return this;
    }

    /// <summary>Links ConfigMap and Secret keys to the Kustomize generators and overlays that produced them.</summary>
    public ConfigTracer WithKustomize(Kustomization kustomization)
    {
        ArgumentNullException.ThrowIfNull(kustomization);
        _resolvers.Add(new KustomizeResolver(kustomization));
        return this;
    }

    /// <summary>
    /// Links layers to what an Argo CD Application or a Flux HelmRelease or Kustomization adds: the Helm values it
    /// passes (see <see cref="HelmChart.ForGitOps"/>), the ConfigMap keys an Argo CD kustomize patch sets, and the
    /// <c>${VAR}</c> placeholders a Flux Kustomization substitutes. Use it instead of <see cref="WithHelm"/> for the release.
    /// </summary>
    public ConfigTracer WithGitOps(GitOpsApplication application, GitOpsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        _resolvers.Add(new GitOpsResolver(application, options));
        return this;
    }

    /// <summary>Links environment variables and ConfigMap and Secret keys to the Terraform outputs with the same name, or the ones linked explicitly.</summary>
    public ConfigTracer WithTerraform(TerraformOutputs outputs, Action<TerraformTraceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        var options = new TerraformTraceOptions();
        configure?.Invoke(options);
        _resolvers.Add(new TerraformResolver(outputs, options));
        return this;
    }

    /// <summary>Adds a custom resolver for another source of configuration.</summary>
    public ConfigTracer WithResolver(IOriginResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolvers.Add(resolver);
        return this;
    }

    /// <summary>Runs the trace.</summary>
    public ProvenanceReport Trace()
    {
        // Reading every key to trace it is not the app using it.
        using var paused = ConfigReadRecorder.Pause();
        var diagnostics = new TraceDiagnostics();
        var layers = _root is not null ? ReadConfiguration(_root, diagnostics)
            : _compose is not null ? _compose.ReadLayers(diagnostics)
            : ReadContainer(_container!, diagnostics);

        var settings = new List<TracedSetting>(layers.Count);
        var budget = new StepBudget(MaxStepsPerSetting, MaxStepsPerTrace);
        foreach (var (key, effectiveValue, steps) in layers)
        {
            var traceWasCut = budget.TraceCut;
            budget.StartSetting();
            var enriched = MarkEffective(steps.Select(s => Enrich(s, 1, diagnostics, budget)).ToList());
            WarnIfCut(key, traceWasCut, budget, diagnostics);

            // A trace that was cut off may have stopped before the secret it would have found, so it is hidden like a
            // setting whose key looks secret.
            var keySensitive = budget.SettingCut || (Options.SensitiveKeyPattern?.IsMatch(key) ?? false);
            var ownNames = OwnNames(key, enriched);
            var effectiveSensitive = false;
            List<ProvenanceStep> redacted;
            if (Options.RevealSensitiveValues)
            {
                redacted = enriched;
                effectiveSensitive = enriched.Count > 0 && IsSensitiveDeep(enriched[^1], ownNames);
            }
            else
            {
                redacted = new List<ProvenanceStep>(enriched.Count);
                foreach (var step in enriched)
                {
                    var (hidden, deep) = Redact(step, ownNames, keySensitive, inherited: false);
                    redacted.Add(hidden);
                    effectiveSensitive = deep;
                }
            }

            // The last step is the one that wins, so effectiveSensitive now says whether its winning chain is secret.
            var sensitive = keySensitive || effectiveSensitive;

            var value = effectiveValue;
            if (sensitive && !Options.RevealSensitiveValues && value is not null)
            {
                value = Options.RedactedText;
            }

            settings.Add(new TracedSetting(key, value, redacted, sensitive));
        }

        return new ProvenanceReport(settings, diagnostics.Warnings, _root is null ? StringComparer.Ordinal : null);
    }

    private List<(string Key, string? Value, List<ProvenanceStep> Steps)> ReadConfiguration(IConfigurationRoot root, TraceDiagnostics diagnostics)
    {
        var environment = Options.Environment ?? ReadProcessEnvironment();
        var describer = new ProviderDescriber(environment, diagnostics);
        var providers = ProviderDescriber.Flatten(root.Providers).ToList();

        var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in root.AsEnumerable())
        {
            if (pair.Value is not null)
            {
                keys.Add(pair.Key);
            }
        }

        var layers = new List<(string, string?, List<ProvenanceStep>)>(keys.Count);
        foreach (var key in keys)
        {
            var steps = new List<ProvenanceStep>();
            foreach (var provider in providers)
            {
                if (provider.TryGet(key, out var providerValue))
                {
                    steps.Add(describer.Describe(provider, key, providerValue) with { ConfigurationKey = key });
                }
            }

            layers.Add((key, root[key], steps));
        }

        return layers;
    }

    // Seen from outside there is no .NET provider above the container, so each variable's or file's history starts at
    // the workload layers themselves. A variable whose value Kubernetes fills in at run time (a downward API field) or
    // whose source was not loaded is still listed, with no value.
    private static List<(string Key, string? Value, List<ProvenanceStep> Steps)> ReadContainer(KubernetesResolver container, TraceDiagnostics diagnostics)
    {
        var layers = new List<(string, string?, List<ProvenanceStep>)>();
        foreach (var name in container.EnvironmentVariableNames(diagnostics).Order(StringComparer.Ordinal))
        {
            var steps = container.ResolveEnvironmentVariable(name, diagnostics).ToList();
            var value = steps.Count > 0 && !steps[^1].IsRemoval ? steps[^1].Value : null;
            layers.Add((name, value, steps));
        }

        foreach (var path in container.MountedFilePaths(diagnostics).Order(StringComparer.Ordinal))
        {
            var steps = container.ResolveMountedFile(path, diagnostics).ToList();
            layers.Add((path, steps.Count > 0 ? steps[^1].Value : null, steps));
        }

        return layers;
    }

    private ProvenanceStep Enrich(ProvenanceStep step, int depth, TraceDiagnostics diagnostics, StepBudget budget)
    {
        if (depth > Options.MaxDepth || _resolvers.Count == 0)
        {
            return step;
        }

        var origins = new List<ProvenanceStep>();
        foreach (var resolver in _resolvers)
        {
            if (!budget.MayResolve())
            {
                break;
            }

            var chain = budget.Take(resolver.Resolve(step, diagnostics));
            if (chain.Count == 0)
            {
                continue;
            }

            origins.AddRange(MarkEffective(chain.Select(o => Enrich(o, depth + 1, diagnostics, budget)).ToList()));
        }

        return origins.Count == 0 ? step : step with { Origins = [.. step.Origins, .. origins] };
    }

    // Names the bound and the key, never a value: warnings are not redacted.
    private void WarnIfCut(string key, bool traceWasCut, StepBudget budget, TraceDiagnostics diagnostics)
    {
        if (budget.SettingBoundReached)
        {
            var hidden = Options.RevealSensitiveValues ? "" : " and its value is hidden: the part not traced may hold a secret";
            diagnostics.Warn(string.Create(CultureInfo.InvariantCulture, $"Stopped following the origins of {key} after {MaxStepsPerSetting:N0} upstream steps, so its trace is incomplete{hidden}."));
        }
        else if (budget.TraceCut && !traceWasCut)
        {
            var hidden = Options.RevealSensitiveValues ? "" : " and their values are hidden: the part not traced may hold a secret";
            diagnostics.Warn(string.Create(CultureInfo.InvariantCulture, $"Stopped following origins after {MaxStepsPerTrace:N0} upstream steps in this trace, so the traces of {key} and every setting after it are incomplete{hidden}."));
        }
    }

    private static List<ProvenanceStep> MarkEffective(List<ProvenanceStep> chain)
    {
        for (var i = 0; i < chain.Count; i++)
        {
            var effective = i == chain.Count - 1 && !chain[i].IsRemoval;
            if (chain[i].IsEffective != effective)
            {
                chain[i] = chain[i] with { IsEffective = effective };
            }
        }

        return chain;
    }

    // A layer is secret when its source says so, or when its value has a name of its own that looks secret, such as
    // ConfigMap key smtp-password or GitHub variable SMTP_PASSWORD feeding a key that does not look secret. A .NET layer
    // is named after the key, which the key rule tests already. So is an environment variable or env entry that goes by
    // one of the setting's own names (ownNames): TOKENSVC_Db__Timeout, read by the .NET layer of Db:Timeout, says
    // nothing more than the key. Any other variable upstream, such as ${INTERNAL_URL} in URL, is checked.
    private bool IsSecretLayer(ProvenanceStep step, HashSet<string> ownNames) =>
        step.IsSensitive
        || (step.Kind != LayerKind.DotNetProvider
            && step.Reference?.ValueName() is { } name
            && !(step.Reference is EnvironmentVariableRef or WorkloadEnvRef && ownNames.Contains(EnvironmentName(name)))
            && (Options.SensitiveKeyPattern?.IsMatch(name) ?? false));

    // The names a setting goes by: its key, which is the variable itself in a container or Compose trace, and the
    // environment variable each of its .NET layers reads it from, with its prefix. __ is read as : and case is ignored.
    private static HashSet<string> OwnNames(string key, List<ProvenanceStep> steps)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { EnvironmentName(key) };
        foreach (var step in steps)
        {
            if (step.Reference is EnvironmentVariableRef variable)
            {
                names.Add(EnvironmentName(variable.Name));
            }
        }

        return names;
    }

    private static string EnvironmentName(string name) => name.Replace("__", ":", StringComparison.Ordinal);

    private bool IsSensitiveDeep(ProvenanceStep step, HashSet<string> ownNames) =>
        IsSecretLayer(step, ownNames) || step.Origins.Any(o => o.IsEffective && IsSensitiveDeep(o, ownNames));

    // Everything upstream of a secret layer holds the secret too, for example the Helm values file that rendered a
    // Secret key, so a sensitive step passes its sensitivity down to its origins. A step is also hidden when its winning
    // chain is secret (Deep), which is worked out on the way back, so each layer's name is matched once.
    private (ProvenanceStep Step, bool Deep) Redact(ProvenanceStep step, HashSet<string> ownNames, bool keySensitive, bool inherited)
    {
        var secretLayer = IsSecretLayer(step, ownNames);
        var secretSource = inherited || secretLayer;
        var deep = secretLayer;
        var origins = step.Origins;
        if (origins.Count > 0)
        {
            var hidden = new List<ProvenanceStep>(origins.Count);
            foreach (var origin in origins)
            {
                var (redacted, originDeep) = Redact(origin, ownNames, keySensitive, secretSource);
                hidden.Add(redacted);
                deep |= origin.IsEffective && originDeep;
            }

            origins = hidden;
        }

        var sensitive = keySensitive || secretSource || deep;
        return (step with
        {
            IsSensitive = sensitive,
            Value = sensitive && step.Value is not null ? Options.RedactedText : step.Value,
            Origins = origins,
        }, deep);
    }

    private static Dictionary<string, string?> ReadProcessEnvironment()
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            result[(string)entry.Key] = entry.Value as string;
        }

        return result;
    }

    // Counts the origin steps that resolvers return, so that a resolver that answers each step with several others,
    // level after level, cannot make one setting or the whole trace grow without end. Only the steps decide where a
    // trace is cut, never the time it takes.
    private sealed class StepBudget(int perSetting, int perTrace)
    {
        private int _settingSteps;
        private int _traceSteps;

        /// <summary>True when the setting being traced lost origin steps to either bound.</summary>
        public bool SettingCut { get; private set; }

        /// <summary>True when the setting being traced lost origin steps to its own bound.</summary>
        public bool SettingBoundReached { get; private set; }

        /// <summary>True once the trace-wide bound cut off a step. Every setting traced after that is cut off too.</summary>
        public bool TraceCut { get; private set; }

        public void StartSetting()
        {
            _settingSteps = 0;
            SettingCut = false;
            SettingBoundReached = false;
        }

        /// <summary>False when the setting is cut off, so its resolvers are not asked again.</summary>
        public bool MayResolve()
        {
            if (TraceCut)
            {
                SettingCut = true;
            }

            return !SettingCut;
        }

        /// <summary>
        /// Counts the chain a resolver returned and keeps as much of it as both bounds allow. A chain that does not
        /// fit is cut at its start, so the step that wins stays and the setting never passes its bound.
        /// </summary>
        public IReadOnlyList<ProvenanceStep> Take(IReadOnlyList<ProvenanceStep> chain)
        {
            var settingRoom = perSetting - _settingSteps;
            var traceRoom = perTrace - _traceSteps;
            var room = Math.Min(settingRoom, traceRoom);
            if (chain.Count > room)
            {
                SettingCut = true;
                if (traceRoom <= settingRoom)
                {
                    TraceCut = true;
                }
                else
                {
                    SettingBoundReached = true;
                }

                chain = [.. chain.Skip(chain.Count - room)];
            }

            _settingSteps += chain.Count;
            _traceSteps += chain.Count;
            return chain;
        }
    }
}
