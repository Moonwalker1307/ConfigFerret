using ConfigFerret.Kubernetes;
using ConfigFerret.Sources;

namespace ConfigFerret.KubernetesCluster;

/// <summary>What <see cref="KubernetesClusterReader.LoadAsync(KubernetesClusterOptions, CancellationToken)"/> read from a cluster.</summary>
public sealed class KubernetesClusterSnapshot
{
    internal KubernetesClusterSnapshot(
        KubernetesManifests manifests, string @namespace, string? context, Workload workload, string? container, bool includesSecrets, IEnumerable<string> warnings)
    {
        Manifests = manifests;
        Namespace = @namespace;
        Context = context;
        Workload = workload;
        Container = container;
        IncludesSecrets = includesSecrets;
        Warnings = [.. warnings];
        Entries = ReadEntries();
    }

    /// <summary>
    /// The workload and what it uses, as core manifests: pass them to <c>ConfigTracer.ForContainer</c> to trace the
    /// container from outside, or use <see cref="CreateResolver"/> to link the app's own configuration to them.
    /// </summary>
    public KubernetesManifests Manifests { get; }

    /// <summary>The namespace that was read.</summary>
    public string Namespace { get; }

    /// <summary>The kubeconfig context that was used, or null for the in-cluster service account.</summary>
    public string? Context { get; }

    /// <summary>The workload that was read.</summary>
    public Workload Workload { get; }

    /// <summary>The container to trace, when one was named.</summary>
    public string? Container { get; }

    /// <summary>True when the workload's Secrets were read, so their keys and values are known.</summary>
    public bool IncludesSecrets { get; }

    /// <summary>Problems found while reading, such as an object the credential could not read. They never hold a value.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Every ConfigMap key and Secret key read, and every Secret key an ExternalSecret or SecretProviderClass writes.</summary>
    public IReadOnlyList<SourceEntry> Entries { get; }

    /// <summary>A name for what was read, for example <c>Kubernetes Deployment/api (prod, orders)</c>.</summary>
    public string DisplayName => $"Kubernetes {Workload.DisplayName} ({(Context is null ? "in-cluster" : Context)}, {Namespace})";

    /// <summary>Creates the resolver that links environment variables and mounted files to what was read.</summary>
    public IOriginResolver CreateResolver() => new WarningResolver(Manifests.CreateResolver(Workload.Name, Container), Warnings);

    /// <summary>The snapshot as a <see cref="LoadedConfigSource"/>, for <see cref="ConfigSourceTracerExtensions.WithSource"/> and <see cref="LoadedConfigSource.FindUnused"/>.</summary>
    public LoadedConfigSource ToLoadedSource() => new(DisplayName, CreateResolver(), Entries, Warnings);

    private List<SourceEntry> ReadEntries()
    {
        var location = $"{(Context is null ? "" : Context + "/")}{Namespace}";
        var entries = new List<SourceEntry>();
        var seen = new HashSet<SourceRef>();
        void Add(string key, SourceRef reference, bool sensitive, string detail)
        {
            if (seen.Add(reference))
            {
                entries.Add(new SourceEntry(key, reference, sensitive, detail, location));
            }
        }

        foreach (var (name, data) in Manifests.ConfigMaps.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            foreach (var key in data.Data.Keys.Order(StringComparer.Ordinal))
            {
                Add(key, new ConfigMapKeyRef(name, key), sensitive: false, $"ConfigMap {name}");
            }
        }

        foreach (var (name, data) in Manifests.Secrets.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            foreach (var key in data.Data.Keys.Order(StringComparer.Ordinal))
            {
                Add(key, new SecretKeyRef(name, key), sensitive: true, $"Secret {name}");
            }
        }

        foreach (var es in Manifests.ExternalSecrets)
        {
            foreach (var item in es.Data)
            {
                Add(item.SecretKey, new SecretKeyRef(es.TargetSecret, item.SecretKey), sensitive: true, $"Secret {es.TargetSecret}, written by ExternalSecret {es.Name}");
            }
        }

        foreach (var spc in Manifests.SecretProviderClasses.Values.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            foreach (var synced in spc.SecretObjects)
            {
                foreach (var key in synced.Keys)
                {
                    Add(key.Key, new SecretKeyRef(synced.SecretName, key.Key), sensitive: true, $"Secret {synced.SecretName}, synced by SecretProviderClass {spc.Name}");
                }
            }
        }

        return entries;
    }

    // Adds what went wrong while reading the cluster to the trace's warnings, once.
    private sealed class WarningResolver(IOriginResolver inner, IReadOnlyList<string> warnings) : IOriginResolver
    {
        private bool _warned;

        public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostics);
            if (!_warned)
            {
                _warned = true;
                foreach (var warning in warnings)
                {
                    diagnostics.Warn(warning);
                }
            }

            return inner.Resolve(layer, diagnostics);
        }
    }
}
