using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using ConfigFerret.Sources;
using Microsoft.Extensions.Options;

namespace ConfigFerret.Azure;

/// <summary>
/// Loads Azure Key Vault secrets, with the options registered by <c>services.AddConfigFerretAzureKeyVault(...)</c> or
/// passed in. Also an <see cref="IConfigSource"/>, so hosts can load it like any other source.
/// </summary>
public sealed class AzureKeyVaultSource : IConfigSource
{
    private readonly Func<AzureKeyVaultOptions> _options;
    private readonly Func<AzureKeyVaultTraceOptions> _traceOptions;
    private readonly TokenCredential? _credential;

    /// <summary>Creates a source from options. Without <paramref name="credential"/>, it signs in as the options say.</summary>
    public AzureKeyVaultSource(AzureKeyVaultOptions options, AzureKeyVaultTraceOptions? traceOptions = null, TokenCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var trace = traceOptions ?? new AzureKeyVaultTraceOptions();
        _options = () => options;
        _traceOptions = () => trace;
        _credential = credential;
    }

    internal AzureKeyVaultSource(IOptions<AzureKeyVaultOptions> options, IOptions<AzureKeyVaultTraceOptions> traceOptions, TokenCredential? credential)
    {
        _options = () => options.Value;
        _traceOptions = () => traceOptions.Value;
        _credential = credential;
    }

    /// <summary>Which vault to read and how to sign in.</summary>
    public AzureKeyVaultOptions Options => _options();

    /// <summary>How to link layers to secrets.</summary>
    public AzureKeyVaultTraceOptions TraceOptions => _traceOptions();

    /// <inheritdoc />
    public string DisplayName => Options.VaultUri is { IsAbsoluteUri: true } uri ? $"Azure Key Vault {AzureAuth.ShortName(uri)}" : "Azure Key Vault";

    /// <summary>Lets tests answer requests without a network.</summary>
    internal SecretClientOptions? ClientOptions { get; set; }

    /// <summary>The credential this source signs in with.</summary>
    internal TokenCredential Credential => _credential ?? AzureAuth.Create(Options.TenantId, Options.ClientId, Options.ClientSecret);

    /// <summary>Reads the secrets. Every call reads them again.</summary>
    public Task<AzureKeyVaultSecrets> LoadSecretsAsync(CancellationToken cancellationToken = default) =>
        AzureKeyVaultSecrets.LoadAsync(Options, Credential, ClientOptions ?? new SecretClientOptions(), cancellationToken);

    /// <inheritdoc />
    public async Task<LoadedConfigSource> LoadAsync(CancellationToken cancellationToken = default) =>
        (await LoadSecretsAsync(cancellationToken).ConfigureAwait(false)).ToLoadedSource(TraceOptions);
}
