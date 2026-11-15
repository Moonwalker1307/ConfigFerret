namespace ConfigFerret.Compose;

/// <summary>Links an app's environment variables to the dotenv lines that set them.</summary>
internal sealed class DotEnvResolver(DotEnvLayers layers) : IOriginResolver
{
    private bool _warned;

    public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!_warned)
        {
            _warned = true;
            foreach (var warning in layers.Warnings)
            {
                diagnostics.Warn(warning);
            }
        }

        if (layer.Kind != LayerKind.DotNetProvider || layer.Reference is not EnvironmentVariableRef env)
        {
            return [];
        }

        var steps = layers.History(env.Name).Select(e => ComposeSteps.Entry(e, e.File, null)).ToList();
        if (steps.Count == 0 || layer.Value is null || string.Equals(layer.Value, steps[^1].Value, StringComparison.Ordinal))
        {
            return steps;
        }

        // The process may have the variable from somewhere else, such as the shell. Never print either value here:
        // notes are not redacted.
        steps[^1] = steps[^1] with
        {
            IsInferred = true,
            Note = ComposeSteps.Join(steps[^1].Note, "the dotenv value differs from the value seen here, so the variable may be set somewhere else too"),
        };
        return steps;
    }
}
