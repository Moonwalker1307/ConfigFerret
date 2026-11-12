namespace ConfigFerret;

/// <summary>
/// Finds the upstream layers behind a step, for example the ConfigMap behind an environment variable.
/// Implement this to plug in another source of configuration.
/// </summary>
public interface IOriginResolver
{
    /// <summary>
    /// Returns the layers that produced <paramref name="layer"/>'s value, lowest priority first,
    /// or an empty list when this resolver knows nothing about it. The last entry is treated as the one that wins.
    /// </summary>
    IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics);
}

/// <summary>Collects warnings found while tracing, such as an ambiguous workload or a file that could not be read.</summary>
public sealed class TraceDiagnostics
{
    private readonly List<string> _warnings = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>The warnings, in the order they were found.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Adds a warning. Duplicate warnings are kept once.</summary>
    public void Warn(string message)
    {
        if (_seen.Add(message))
        {
            _warnings.Add(message);
        }
    }
}
