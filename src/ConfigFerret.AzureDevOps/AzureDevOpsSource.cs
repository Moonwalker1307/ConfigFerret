using ConfigFerret.Sources;
using Microsoft.Extensions.Options;

namespace ConfigFerret.AzureDevOps;

/// <summary>
/// Loads Azure Pipelines variable groups with the options registered by <c>services.AddConfigFerretAzureDevOps(...)</c>,
/// or with options you pass. As an <see cref="IConfigSource"/>, it returns a snapshot ready to add to a trace.
/// </summary>
public sealed class AzureDevOpsSource : IConfigSource
{
    private readonly IOptions<AzureDevOpsOptions> _options;
    private readonly IOptions<AzureDevOpsTraceOptions> _traceOptions;

    /// <summary>Creates the source. Usually resolved from dependency injection.</summary>
    public AzureDevOpsSource(IOptions<AzureDevOpsOptions> options, IOptions<AzureDevOpsTraceOptions> traceOptions)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _traceOptions = traceOptions ?? throw new ArgumentNullException(nameof(traceOptions));
    }

    /// <summary>Creates the source from options you already have.</summary>
    public static AzureDevOpsSource Create(AzureDevOpsOptions options, AzureDevOpsTraceOptions? traceOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new AzureDevOpsSource(Microsoft.Extensions.Options.Options.Create(options), Microsoft.Extensions.Options.Options.Create(traceOptions ?? new AzureDevOpsTraceOptions()));
    }

    /// <summary>What to read and how to sign in, as bound from configuration.</summary>
    public AzureDevOpsOptions Options => _options.Value;

    /// <summary>How to link layers to variables, as bound from configuration.</summary>
    public AzureDevOpsTraceOptions TraceOptions => _traceOptions.Value;

    /// <inheritdoc/>
    public string DisplayName =>
        Options.OrganizationUrl is { IsAbsoluteUri: true } url
            ? $"Azure DevOps {AzureDevOpsVariableGroups.OrganizationName(url)}/{Options.Project}"
            : $"Azure DevOps {Options.Project}";

    /// <summary>Lets tests answer requests without a network.</summary>
    internal HttpClient? HttpClient { get; set; }

    /// <summary>Reads the variable groups from Azure DevOps. Every call reads them again.</summary>
    public Task<AzureDevOpsVariableGroups> LoadAsync(CancellationToken cancellationToken = default) =>
        HttpClient is null
            ? AzureDevOpsVariableGroups.LoadAsync(Options, cancellationToken)
            : AzureDevOpsVariableGroups.LoadAsync(Options, HttpClient, cancellationToken);

    async Task<LoadedConfigSource> IConfigSource.LoadAsync(CancellationToken cancellationToken)
    {
        var groups = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var resolver = new AzureDevOpsResolver(groups, TraceOptions);
        return new LoadedConfigSource(DisplayName, resolver, resolver.Entries(), groups.Warnings);
    }
}
