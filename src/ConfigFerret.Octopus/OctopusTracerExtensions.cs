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

    /// <summary>
    /// Loads the Octopus variables with the <see cref="OctopusVariablesSource"/> registered by
    /// <c>AddConfigFerretOctopus</c>, and matches them against the deployment bound from configuration.
    /// </summary>
    /// <example>
    /// <code>
    /// var tracer = ConfigTracer.For(builder.Configuration);
    /// await tracer.WithOctopusAsync(app.Services);
    /// Console.WriteLine(tracer.Trace().Explain("Db:Timeout"));
    /// </code>
    /// </example>
    public static async Task<ConfigTracer> WithOctopusAsync(this ConfigTracer tracer, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(services);
        var source = (OctopusVariablesSource?)services.GetService(typeof(OctopusVariablesSource))
            ?? throw new InvalidOperationException("OctopusVariablesSource is not registered. Call services.AddConfigFerretOctopus(configuration.GetSection(\"ConfigFerret:Octopus\")) first.");
        var variables = await source.LoadAsync(cancellationToken).ConfigureAwait(false);
        return tracer.WithResolver(new OctopusResolver(variables, source.TraceOptions));
    }
}
