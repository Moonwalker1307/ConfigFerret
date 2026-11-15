namespace ConfigFerret.GoogleCloud;

/// <summary>Adds Google Cloud Secret Manager secrets to a <see cref="ConfigTracer"/>.</summary>
public static class GoogleCloudTracerExtensions
{
    /// <summary>
    /// Links External Secrets Operator and Secrets Store CSI references, environment variables, Kubernetes env entries and
    /// ConfigMap/Secret keys to the Secret Manager secrets in <paramref name="secrets"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// var gcp = await GcpSecrets.LoadAsync(new GcpSecretManagerOptions { ProjectId = "contoso-prod" });
    /// var report = ConfigTracer.For(configuration).WithGcpSecretManager(gcp).Trace();
    /// </code>
    /// </example>
    public static ConfigTracer WithGcpSecretManager(this ConfigTracer tracer, GcpSecrets secrets, Action<GcpSecretManagerTraceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(secrets);
        var options = new GcpSecretManagerTraceOptions();
        configure?.Invoke(options);
        return tracer.WithResolver(new GcpSecretManagerResolver(secrets, options));
    }

    /// <summary>
    /// Loads the secrets with the <see cref="GcpSecretManagerSource"/> registered by <c>AddConfigFerretGcpSecretManager</c>,
    /// and links layers to them with the trace options bound from configuration.
    /// </summary>
    public static async Task<ConfigTracer> WithGcpSecretManagerAsync(this ConfigTracer tracer, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(services);
        var source = (GcpSecretManagerSource?)services.GetService(typeof(GcpSecretManagerSource))
            ?? throw new InvalidOperationException("GcpSecretManagerSource is not registered. Call services.AddConfigFerretGcpSecretManager(configuration.GetSection(\"ConfigFerret:GcpSecretManager\")) first.");
        var secrets = await source.LoadAsync(cancellationToken).ConfigureAwait(false);
        return tracer.WithResolver(new GcpSecretManagerResolver(secrets, source.TraceOptions));
    }
}
