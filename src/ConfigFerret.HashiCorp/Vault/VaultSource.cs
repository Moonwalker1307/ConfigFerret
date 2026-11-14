using ConfigFerret.Sources;
using Microsoft.Extensions.Options;

namespace ConfigFerret.HashiCorp;

/// <summary>
/// Loads HashiCorp Vault secrets with the options registered by <c>services.AddConfigFerretVault(...)</c>, or with
/// options you pass. As an <see cref="IConfigSource"/>, it returns a snapshot ready to add to a trace.
/// </summary>
public sealed class VaultSource : IConfigSource
{
    private readonly IOptions<VaultOptions> _options;
    private readonly IOptions<VaultTraceOptions> _traceOptions;

    /// <summary>Creates the source. Usually resolved from dependency injection.</summary>
    public VaultSource(IOptions<VaultOptions> options, IOptions<VaultTraceOptions> traceOptions)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _traceOptions = traceOptions ?? throw new ArgumentNullException(nameof(traceOptions));
    }

    /// <summary>Creates the source from options you already have.</summary>
    public static VaultSource Create(VaultOptions options, VaultTraceOptions? traceOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new VaultSource(Microsoft.Extensions.Options.Options.Create(options), Microsoft.Extensions.Options.Options.Create(traceOptions ?? new VaultTraceOptions()));
    }

    /// <summary>What to read and how to sign in, as bound from configuration.</summary>
    public VaultOptions Options => _options.Value;

    /// <summary>How to link layers to secrets, as bound from configuration.</summary>
    public VaultTraceOptions TraceOptions => _traceOptions.Value;

    /// <inheritdoc/>
    public string DisplayName => $"HashiCorp Vault {Options.Address?.Authority ?? "(no address)"} {Options.Mount}";

    /// <summary>Lets tests answer requests without a network.</summary>
    internal HttpClient? HttpClient { get; set; }

    /// <summary>Reads the secrets from Vault. Every call reads them again.</summary>
    public Task<VaultSecrets> LoadAsync(CancellationToken cancellationToken = default) =>
        HttpClient is null
            ? VaultSecrets.LoadAsync(Options, cancellationToken)
            : VaultSecrets.LoadAsync(Options, HttpClient, cancellationToken);

    async Task<LoadedConfigSource> IConfigSource.LoadAsync(CancellationToken cancellationToken)
    {
        var secrets = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var resolver = new VaultResolver(secrets, TraceOptions);
        return new LoadedConfigSource(DisplayName, resolver, resolver.Entries(), secrets.Warnings);
    }
}
