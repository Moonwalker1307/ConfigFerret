namespace ConfigFerret.Compose;

/// <summary>Links a trace to dotenv files and Docker Compose services.</summary>
public static class ComposeTracerExtensions
{
    /// <summary>
    /// Links the app's environment variables to a Docker Compose service: each <c>environment</c> entry and
    /// <c>env_file</c> line that sets one, and where each <c>${VAR}</c> in it came from. Files the app reads under
    /// <c>/run/secrets</c> (key-per-file) are linked to the service's <c>secrets</c>.
    /// </summary>
    /// <param name="tracer">The trace of the app's configuration.</param>
    /// <param name="project">The compose project.</param>
    /// <param name="service">The service the app runs as. Needed only when the project has more than one.</param>
    public static ConfigTracer WithCompose(this ConfigTracer tracer, ComposeProject project, string? service = null)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(project);
        return tracer.WithResolver(new ComposeResolver(project, service));
    }

    /// <summary>Links the app's environment variables to the dotenv lines that set them, across layered files such as <c>.env</c> and <c>.env.local</c>.</summary>
    public static ConfigTracer WithDotEnv(this ConfigTracer tracer, DotEnvLayers layers)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(layers);
        return tracer.WithResolver(new DotEnvResolver(layers));
    }
}
