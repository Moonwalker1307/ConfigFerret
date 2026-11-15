namespace ConfigFerret.Aws;

/// <summary>How to link configuration layers to Parameter Store parameters. Can be bound from a configuration section.</summary>
public sealed class AwsParametersTraceOptions
{
    /// <summary>
    /// When true (the default), an environment variable, Kubernetes env entry or ConfigMap/Secret key is linked to the
    /// parameter with the same name, or whose .NET key is the same: <c>Db__Timeout</c> to <c>/orders/prod/Db/Timeout</c>
    /// under <c>/orders/prod/</c>. These links are marked as inferred.
    /// </summary>
    public bool MatchByName { get; set; } = true;

    /// <summary>
    /// When true (the default), a key set by Amazon's Systems Manager configuration provider (<c>AddSystemsManager</c>) is
    /// linked to the parameter that maps to it. These links are marked as inferred.
    /// </summary>
    public bool MatchConfigurationKeys { get; set; } = true;

    /// <summary>
    /// Explicit links, from layer name (environment variable, env entry, ConfigMap/Secret key or .NET key) to full
    /// parameter name, for example <c>/orders/prod/Db/Timeout</c>. <see cref="Map"/> adds to it, and it can be bound
    /// from a <c>Links</c> configuration section.
    /// </summary>
    public IDictionary<string, string> Links { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Links the layer <paramref name="name"/> to the parameter <paramref name="parameterName"/>.</summary>
    public AwsParametersTraceOptions Map(string name, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        Links[name] = parameterName;
        return this;
    }

    internal string? FindLink(string? name) =>
        name is not null && Links.TryGetValue(name, out var link) && !string.IsNullOrWhiteSpace(link) ? link.Trim() : null;
}
