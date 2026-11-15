namespace ConfigFerret.Aws;

/// <summary>How to link configuration layers to AWS Secrets Manager secrets. Can be bound from a configuration section.</summary>
public sealed class AwsSecretsTraceOptions
{
    /// <summary>
    /// When true (the default), an environment variable, Kubernetes env entry or ConfigMap/Secret key is linked to the
    /// secret with the same name, or to the secret (or JSON property) whose .NET key is the same, for example
    /// <c>Db__Password</c> to <c>prod/orders/Db/Password</c> or to property <c>Password</c> of <c>prod/orders/Db</c>.
    /// These links are marked as inferred.
    /// </summary>
    public bool MatchByName { get; set; } = true;

    /// <summary>
    /// When true (the default), a key set by a Secrets Manager configuration provider (Kralizek's
    /// <c>AddSecretsManager</c>, or Amazon's Systems Manager provider) is linked to the secret or JSON property that
    /// maps to it. These links are marked as inferred.
    /// </summary>
    public bool MatchConfigurationKeys { get; set; } = true;

    /// <summary>
    /// The part of secret names that is not part of the .NET key, for example <c>prod/orders/</c>, so
    /// <c>prod/orders/Db/Password</c> maps to <c>Db:Password</c>. Null uses the name prefix the secrets were listed
    /// with (<see cref="AwsSecretsManagerOptions.NamePrefix"/>). Matched case-insensitively.
    /// </summary>
    public string? KeyPrefix { get; set; }

    /// <summary>
    /// Explicit links, from layer name (environment variable, env entry, ConfigMap/Secret key or .NET key) to secret name
    /// or ARN. Add <c>#property</c> to link to one property of a JSON secret, for example <c>prod/orders/db#password</c>.
    /// <see cref="Map"/> adds to it, and it can be bound from a <c>Links</c> configuration section.
    /// </summary>
    public IDictionary<string, string> Links { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Links the layer <paramref name="name"/> to the secret <paramref name="secretName"/>, optionally to one JSON property.</summary>
    public AwsSecretsTraceOptions Map(string name, string secretName, string? property = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        Links[name] = string.IsNullOrEmpty(property) ? secretName : $"{secretName}#{property}";
        return this;
    }

    internal (string Secret, string? Property)? FindLink(string? name)
    {
        if (name is null || !Links.TryGetValue(name, out var link) || string.IsNullOrWhiteSpace(link))
        {
            return null;
        }

        link = link.Trim();
        var hash = link.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? (link, null) : (link[..hash], hash == link.Length - 1 ? null : link[(hash + 1)..]);
    }
}
