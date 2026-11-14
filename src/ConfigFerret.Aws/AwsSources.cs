using Amazon.Runtime;
using ConfigFerret.Sources;

namespace ConfigFerret.Aws;

/// <summary>
/// Loads AWS Secrets Manager secrets with fixed options. Resolve it from dependency injection after
/// <c>services.AddConfigFerretAwsSecretsManager(...)</c>, create it from code, or get it from <see cref="AwsSecretsManagerProvider"/>.
/// </summary>
public sealed class AwsSecretsManagerSource : IConfigSource
{
    /// <summary>Creates the source.</summary>
    public AwsSecretsManagerSource(AwsSecretsManagerOptions options, AwsSecretsTraceOptions? traceOptions = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        TraceOptions = traceOptions ?? new AwsSecretsTraceOptions();
    }

    /// <summary>What to read and how to sign in.</summary>
    public AwsSecretsManagerOptions Options { get; }

    /// <summary>How to link layers to secrets.</summary>
    public AwsSecretsTraceOptions TraceOptions { get; }

    /// <inheritdoc/>
    public string DisplayName =>
        $"AWS Secrets Manager {(string.IsNullOrWhiteSpace(Options.Region) ? "(default region)" : Options.Region.Trim())}"
        + (string.IsNullOrEmpty(Options.NamePrefix) ? string.Empty : $" {Options.NamePrefix}*");

    /// <summary>Lets tests answer requests without a network, for example by setting <see cref="ClientConfig.HttpClientFactory"/>.</summary>
    internal Action<ClientConfig>? ConfigureClient { get; set; }

    /// <summary>Reads the secrets. Every call reads them again.</summary>
    /// <exception cref="AmazonClientException">AWS refused the request or could not be reached.</exception>
    public async Task<AwsSecrets> LoadAsync(CancellationToken cancellationToken = default)
    {
        using var client = AwsClients.CreateSecretsManager(Options, ConfigureClient);
        return await AwsSecrets.LoadAsync(Options, client, cancellationToken).ConfigureAwait(false);
    }

    async Task<LoadedConfigSource> IConfigSource.LoadAsync(CancellationToken cancellationToken) =>
        (await LoadAsync(cancellationToken).ConfigureAwait(false)).ToLoadedSource(DisplayName, TraceOptions);
}

/// <summary>
/// Loads AWS Systems Manager Parameter Store parameters with fixed options. Resolve it from dependency injection after
/// <c>services.AddConfigFerretAwsParameterStore(...)</c>, create it from code, or get it from <see cref="AwsParameterStoreProvider"/>.
/// </summary>
public sealed class AwsParameterStoreSource : IConfigSource
{
    /// <summary>Creates the source.</summary>
    public AwsParameterStoreSource(AwsParameterStoreOptions options, AwsParametersTraceOptions? traceOptions = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        TraceOptions = traceOptions ?? new AwsParametersTraceOptions();
    }

    /// <summary>What to read and how to sign in.</summary>
    public AwsParameterStoreOptions Options { get; }

    /// <summary>How to link layers to parameters.</summary>
    public AwsParametersTraceOptions TraceOptions { get; }

    /// <inheritdoc/>
    public string DisplayName =>
        $"AWS Parameter Store {(string.IsNullOrWhiteSpace(Options.Region) ? "(default region)" : Options.Region.Trim())} {Options.Path}".TrimEnd();

    /// <summary>Lets tests answer requests without a network, for example by setting <see cref="ClientConfig.HttpClientFactory"/>.</summary>
    internal Action<ClientConfig>? ConfigureClient { get; set; }

    /// <summary>Reads the parameters. Every call reads them again.</summary>
    /// <exception cref="AmazonClientException">AWS refused the request or could not be reached.</exception>
    public async Task<AwsParameters> LoadAsync(CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Options.Path);
        using var client = AwsClients.CreateParameterStore(Options, ConfigureClient);
        return await AwsParameters.LoadAsync(Options, client, cancellationToken).ConfigureAwait(false);
    }

    async Task<LoadedConfigSource> IConfigSource.LoadAsync(CancellationToken cancellationToken) =>
        (await LoadAsync(cancellationToken).ConfigureAwait(false)).ToLoadedSource(DisplayName, TraceOptions);
}
