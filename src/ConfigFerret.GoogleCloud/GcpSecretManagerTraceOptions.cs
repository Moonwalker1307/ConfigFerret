namespace ConfigFerret.GoogleCloud;

/// <summary>How to link configuration layers to Secret Manager secrets. Can be bound from a configuration section.</summary>
public sealed class GcpSecretManagerTraceOptions
{
    /// <summary>
    /// When true (the default), an environment variable, Kubernetes env entry or ConfigMap/Secret key is linked to the
    /// secret whose id gives the same configuration key the way .NET reads environment variables: <c>Db__Timeout</c> and
    /// <c>Db:Timeout</c> both match secret <c>Db__Timeout</c>. These links are marked as inferred.
    /// </summary>
    public bool MatchByName { get; set; } = true;

    /// <summary>
    /// Remove <see cref="GcpSecretManagerOptions.Prefix"/> from secret ids before matching by name, so secret
    /// <c>orders-Db__Timeout</c> read with prefix <c>orders-</c> matches <c>Db__Timeout</c>. Default true.
    /// </summary>
    public bool StripSecretPrefix { get; set; } = true;

    /// <summary>
    /// Prefixes to strip from layer names before matching by name, for example <c>ORDERS_</c> when the app reads
    /// environment variables with <c>AddEnvironmentVariables("ORDERS_")</c>. Bindable from a <c>StripPrefixes</c> section.
    /// </summary>
    public IList<string> StripPrefixes { get; } = [];

    /// <summary>
    /// Another separator in secret ids that stands for <c>:</c>, besides <c>__</c>, for example <c>--</c> when secrets
    /// are named like Azure Key Vault secrets (<c>Db--Timeout</c>). Null for none.
    /// </summary>
    public string? Separator { get; set; }

    /// <summary>
    /// Explicit links, from layer name (environment variable, env entry or ConfigMap/Secret key) to a secret id, optionally
    /// followed by <c>#property</c> for a key in a JSON secret, for example <c>orders-db#password</c>. <see cref="Map"/>
    /// adds to it, and it can be bound from a <c>Links</c> configuration section.
    /// </summary>
    public IDictionary<string, string> Links { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Links the layer <paramref name="name"/> to the secret <paramref name="secretId"/> and, optionally, the property
    /// <paramref name="property"/> of its JSON value, for example <c>Map("DB_PASSWORD", "orders-db", "password")</c>.
    /// </summary>
    public GcpSecretManagerTraceOptions Map(string name, string secretId, string? property = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretId);
        Links[name] = string.IsNullOrEmpty(property) ? secretId : $"{secretId}#{property}";
        return this;
    }

    internal (string SecretId, string? Property)? FindMapping(string name)
    {
        if (!Links.TryGetValue(name, out var link) || string.IsNullOrWhiteSpace(link))
        {
            return null;
        }

        var hash = link.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? (link.Trim(), null) : (link[..hash].Trim(), hash + 1 < link.Length ? link[(hash + 1)..] : null);
    }
}
