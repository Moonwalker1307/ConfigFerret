namespace ConfigFerret.HashiCorp;

/// <summary>Adds HashiCorp Vault secrets and Consul KV keys to a <see cref="ConfigTracer"/>.</summary>
public static class HashiCorpTracerExtensions
{
    /// <summary>
    /// Links External Secrets Operator and Secrets Store CSI references, environment variables, Kubernetes env entries and
    /// ConfigMap/Secret keys to the Vault secrets in <paramref name="secrets"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// var vault = await VaultSecrets.LoadAsync(new VaultOptions
    /// {
    ///     Address = new Uri("https://vault.example.com:8200/"),
    ///     Token = Environment.GetEnvironmentVariable("VAULT_TOKEN"),
    ///     Paths = { "orders/prod" },
    /// });
    /// var report = ConfigTracer.For(configuration).WithVault(vault).Trace();
    /// </code>
    /// </example>
    public static ConfigTracer WithVault(this ConfigTracer tracer, VaultSecrets secrets, Action<VaultTraceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(secrets);
        var options = new VaultTraceOptions();
        configure?.Invoke(options);
        return tracer.WithResolver(new VaultResolver(secrets, options));
    }

    /// <summary>
    /// Loads the Vault secrets with the <see cref="VaultSource"/> registered by <c>AddConfigFerretVault</c>, and links
    /// layers to them with the trace options bound from configuration.
    /// </summary>
    public static async Task<ConfigTracer> WithVaultAsync(this ConfigTracer tracer, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(services);
        var source = (VaultSource?)services.GetService(typeof(VaultSource))
            ?? throw new InvalidOperationException("VaultSource is not registered. Call services.AddConfigFerretVault(configuration.GetSection(\"ConfigFerret:Vault\")) first.");
        var secrets = await source.LoadAsync(cancellationToken).ConfigureAwait(false);
        return tracer.WithResolver(new VaultResolver(secrets, source.TraceOptions));
    }

    /// <summary>
    /// Links environment variables, Kubernetes env entries and ConfigMap/Secret keys to the Consul KV keys in
    /// <paramref name="values"/> that set the same configuration key.
    /// </summary>
    /// <example>
    /// <code>
    /// var consul = await ConsulKeyValues.LoadAsync(new ConsulOptions
    /// {
    ///     Address = new Uri("https://consul.example.com:8501/"),
    ///     Token = Environment.GetEnvironmentVariable("CONSUL_HTTP_TOKEN"),
    ///     Prefix = "config/orders/",
    /// });
    /// var report = ConfigTracer.For(configuration).WithConsul(consul).Trace();
    /// </code>
    /// </example>
    public static ConfigTracer WithConsul(this ConfigTracer tracer, ConsulKeyValues values, Action<ConsulTraceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(values);
        var options = new ConsulTraceOptions();
        configure?.Invoke(options);
        return tracer.WithResolver(new ConsulResolver(values, options));
    }

    /// <summary>
    /// Loads the Consul keys with the <see cref="ConsulSource"/> registered by <c>AddConfigFerretConsul</c>, and links
    /// layers to them with the trace options bound from configuration.
    /// </summary>
    public static async Task<ConfigTracer> WithConsulAsync(this ConfigTracer tracer, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(services);
        var source = (ConsulSource?)services.GetService(typeof(ConsulSource))
            ?? throw new InvalidOperationException("ConsulSource is not registered. Call services.AddConfigFerretConsul(configuration.GetSection(\"ConfigFerret:Consul\")) first.");
        var values = await source.LoadAsync(cancellationToken).ConfigureAwait(false);
        return tracer.WithResolver(new ConsulResolver(values, source.TraceOptions));
    }
}
