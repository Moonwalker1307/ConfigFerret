namespace ConfigFerret.AzureDevOps;

/// <summary>Adds Azure Pipelines variable groups to a <see cref="ConfigTracer"/>.</summary>
public static class AzureDevOpsTracerExtensions
{
    /// <summary>
    /// Links environment variables, Kubernetes env entries and ConfigMap/Secret keys to the variable group variables in
    /// <paramref name="groups"/> and to the variables set in the pipeline files read with <paramref name="configure"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// var groups = await AzureDevOpsVariableGroups.LoadAsync(new AzureDevOpsOptions
    /// {
    ///     OrganizationUrl = new Uri("https://dev.azure.com/contoso/"), Project = "api",
    ///     GroupNames = { "api-production" },
    ///     PersonalAccessToken = Environment.GetEnvironmentVariable("AZURE_DEVOPS_EXT_PAT"),
    /// });
    /// var report = ConfigTracer.For(configuration)
    ///     .WithAzureDevOps(groups, o => o.AddPipeline("azure-pipelines.yml"))
    ///     .Trace();
    /// </code>
    /// </example>
    public static ConfigTracer WithAzureDevOps(this ConfigTracer tracer, AzureDevOpsVariableGroups groups, Action<AzureDevOpsTraceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(groups);
        var options = new AzureDevOpsTraceOptions();
        configure?.Invoke(options);
        return tracer.WithResolver(new AzureDevOpsResolver(groups, options));
    }

    /// <summary>
    /// Loads the variable groups with the <see cref="AzureDevOpsSource"/> registered by <c>AddConfigFerretAzureDevOps</c>,
    /// and links layers to them with the trace options bound from configuration.
    /// </summary>
    /// <example>
    /// <code>
    /// var tracer = ConfigTracer.For(builder.Configuration);
    /// await tracer.WithAzureDevOpsAsync(app.Services);
    /// Console.WriteLine(tracer.Trace().Explain("Db:Timeout"));
    /// </code>
    /// </example>
    public static async Task<ConfigTracer> WithAzureDevOpsAsync(this ConfigTracer tracer, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(services);
        var source = (AzureDevOpsSource?)services.GetService(typeof(AzureDevOpsSource))
            ?? throw new InvalidOperationException("AzureDevOpsSource is not registered. Call services.AddConfigFerretAzureDevOps(configuration.GetSection(\"ConfigFerret:AzureDevOps\")) first.");
        var groups = await source.LoadAsync(cancellationToken).ConfigureAwait(false);
        return tracer.WithResolver(new AzureDevOpsResolver(groups, source.TraceOptions));
    }
}
