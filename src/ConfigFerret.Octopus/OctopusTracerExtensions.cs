namespace ConfigFerret.Octopus;

/// <summary>Adds Octopus Deploy variables to a <see cref="ConfigTracer"/>.</summary>
public static class OctopusTracerExtensions
{
    /// <summary>
    /// Links JSON configuration keys, environment variables, Kubernetes env entries and ConfigMap/Secret keys to the
    /// Octopus variables in <paramref name="variables"/> whose scope matches the deployment described in
    /// <paramref name="configure"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// var octopus = await OctopusVariables.LoadAsync(new OctopusVariablesOptions
    /// {
    ///     ServerUri = new Uri("https://contoso.octopus.app/"),
    ///     ApiKey = Environment.GetEnvironmentVariable("OCTOPUS_API_KEY")!,
    ///     Project = "api",
    /// });
    /// var report = ConfigTracer.For(configuration)
    ///     .WithOctopus(octopus, o => o.Environment = "Production")
    ///     .Trace();
    /// </code>
    /// </example>
    public static ConfigTracer WithOctopus(this ConfigTracer tracer, OctopusVariables variables, Action<OctopusTraceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(variables);
        var options = new OctopusTraceOptions();
        configure?.Invoke(options);
        return tracer.WithResolver(new OctopusResolver(variables, options));
    }
}
