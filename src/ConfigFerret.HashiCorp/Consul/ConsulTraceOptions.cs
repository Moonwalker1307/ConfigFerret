namespace ConfigFerret.HashiCorp;

/// <summary>How to link configuration layers to Consul KV keys. Can be bound from a configuration section.</summary>
public sealed class ConsulTraceOptions
{
    /// <summary>
    /// When true (the default), an environment variable, Kubernetes env entry or ConfigMap/Secret key is linked to the
    /// Consul key that sets the same configuration key: <c>Db__Timeout</c> matches <c>config/orders/Db/Timeout</c> under
    /// prefix <c>config/orders/</c>. These links are marked as inferred.
    /// </summary>
    public bool MatchByName { get; set; } = true;

    /// <summary>
    /// Prefixes to strip from layer names before matching by name, for example <c>ORDERS_</c> when the app reads
    /// environment variables with <c>AddEnvironmentVariables("ORDERS_")</c>. Bindable from a <c>StripPrefixes</c> section.
    /// </summary>
    public IList<string> StripPrefixes { get; } = [];

    /// <summary>
    /// Explicit links, from layer name (environment variable, env entry or ConfigMap/Secret key) to a Consul key, either
    /// in full or relative to the prefix, optionally followed by <c>#</c> and a path inside its JSON value, for example
    /// <c>config/orders/appsettings#Db:Timeout</c>. <see cref="Map"/> adds to it, and it can be bound from a <c>Links</c> section.
    /// </summary>
    public IDictionary<string, string> Links { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Links the layer <paramref name="name"/> to the Consul key <paramref name="key"/>, for example <c>Map("DB_TIMEOUT", "config/orders/Db/Timeout")</c>.</summary>
    public ConsulTraceOptions Map(string name, string key, string? jsonPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Links[name] = string.IsNullOrEmpty(jsonPath) ? key : $"{key}#{jsonPath}";
        return this;
    }

    internal (string Key, string? JsonPath)? FindMapping(string name)
    {
        if (!Links.TryGetValue(name, out var link) || string.IsNullOrWhiteSpace(link))
        {
            return null;
        }

        var hash = link.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? (link.Trim(), null) : (link[..hash].Trim(), hash + 1 < link.Length ? link[(hash + 1)..] : null);
    }
}
