namespace ConfigFerret.Octopus;

/// <summary>
/// The deployment the configuration came from, used to pick which scoped Octopus variables apply, and how to link
/// configuration layers to Octopus variables.
/// </summary>
public sealed class OctopusTraceOptions
{
    /// <summary>The environment deployed to, for example <c>Production</c>.</summary>
    public string? Environment { get; set; }

    /// <summary>The deployment target (machine) name.</summary>
    public string? Machine { get; set; }

    /// <summary>The target tags (roles) of the deployment target.</summary>
    public ICollection<string> Roles { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The release channel.</summary>
    public string? Channel { get; set; }

    /// <summary>The deployment process step (action) that wrote the configuration.</summary>
    public string? Step { get; set; }

    /// <summary>The tenant tags, as canonical names such as <c>Region/West</c> or tag names such as <c>West</c>.</summary>
    public ICollection<string> TenantTags { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When true (the default), a key in a JSON file is linked to the Octopus variable with the same name, for example
    /// <c>Db:Timeout</c>, as Octopus structured configuration variables replace it. These links are marked as inferred.
    /// </summary>
    public bool MatchConfigurationKeys { get; set; } = true;

    /// <summary>
    /// When true (the default), an environment variable, Kubernetes env entry or ConfigMap/Secret key is linked to the
    /// Octopus variable with the same name. These links are marked as inferred.
    /// </summary>
    public bool MatchByName { get; set; } = true;

    /// <summary>
    /// Links the environment variable, env entry, ConfigMap/Secret key or configuration key <paramref name="name"/> to the
    /// Octopus variable <paramref name="variableName"/>, for example <c>Map("DB__Timeout", "Database.Timeout")</c>.
    /// </summary>
    public OctopusTraceOptions Map(string name, string variableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);
        Links[name] = variableName;
        return this;
    }

    /// <summary>
    /// The explicit links, from layer name (environment variable, env entry, ConfigMap/Secret key or configuration key)
    /// to Octopus variable name. <see cref="Map"/> adds to it, and it can be bound from a <c>Links</c> configuration section.
    /// </summary>
    public IDictionary<string, string> Links { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal string? FindMapping(string name) => Links.TryGetValue(name, out var variable) && !string.IsNullOrWhiteSpace(variable) ? variable : null;

    /// <summary>The context values for one Octopus scope dimension, or null when the dimension was not given.</summary>
    internal IReadOnlyCollection<string>? Context(string dimension) => dimension switch
    {
        "Environment" => One(Environment),
        "Machine" => One(Machine),
        "Role" => Roles.Count == 0 ? null : [.. Roles],
        "Channel" => One(Channel),
        "Action" => One(Step),
        "TenantTag" => TenantTags.Count == 0 ? null : [.. TenantTags],
        _ => null,
    };

    private static string[]? One(string? value) => string.IsNullOrWhiteSpace(value) ? null : [value];
}
