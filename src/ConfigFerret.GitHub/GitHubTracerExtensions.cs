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
}
