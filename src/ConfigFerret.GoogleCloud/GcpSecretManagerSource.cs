using ConfigFerret.Sources;
using Microsoft.Extensions.Options;

namespace ConfigFerret.GoogleCloud;

/// <summary>
/// Loads Google Cloud Secret Manager secrets with the options registered by
/// <c>services.AddConfigFerretGcpSecretManager(...)</c>, or with options you pass. As an <see cref="IConfigSource"/>, it
/// returns a snapshot ready to add to a trace.
/// </summary>
public sealed class GcpSecretManagerSource : IConfigSource
{
    private readonly IOptions<GcpSecretManagerOptions> _options;
    private readonly IOptions<GcpSecretManagerTraceOptions> _traceOptions;

    /// <summary>Creates the source. Usually resolved from dependency injection.</summary>
    public GcpSecretManagerSource(IOptions<GcpSecretManagerOptions> options, IOptions<GcpSecretManagerTraceOptions> traceOptions)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _traceOptions = traceOptions ?? throw new ArgumentNullException(nameof(traceOptions));
    }

    /// <summary>Creates the source from options you already have.</summary>
    public static GcpSecretManagerSource Create(GcpSecretManagerOptions options, GcpSecretManagerTraceOptions? traceOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new GcpSecretManagerSource(
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Options.Options.Create(traceOptions ?? new GcpSecretManagerTraceOptions()));
    }

    /// <summary>What to read and how to sign in, as bound from configuration.</summary>
    public GcpSecretManagerOptions Options => _options.Value;

    /// <summary>How to link layers to secrets, as bound from configuration.</summary>
    public GcpSecretManagerTraceOptions TraceOptions => _traceOptions.Value;

    /// <inheritdoc/>
    public string DisplayName =>
        $"Google Cloud Secret Manager {(string.IsNullOrWhiteSpace(Options.ProjectId) ? "(no project)" : Options.ProjectId.Trim())}"
        + (string.IsNullOrWhiteSpace(Options.Location) ? string.Empty : " " + Options.Location.Trim());

    /// <summary>Lets tests answer requests without a network.</summary>
    internal HttpClient? HttpClient { get; set; }

    /// <summary>Reads the secrets from Secret Manager. Every call reads them again.</summary>
    public Task<GcpSecrets> LoadAsync(CancellationToken cancellationToken = default) =>
        HttpClient is null
            ? GcpSecrets.LoadAsync(Options, cancellationToken)
            : GcpSecrets.LoadAsync(Options, HttpClient, cancellationToken);

    async Task<LoadedConfigSource> IConfigSource.LoadAsync(CancellationToken cancellationToken)
    {
        var secrets = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var resolver = new GcpSecretManagerResolver(secrets, TraceOptions);
        return new LoadedConfigSource(DisplayName, resolver, resolver.Entries(), secrets.Warnings);
    }
}
