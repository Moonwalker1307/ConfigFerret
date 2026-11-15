using ConfigFerret.Sources;
using Microsoft.Extensions.Options;

namespace ConfigFerret.KubernetesCluster;

/// <summary>
/// Reads a workload from a live cluster with the options registered by <c>services.AddConfigFerretKubernetesCluster(...)</c>,
/// or with options a host passes, for example from <see cref="KubernetesClusterSourceProvider"/>.
/// </summary>
public sealed class KubernetesClusterSource : IConfigSource
{
    private readonly Func<KubernetesClusterOptions> _options;

    /// <summary>Creates the source from options bound from configuration. Usually resolved from dependency injection.</summary>
    public KubernetesClusterSource(IOptions<KubernetesClusterOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = () => options.Value;
    }

    /// <summary>Creates the source from <paramref name="options"/>.</summary>
    public KubernetesClusterSource(KubernetesClusterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = () => options;
    }

    /// <summary>Which cluster, namespace and workload to read.</summary>
    public KubernetesClusterOptions Options => _options();

    /// <inheritdoc />
    public string DisplayName => $"Kubernetes {Options.Workload}" + (string.IsNullOrWhiteSpace(Options.Context) ? "" : $" ({Options.Context})");

    /// <summary>Lets tests answer requests without a cluster.</summary>
    internal DelegatingHandler[] Handlers { get; set; } = [];

    /// <summary>Lets tests replace the process environment (KUBECONFIG, HOME).</summary>
    internal Func<string, string?> GetEnvironmentVariable { get; set; } = Environment.GetEnvironmentVariable;

    /// <summary>Reads the workload and what it uses. Every call reads them again.</summary>
    public Task<KubernetesClusterSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default) =>
        KubernetesClusterReader.LoadAsync(Options, Handlers, GetEnvironmentVariable, cancellationToken);

    /// <inheritdoc />
    public async Task<LoadedConfigSource> LoadAsync(CancellationToken cancellationToken = default) =>
        (await LoadSnapshotAsync(cancellationToken).ConfigureAwait(false)).ToLoadedSource();
}
