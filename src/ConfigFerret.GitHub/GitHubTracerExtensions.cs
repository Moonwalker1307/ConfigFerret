namespace ConfigFerret.GitHub;

/// <summary>Adds GitHub Actions variables to a <see cref="ConfigTracer"/>.</summary>
public static class GitHubTracerExtensions
{
    /// <summary>
    /// Links environment variables, Kubernetes env entries and ConfigMap/Secret keys to the GitHub Actions variables
    /// and secrets in <paramref name="variables"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// var github = await GitHubVariables.LoadAsync(new GitHubVariablesOptions
    /// {
    ///     Owner = "contoso", Repository = "api", Environment = "production",
    ///     Token = Environment.GetEnvironmentVariable("GITHUB_TOKEN"),
    /// });
    /// var report = ConfigTracer.For(configuration)
    ///     .WithGitHub(github, o => o.AddWorkflow(".github/workflows/deploy.yml"))
    ///     .Trace();
    /// </code>
    /// </example>
    public static ConfigTracer WithGitHub(this ConfigTracer tracer, GitHubVariables variables, Action<GitHubTraceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(variables);
        var options = new GitHubTraceOptions();
        configure?.Invoke(options);
        return tracer.WithResolver(new GitHubResolver(variables, options));
    }

    /// <summary>
    /// Loads the GitHub Actions variables with the <see cref="GitHubVariablesSource"/> registered by
    /// <c>AddConfigFerretGitHub</c>, and links layers to them with the trace options bound from configuration.
    /// </summary>
    /// <example>
    /// <code>
    /// var tracer = ConfigTracer.For(builder.Configuration);
    /// await tracer.WithGitHubAsync(app.Services);
    /// Console.WriteLine(tracer.Trace().Explain("Db:Timeout"));
    /// </code>
    /// </example>
    public static async Task<ConfigTracer> WithGitHubAsync(this ConfigTracer tracer, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(services);
        var source = (GitHubVariablesSource?)services.GetService(typeof(GitHubVariablesSource))
            ?? throw new InvalidOperationException("GitHubVariablesSource is not registered. Call services.AddConfigFerretGitHub(configuration.GetSection(\"ConfigFerret:GitHub\")) first.");
        var variables = await source.LoadAsync(cancellationToken).ConfigureAwait(false);
        return tracer.WithResolver(new GitHubResolver(variables, source.TraceOptions));
    }
}
