namespace ConfigFerret.Aws;

/// <summary>Adds AWS Secrets Manager secrets and Parameter Store parameters to a <see cref="ConfigTracer"/>.</summary>
public static class AwsTracerExtensions
{
    /// <summary>
    /// Links External Secrets Operator references, environment variables, Kubernetes env entries, ConfigMap/Secret keys
    /// and keys from a Secrets Manager configuration provider to the secrets in <paramref name="secrets"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// var secrets = await AwsSecrets.LoadAsync(new AwsSecretsManagerOptions { Region = "eu-west-1", NamePrefix = "prod/orders/" });
    /// var report = ConfigTracer.For(configuration).WithAwsSecretsManager(secrets).Trace();
    /// </code>
    /// </example>
    public static ConfigTracer WithAwsSecretsManager(this ConfigTracer tracer, AwsSecrets secrets, Action<AwsSecretsTraceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(secrets);
        var options = new AwsSecretsTraceOptions();
        configure?.Invoke(options);
        return tracer.WithResolver(new AwsSecretsResolver(secrets, options));
    }

    /// <summary>
    /// Links External Secrets Operator references, environment variables, Kubernetes env entries, ConfigMap/Secret keys
    /// and keys from <c>AddSystemsManager</c> to the parameters in <paramref name="parameters"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// var parameters = await AwsParameters.LoadAsync(new AwsParameterStoreOptions { Region = "eu-west-1", Path = "/orders/prod/" });
    /// var report = ConfigTracer.For(configuration).WithAwsParameterStore(parameters).Trace();
    /// </code>
    /// </example>
    public static ConfigTracer WithAwsParameterStore(this ConfigTracer tracer, AwsParameters parameters, Action<AwsParametersTraceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(parameters);
        var options = new AwsParametersTraceOptions();
        configure?.Invoke(options);
        return tracer.WithResolver(new AwsParametersResolver(parameters, options));
    }

    /// <summary>
    /// Loads the secrets with the <see cref="AwsSecretsManagerSource"/> registered by <c>AddConfigFerretAwsSecretsManager</c>,
    /// and links layers to them with the trace options bound from configuration.
    /// </summary>
    public static async Task<ConfigTracer> WithAwsSecretsManagerAsync(this ConfigTracer tracer, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(services);
        var source = (AwsSecretsManagerSource?)services.GetService(typeof(AwsSecretsManagerSource))
            ?? throw new InvalidOperationException("AwsSecretsManagerSource is not registered. Call services.AddConfigFerretAwsSecretsManager(configuration.GetSection(\"ConfigFerret:AwsSecretsManager\")) first.");
        var secrets = await source.LoadAsync(cancellationToken).ConfigureAwait(false);
        return tracer.WithResolver(new AwsSecretsResolver(secrets, source.TraceOptions));
    }

    /// <summary>
    /// Loads the parameters with the <see cref="AwsParameterStoreSource"/> registered by <c>AddConfigFerretAwsParameterStore</c>,
    /// and links layers to them with the trace options bound from configuration.
    /// </summary>
    public static async Task<ConfigTracer> WithAwsParameterStoreAsync(this ConfigTracer tracer, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(services);
        var source = (AwsParameterStoreSource?)services.GetService(typeof(AwsParameterStoreSource))
            ?? throw new InvalidOperationException("AwsParameterStoreSource is not registered. Call services.AddConfigFerretAwsParameterStore(configuration.GetSection(\"ConfigFerret:AwsParameterStore\")) first.");
        var parameters = await source.LoadAsync(cancellationToken).ConfigureAwait(false);
        return tracer.WithResolver(new AwsParametersResolver(parameters, source.TraceOptions));
    }
}
