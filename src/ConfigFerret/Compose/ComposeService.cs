namespace ConfigFerret.Compose;

/// <summary>One service of a <see cref="ComposeProject"/>, with the environment and files its containers get.</summary>
public sealed class ComposeService
{
    private readonly Dictionary<string, List<ProvenanceStep>> _layers;
    private readonly Dictionary<string, ProvenanceStep> _mounts;

    internal ComposeService(string name, Dictionary<string, List<ProvenanceStep>> layers, Dictionary<string, ProvenanceStep> mountSteps, List<ComposeMount> mounts, List<string> warnings)
    {
        Name = name;
        _layers = layers.ToDictionary(l => l.Key, l => MarkEffective(l.Value), StringComparer.Ordinal);
        _mounts = mountSteps;
        Mounts = [.. mounts.OrderBy(m => m.Path, StringComparer.Ordinal)];
        Warnings = warnings.Distinct(StringComparer.Ordinal).ToList();
        Environment = _layers
            .OrderBy(l => l.Key, StringComparer.Ordinal)
            .ToDictionary(l => l.Key, l => l.Value[^1].IsRemoval ? null : l.Value[^1].Value, StringComparer.Ordinal);
    }

    /// <summary>The service name.</summary>
    public string Name { get; }

    /// <summary>
    /// Each environment variable the container gets and its value, as <c>docker compose config</c> shows it. The
    /// value is null for a name listed without a value that neither the shell nor the project's env file sets.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Environment { get; }

    /// <summary>The secrets and configs mounted into the container as files, sorted by path.</summary>
    public IReadOnlyList<ComposeMount> Mounts { get; }

    /// <summary>Problems found while working out this service, such as an env_file that does not exist.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// Every layer that sets the environment variable <paramref name="name"/>, lowest priority first: each
    /// <c>env_file</c> line, then each <c>environment</c> entry. Each step's origins say where a <c>${VAR}</c> in it
    /// came from. Empty when the service does not set it.
    /// </summary>
    public IReadOnlyList<ProvenanceStep> Layers(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _layers.TryGetValue(name, out var steps) ? steps : [];
    }

    /// <summary>The secret or config mounted at <paramref name="path"/>, or an empty list.</summary>
    public IReadOnlyList<ProvenanceStep> MountLayers(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        // Try the path as listed first: a target can itself hold a backslash, and Mounts lists it unchanged. The path is
        // a file the traced app reads on this system, so only this system's own separator stands for a / in the
        // container: outside Windows a backslash is part of a file name.
        return _mounts.TryGetValue(path, out var step) || _mounts.TryGetValue(path.Replace(Path.DirectorySeparatorChar, '/'), out step) ? [step with { IsEffective = true }] : [];
    }

    private static List<ProvenanceStep> MarkEffective(List<ProvenanceStep> steps) =>
        steps.Select((s, i) => s with { IsEffective = i == steps.Count - 1 && !s.IsRemoval }).ToList();
}

/// <summary>A secret or config that Docker Compose mounts into a service's containers as a file.</summary>
/// <param name="Path">The absolute path in the container, for example <c>/run/secrets/db_password</c>.</param>
/// <param name="Name">The top-level secret or config name.</param>
/// <param name="IsSecret">True for a secret, false for a config.</param>
public sealed record ComposeMount(string Path, string Name, bool IsSecret);
