namespace ConfigFerret.GitLab;

/// <summary>Adds GitLab CI/CD variables to a <see cref="ConfigTracer"/>.</summary>
public static class GitLabTracerExtensions
{
    /// <summary>
    /// Links environment variables, Kubernetes env entries and ConfigMap/Secret keys to the GitLab CI/CD variables in
    /// <paramref name="variables"/> that apply to the environment set with <paramref name="configure"/>, and to the
    /// variables set in the CI files read with it.
    /// </summary>
    /// <example>
    /// <code>
    /// var gitlab = await GitLabVariables.LoadAsync(new GitLabOptions
    /// {
    ///     Project = "contoso/api",
    ///     Token = Environment.GetEnvironmentVariable("GITLAB_TOKEN"),
    /// });
    /// var report = ConfigTracer.For(configuration)
    ///     .WithGitLab(gitlab, o => { o.Environment = "production"; o.AddCiFile(".gitlab-ci.yml"); })
    ///     .Trace();
    /// </code>
    /// </example>
    public static ConfigTracer WithGitLab(this ConfigTracer tracer, GitLabVariables variables, Action<GitLabTraceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(variables);
        var options = new GitLabTraceOptions();
        configure?.Invoke(options);
        return tracer.WithResolver(new GitLabResolver(variables, options));
    }

    /// <summary>
    /// Loads the GitLab CI/CD variables with the <see cref="GitLabSource"/> registered by <c>AddConfigFerretGitLab</c>,
    /// and links layers to them with the trace options bound from configuration.
    /// </summary>
    /// <example>
    /// <code>
    /// var tracer = ConfigTracer.For(builder.Configuration);
    /// await tracer.WithGitLabAsync(app.Services);
    /// Console.WriteLine(tracer.Trace().Explain("Db:Timeout"));
    /// </code>
    /// </example>
    public static async Task<ConfigTracer> WithGitLabAsync(this ConfigTracer tracer, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(services);
        var source = (GitLabSource?)services.GetService(typeof(GitLabSource))
            ?? throw new InvalidOperationException("GitLabSource is not registered. Call services.AddConfigFerretGitLab(configuration.GetSection(\"ConfigFerret:GitLab\")) first.");
        var variables = await source.LoadAsync(cancellationToken).ConfigureAwait(false);
        return tracer.WithResolver(new GitLabResolver(variables, source.TraceOptions));
    }
}
