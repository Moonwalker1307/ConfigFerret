namespace ConfigFerret.Azure;

/// <summary>Adds Azure Key Vault secrets and App Configuration key-values to a <see cref="ConfigTracer"/>.</summary>
public static class AzureTracerExtensions
{
    /// <summary>
    /// Links environment variables, Kubernetes env entries, ConfigMap/Secret keys, External Secrets references, App
    /// Configuration Key Vault references and Key Vault configuration provider values to the secrets in <paramref name="secrets"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// var vault = await AzureKeyVaultSecrets.LoadAsync(new AzureKeyVaultOptions { VaultUri = new("https://contoso-prod.vault.azure.net/") });
    /// var report = ConfigTracer.For(configuration).WithAzureKeyVault(vault).Trace();
    /// </code>
    /// </example>
    public static ConfigTracer WithAzureKeyVault(this ConfigTracer tracer, AzureKeyVaultSecrets secrets, Action<AzureKeyVaultTraceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(secrets);
        var options = new AzureKeyVaultTraceOptions();
        configure?.Invoke(options);
        return tracer.WithResolver(new AzureKeyVaultResolver(secrets, options));
    }

    /// <summary>
    /// Loads the secrets with the <see cref="AzureKeyVaultSource"/> registered by <c>AddConfigFerretAzureKeyVault</c>, and
    /// links layers to them with the trace options bound from configuration.
    /// </summary>
    public static async Task<ConfigTracer> WithAzureKeyVaultAsync(this ConfigTracer tracer, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(services);
        var source = (AzureKeyVaultSource?)services.GetService(typeof(AzureKeyVaultSource))
            ?? throw new InvalidOperationException("AzureKeyVaultSource is not registered. Call services.AddConfigFerretAzureKeyVault(configuration.GetSection(\"ConfigFerret:AzureKeyVault\")) first.");
        var secrets = await source.LoadSecretsAsync(cancellationToken).ConfigureAwait(false);
        return tracer.WithResolver(new AzureKeyVaultResolver(secrets, source.TraceOptions));
    }

    /// <summary>
    /// Links environment variables, Kubernetes env entries, ConfigMap/Secret keys and App Configuration provider values to
    /// the key-values in <paramref name="settings"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// var store = await AzureAppConfigurationSettings.LoadAsync(new AzureAppConfigurationOptions { Endpoint = new("https://contoso.azconfig.io") });
    /// var report = ConfigTracer.For(configuration).WithAzureAppConfiguration(store).Trace();
    /// </code>
    /// </example>
    public static ConfigTracer WithAzureAppConfiguration(this ConfigTracer tracer, AzureAppConfigurationSettings settings, Action<AzureAppConfigurationTraceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(settings);
        var options = new AzureAppConfigurationTraceOptions();
        configure?.Invoke(options);
        return tracer.WithResolver(new AzureAppConfigurationResolver(settings, options));
    }

    /// <summary>
    /// Loads the key-values with the <see cref="AzureAppConfigurationSource"/> registered by
    /// <c>AddConfigFerretAzureAppConfiguration</c>, and links layers to them with the trace options bound from configuration.
    /// </summary>
    public static async Task<ConfigTracer> WithAzureAppConfigurationAsync(this ConfigTracer tracer, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(services);
        var source = (AzureAppConfigurationSource?)services.GetService(typeof(AzureAppConfigurationSource))
            ?? throw new InvalidOperationException("AzureAppConfigurationSource is not registered. Call services.AddConfigFerretAzureAppConfiguration(configuration.GetSection(\"ConfigFerret:AzureAppConfiguration\")) first.");
        var settings = await source.LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        return tracer.WithResolver(new AzureAppConfigurationResolver(settings, source.TraceOptions));
    }
}
