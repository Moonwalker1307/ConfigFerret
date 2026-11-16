using Azure.Core;
using Azure.Data.AppConfiguration;
using ConfigFerret.Sources;
using Microsoft.Extensions.Options;

namespace ConfigFerret.Azure;

/// <summary>
/// Loads Azure App Configuration key-values, with the options registered by
/// <c>services.AddConfigFerretAzureAppConfiguration(...)</c> or passed in. Also an <see cref="IConfigSource"/>, so hosts
/// can load it like any other source.
/// </summary>
public sealed class AzureAppConfigurationSource : IConfigSource
{
    private readonly Func<AzureAppConfigurationOptions> _options;
    private readonly Func<AzureAppConfigurationTraceOptions> _traceOptions;
    private readonly TokenCredential? _credential;

    /// <summary>
    /// Creates a source from options. Without <paramref name="credential"/>, or without an endpoint, it signs in as the
    /// options say: with the connection string, the service principal or <c>DefaultAzureCredential</c>.
    /// </summary>
    public AzureAppConfigurationSource(AzureAppConfigurationOptions options, AzureAppConfigurationTraceOptions? traceOptions = null, TokenCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var trace = traceOptions ?? new AzureAppConfigurationTraceOptions();
        _options = () => options;
        _traceOptions = () => trace;
        _credential = credential;
    }

    internal AzureAppConfigurationSource(IOptions<AzureAppConfigurationOptions> options, IOptions<AzureAppConfigurationTraceOptions> traceOptions, TokenCredential? credential)
    {
        _options = () => options.Value;
        _traceOptions = () => traceOptions.Value;
        _credential = credential;
    }

    /// <summary>Which store to read and how to sign in.</summary>
    public AzureAppConfigurationOptions Options => _options();

    /// <summary>How to link layers to key-values.</summary>
    public AzureAppConfigurationTraceOptions TraceOptions => _traceOptions();

    /// <inheritdoc />
    public string DisplayName
    {
        get
        {
            var options = Options;
            if (options.Endpoint is { IsAbsoluteUri: true } uri)
            {
                return $"Azure App Configuration {AzureAuth.ShortName(uri)}";
            }

            try
            {
                return string.IsNullOrWhiteSpace(options.ConnectionString)
                    ? "Azure App Configuration"
                    : $"Azure App Configuration {AzureAuth.ShortName(AzureAppConfigurationSettings.ReadEndpoint(options.ConnectionString))}";
            }
            catch (ArgumentException)
            {
                return "Azure App Configuration";
            }
        }
    }

    /// <summary>Lets tests answer requests without a network.</summary>
    internal ConfigurationClientOptions? ClientOptions { get; set; }

    /// <summary>Reads the key-values. Every call reads them again.</summary>
    public Task<AzureAppConfigurationSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
        AzureAppConfigurationSettings.LoadAsync(Options, _credential, ClientOptions ?? new ConfigurationClientOptions(), cancellationToken);

    /// <inheritdoc />
    public async Task<LoadedConfigSource> LoadAsync(CancellationToken cancellationToken = default) =>
        (await LoadSettingsAsync(cancellationToken).ConfigureAwait(false)).ToLoadedSource(TraceOptions);
}
