using Microsoft.Extensions.Options;

namespace ConfigFerret.Octopus;

/// <summary>Loads Octopus Deploy variables with the options registered by <c>services.AddConfigFerretOctopus(...)</c>.</summary>
public sealed class OctopusVariablesSource
{
    private readonly IOptions<OctopusVariablesOptions> _options;
    private readonly IOptions<OctopusTraceOptions> _traceOptions;

    /// <summary>Creates the source. Usually resolved from dependency injection.</summary>
    public OctopusVariablesSource(IOptions<OctopusVariablesOptions> options, IOptions<OctopusTraceOptions> traceOptions)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _traceOptions = traceOptions ?? throw new ArgumentNullException(nameof(traceOptions));
    }

    /// <summary>What to read and how to sign in, as bound from configuration.</summary>
    public OctopusVariablesOptions Options => _options.Value;

    /// <summary>The deployment to match scopes against and how to link layers, as bound from configuration.</summary>
    public OctopusTraceOptions TraceOptions => _traceOptions.Value;

    /// <summary>Lets tests answer requests without a network.</summary>
    internal HttpClient? HttpClient { get; set; }

    /// <summary>Reads the project's variables from Octopus. Every call reads them again.</summary>
    public Task<OctopusVariables> LoadAsync(CancellationToken cancellationToken = default) =>
        HttpClient is null
            ? OctopusVariables.LoadAsync(Options, cancellationToken)
            : OctopusVariables.LoadAsync(Options, HttpClient, cancellationToken);
}
