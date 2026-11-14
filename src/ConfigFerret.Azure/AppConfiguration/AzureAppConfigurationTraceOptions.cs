namespace ConfigFerret.Azure;

/// <summary>How to link configuration layers to Azure App Configuration key-values. Can be bound from a configuration section.</summary>
public sealed class AzureAppConfigurationTraceOptions
{
    /// <summary>
    /// When true (the default), an environment variable, Kubernetes env entry or ConfigMap/Secret key is linked to the
    /// key-value with the same key, or that sets the same configuration key (<c>Db__Timeout</c> and <c>Db:Timeout</c>).
    /// These links are marked as inferred.
    /// </summary>
    public bool MatchByName { get; set; } = true;

    /// <summary>
    /// When true (the default), a value the App Configuration provider (<c>AddAzureAppConfiguration</c>) set in the app is
    /// linked to the key-values that set the same key. These links are marked as inferred, since the provider may read
    /// another store or other labels.
    /// </summary>
    public bool MatchConfigurationProvider { get; set; } = true;

    /// <summary>
    /// Explicit links, from layer name (environment variable, env entry or ConfigMap/Secret key) to App Configuration key,
    /// for example <c>DB_TIMEOUT</c> to <c>Orders:Db:Timeout</c>. Bindable from a <c>Links</c> configuration section.
    /// </summary>
    public IDictionary<string, string> Links { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Links the layer <paramref name="name"/> to the App Configuration key <paramref name="key"/>.</summary>
    public AzureAppConfigurationTraceOptions Map(string name, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Links[name] = key;
        return this;
    }
}
