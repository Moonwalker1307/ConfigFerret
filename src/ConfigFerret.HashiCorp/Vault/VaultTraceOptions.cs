namespace ConfigFerret.HashiCorp;

/// <summary>How to link configuration layers to HashiCorp Vault secrets. Can be bound from a configuration section.</summary>
public sealed class VaultTraceOptions
{
    /// <summary>
    /// When true (the default), an environment variable, Kubernetes env entry or ConfigMap/Secret key is linked to the
    /// Vault key or secret whose path below the configured path gives the same configuration key: <c>Db__Timeout</c> and
    /// <c>Db:Timeout</c> match key <c>Db__Timeout</c> or <c>Db:Timeout</c> in the configured secret, key <c>Timeout</c> in
    /// secret <c>Db</c> below it, or secret <c>Db/Timeout</c>. These links are marked as inferred.
    /// </summary>
    public bool MatchByName { get; set; } = true;

    /// <summary>
    /// Prefixes to strip from layer names before matching by name, for example <c>ORDERS_</c> when the app reads
    /// environment variables with <c>AddEnvironmentVariables("ORDERS_")</c>. Bindable from a <c>StripPrefixes</c> section.
    /// </summary>
    public IList<string> StripPrefixes { get; } = [];

    /// <summary>
    /// Explicit links, from layer name (environment variable, env entry or ConfigMap/Secret key) to a secret path inside
    /// the mount, optionally followed by <c>#key</c>, for example <c>orders/prod/db#password</c>. <see cref="Map"/> adds to
    /// it, and it can be bound from a <c>Links</c> configuration section.
    /// </summary>
    public IDictionary<string, string> Links { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Links the layer <paramref name="name"/> to the secret at <paramref name="path"/> and, optionally, the key
    /// <paramref name="key"/> inside it, for example <c>Map("DB_PASSWORD", "orders/prod/db", "password")</c>.
    /// </summary>
    public VaultTraceOptions Map(string name, string path, string? key = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Links[name] = string.IsNullOrEmpty(key) ? path : $"{path}#{key}";
        return this;
    }

    internal (string Path, string? Key)? FindMapping(string name)
    {
        if (!Links.TryGetValue(name, out var link) || string.IsNullOrWhiteSpace(link))
        {
            return null;
        }

        var hash = link.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? (link.Trim(), null) : (link[..hash].Trim(), hash + 1 < link.Length ? link[(hash + 1)..] : null);
    }
}
