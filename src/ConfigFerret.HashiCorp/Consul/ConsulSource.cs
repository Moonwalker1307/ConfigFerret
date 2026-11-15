using ConfigFerret.Sources;
using Microsoft.Extensions.Options;

namespace ConfigFerret.HashiCorp;

/// <summary>
/// Loads Consul KV keys with the options registered by <c>services.AddConfigFerretConsul(...)</c>, or with options you
/// pass. As an <see cref="IConfigSource"/>, it returns a snapshot ready to add to a trace.
/// </summary>
public sealed class ConsulSource : IConfigSource
{
    private readonly IOptions<ConsulOptions> _options;
    private readonly IOptions<ConsulTraceOptions> _traceOptions;

    /// <summary>Creates the source. Usually resolved from dependency injection.</summary>
    public ConsulSource(IOptions<ConsulOptions> options, IOptions<ConsulTraceOptions> traceOptions)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _traceOptions = traceOptions ?? throw new ArgumentNullException(nameof(traceOptions));
    }

    /// <summary>Creates the source from options you already have.</summary>
    public static ConsulSource Create(ConsulOptions options, ConsulTraceOptions? traceOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ConsulSource(Microsoft.Extensions.Options.Options.Create(options), Microsoft.Extensions.Options.Options.Create(traceOptions ?? new ConsulTraceOptions()));
    }

    /// <summary>What to read and how to sign in, as bound from configuration.</summary>
    public ConsulOptions Options => _options.Value;

    /// <summary>How to link layers to keys, as bound from configuration.</summary>
    public ConsulTraceOptions TraceOptions => _traceOptions.Value;

    /// <inheritdoc/>
    public string DisplayName => $"Consul KV {Options.Address?.Authority ?? "(no address)"} {Options.Prefix}".TrimEnd();

    /// <summary>Lets tests answer requests without a network.</summary>
    internal HttpClient? HttpClient { get; set; }

    /// <summary>Reads the keys from Consul. Every call reads them again.</summary>
    public Task<ConsulKeyValues> LoadAsync(CancellationToken cancellationToken = default) =>
        HttpClient is null
            ? ConsulKeyValues.LoadAsync(Options, cancellationToken)
            : ConsulKeyValues.LoadAsync(Options, HttpClient, cancellationToken);

    async Task<LoadedConfigSource> IConfigSource.LoadAsync(CancellationToken cancellationToken)
    {
        var values = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var resolver = new ConsulResolver(values, TraceOptions);
        return new LoadedConfigSource(DisplayName, resolver, resolver.Entries(), values.Warnings);
    }
}
