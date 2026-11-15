namespace ConfigFerret.Compose;

/// <summary>
/// Links an app's environment variables and mounted secret files to one Docker Compose service, and lists what the
/// service's containers get for <see cref="ConfigTracer.ForCompose"/>.
/// </summary>
internal sealed class ComposeResolver(ComposeProject project, string? serviceName) : IOriginResolver
{
    private ComposeService? _service;
    private bool _selected;

    public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (layer.Kind != LayerKind.DotNetProvider || Select(diagnostics) is not { } service)
        {
            return [];
        }

        var steps = layer.Reference switch
        {
            EnvironmentVariableRef env => service.Layers(env.Name),
            FileRef file => service.MountLayers(file.Path),
            _ => [],
        };
        if (steps.Count == 0 || layer.Value is null || steps[^1].IsRemoval || steps[^1].Value is null
            || string.Equals(layer.Value, steps[^1].Value, StringComparison.Ordinal))
        {
            return steps;
        }

        // Never print either value here: notes are not redacted.
        return
        [
            .. steps.Take(steps.Count - 1),
            steps[^1] with { Note = ComposeSteps.Join(steps[^1].Note, "the compose value differs from the value seen here, so the container may be out of date") },
        ];
    }

    /// <summary>What the service's containers get: every environment variable sorted by name, then every mounted file sorted by path.</summary>
    internal List<(string Key, string? Value, List<ProvenanceStep> Steps)> ReadLayers(TraceDiagnostics diagnostics)
    {
        var layers = new List<(string, string?, List<ProvenanceStep>)>();
        if (Select(diagnostics) is not { } service)
        {
            return layers;
        }

        layers.AddRange(service.Environment.Select(e => (e.Key, e.Value, service.Layers(e.Key).ToList())));
        layers.AddRange(service.Mounts.Select(m =>
        {
            var steps = service.MountLayers(m.Path).ToList();
            return (m.Path, steps[0].Value, steps);
        }));
        return layers;
    }

    private ComposeService? Select(TraceDiagnostics diagnostics)
    {
        if (_selected)
        {
            return _service;
        }

        _selected = true;
        foreach (var warning in project.Warnings)
        {
            diagnostics.Warn(warning);
        }

        if (project.ServiceNames.Count == 0)
        {
            diagnostics.Warn("The compose project has no services.");
            return null;
        }

        if (serviceName is null && project.ServiceNames.Count > 1)
        {
            diagnostics.Warn($"The compose project has {project.ServiceNames.Count} services; using {project.ServiceNames[0]}. Pass a service name to pick another.");
        }

        _service = project.FindService(serviceName ?? project.ServiceNames[0]);
        if (_service is null)
        {
            diagnostics.Warn($"No service named '{serviceName}' in the compose project. It has {string.Join(", ", project.ServiceNames)}.");
            return null;
        }

        foreach (var warning in _service.Warnings)
        {
            diagnostics.Warn(warning);
        }

        return _service;
    }
}
