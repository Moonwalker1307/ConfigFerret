namespace ConfigFerret.KubernetesCluster;

/// <summary>Adds a workload read from a live cluster to a <see cref="ConfigTracer"/>.</summary>
public static class KubernetesClusterTracerExtensions
{
    /// <summary>
    /// Links environment variables and mounted files to the workload, ConfigMaps, Secrets, ExternalSecrets and
    /// SecretProviderClasses in <paramref name="cluster"/>. To trace the container from outside instead, pass
    /// <see cref="KubernetesClusterSnapshot.Manifests"/> to <c>ConfigTracer.ForContainer</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// var cluster = await KubernetesClusterReader.LoadAsync(new KubernetesClusterOptions { Context = "prod", Workload = "deployment/api" });
    /// var report = ConfigTracer.For(configuration).WithKubernetesCluster(cluster).Trace();
    /// </code>
    /// </example>
    public static ConfigTracer WithKubernetesCluster(this ConfigTracer tracer, KubernetesClusterSnapshot cluster)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(cluster);
        return tracer.WithResolver(cluster.CreateResolver());
    }

    /// <summary>
    /// Reads the workload with the <see cref="KubernetesClusterSource"/> registered by
    /// <c>AddConfigFerretKubernetesCluster</c>, and links layers to it.
    /// </summary>
    /// <example>
    /// <code>
    /// var tracer = ConfigTracer.For(builder.Configuration);
    /// await tracer.WithKubernetesClusterAsync(app.Services);
    /// Console.WriteLine(tracer.Trace().Explain("Db:Timeout"));
    /// </code>
    /// </example>
    public static async Task<ConfigTracer> WithKubernetesClusterAsync(this ConfigTracer tracer, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(services);
        var source = (KubernetesClusterSource?)services.GetService(typeof(KubernetesClusterSource))
            ?? throw new InvalidOperationException("KubernetesClusterSource is not registered. Call services.AddConfigFerretKubernetesCluster(configuration.GetSection(\"ConfigFerret:KubernetesCluster\")) first.");
        var cluster = await source.LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return tracer.WithKubernetesCluster(cluster);
    }
}
