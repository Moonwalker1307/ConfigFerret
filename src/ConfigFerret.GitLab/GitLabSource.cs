using ConfigFerret.Sources;
using Microsoft.Extensions.Options;

namespace ConfigFerret.GitLab;

/// <summary>
/// Loads GitLab CI/CD variables with the options registered by <c>services.AddConfigFerretGitLab(...)</c>, or with
/// options you pass. As an <see cref="IConfigSource"/>, it returns a snapshot ready to add to a trace.
/// </summary>
public sealed class GitLabSource : IConfigSource
{
    private readonly IOptions<GitLabOptions> _options;
    private readonly IOptions<GitLabTraceOptions> _traceOptions;

    /// <summary>Creates the source. Usually resolved from dependency injection.</summary>
    public GitLabSource(IOptions<GitLabOptions> options, IOptions<GitLabTraceOptions> traceOptions)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _traceOptions = traceOptions ?? throw new ArgumentNullException(nameof(traceOptions));
    }

    /// <summary>Creates the source from options you already have.</summary>
    public static GitLabSource Create(GitLabOptions options, GitLabTraceOptions? traceOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new GitLabSource(Microsoft.Extensions.Options.Options.Create(options), Microsoft.Extensions.Options.Options.Create(traceOptions ?? new GitLabTraceOptions()));
    }

    /// <summary>What to read and how to sign in, as bound from configuration.</summary>
    public GitLabOptions Options => _options.Value;

    /// <summary>How to link layers to variables, as bound from configuration.</summary>
    public GitLabTraceOptions TraceOptions => _traceOptions.Value;

    /// <inheritdoc/>
    public string DisplayName =>
        $"GitLab {Options.Project}{(TraceOptions.Environment is { Length: > 0 } environment ? $" ({environment})" : string.Empty)}";

    /// <summary>Lets tests answer requests without a network.</summary>
    internal HttpClient? HttpClient { get; set; }

    /// <summary>Reads the variables from GitLab. Every call reads them again.</summary>
    public Task<GitLabVariables> LoadAsync(CancellationToken cancellationToken = default) =>
        HttpClient is null
            ? GitLabVariables.LoadAsync(Options, cancellationToken)
            : GitLabVariables.LoadAsync(Options, HttpClient, cancellationToken);

    async Task<LoadedConfigSource> IConfigSource.LoadAsync(CancellationToken cancellationToken)
    {
        var variables = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var resolver = new GitLabResolver(variables, TraceOptions);
        return new LoadedConfigSource(DisplayName, resolver, resolver.Entries(), variables.Warnings);
    }
}
