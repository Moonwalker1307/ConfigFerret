using ConfigFerret.DotNet;
using ConfigFerret.Helm;
using ConfigFerret.Kubernetes;
using ConfigFerret.Kustomize;
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
    private readonly List<IOriginResolver> _resolvers = [];

    private ConfigTracer(IConfigurationRoot root) => _root = root;

    private ConfigTracer(KubernetesResolver container) => _container = container;

    /// <summary>The trace options.</summary>
    public TraceOptions Options { get; private set; } = new();

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
        var layers = _root is not null ? ReadConfiguration(_root, diagnostics) : ReadContainer(_container!, diagnostics);

        var settings = new List<TracedSetting>(layers.Count);
        foreach (var (key, effectiveValue, steps) in layers)
        {
            var enriched = MarkEffective(steps.Select(s => Enrich(s, 1, diagnostics)).ToList());
            var keySensitive = Options.SensitiveKeyPattern?.IsMatch(key) ?? false;
            var effectiveSensitive = enriched.Count > 0 && IsSensitiveDeep(enriched[^1]);
            var sensitive = keySensitive || effectiveSensitive;

            var redacted = Options.RevealSensitiveValues
                ? enriched
                : enriched.Select(s => Redact(s, keySensitive, inherited: false)).ToList();

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

    private ProvenanceStep Enrich(ProvenanceStep step, int depth, TraceDiagnostics diagnostics)
    {
        if (depth > Options.MaxDepth || _resolvers.Count == 0)
        {
            return step;
        }

        var origins = new List<ProvenanceStep>();
        foreach (var resolver in _resolvers)
        {
            var chain = resolver.Resolve(step, diagnostics);
            if (chain.Count == 0)
            {
                continue;
            }

            origins.AddRange(MarkEffective(chain.Select(o => Enrich(o, depth + 1, diagnostics)).ToList()));
        }

        return origins.Count == 0 ? step : step with { Origins = [.. step.Origins, .. origins] };
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

    private static bool IsSensitiveDeep(ProvenanceStep step) =>
        step.IsSensitive || step.Origins.Any(o => o.IsEffective && IsSensitiveDeep(o));

    // Everything upstream of a secret layer holds the secret too, for example the Helm values file that rendered a
    // Secret key, so a sensitive step passes its sensitivity down to its origins.
    private ProvenanceStep Redact(ProvenanceStep step, bool keySensitive, bool inherited)
    {
        var secretSource = inherited || step.IsSensitive;
        var sensitive = keySensitive || secretSource || IsSensitiveDeep(step);
        var origins = step.Origins.Count == 0
            ? step.Origins
            : step.Origins.Select(o => Redact(o, keySensitive, secretSource)).ToList();
        return step with
        {
            IsSensitive = sensitive,
            Value = sensitive && step.Value is not null ? Options.RedactedText : step.Value,
            Origins = origins,
        };
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
}
