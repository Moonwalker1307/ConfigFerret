using Microsoft.Extensions.Options;

namespace ConfigFerret.GitHub;

/// <summary>
/// Loads GitHub Actions variables with the options registered by
/// <c>services.AddConfigFerretGitHub(...)</c>.
/// </summary>
public sealed class GitHubVariablesSource
{
    private readonly IOptions<GitHubVariablesOptions> _options;
    private readonly IOptions<GitHubTraceOptions> _traceOptions;

    /// <summary>Creates the source. Usually resolved from dependency injection.</summary>
    public GitHubVariablesSource(IOptions<GitHubVariablesOptions> options, IOptions<GitHubTraceOptions> traceOptions)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _traceOptions = traceOptions ?? throw new ArgumentNullException(nameof(traceOptions));
    }

    /// <summary>What to read and how to sign in, as bound from configuration.</summary>
    public GitHubVariablesOptions Options => _options.Value;

    /// <summary>How to link layers to variables, as bound from configuration.</summary>
    public GitHubTraceOptions TraceOptions => _traceOptions.Value;

    /// <summary>Lets tests answer requests without a network.</summary>
    internal HttpClient? HttpClient { get; set; }

    /// <summary>Reads the variables and secret names from GitHub. Every call reads them again.</summary>
    public Task<GitHubVariables> LoadAsync(CancellationToken cancellationToken = default) =>
        HttpClient is null
            ? GitHubVariables.LoadAsync(Options, cancellationToken)
            : GitHubVariables.LoadAsync(Options, HttpClient, cancellationToken);
}
