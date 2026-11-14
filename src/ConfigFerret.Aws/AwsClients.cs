using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.SecretsManager;
using Amazon.SimpleSystemsManagement;

namespace ConfigFerret.Aws;

/// <summary>Builds AWS SDK clients from <see cref="AwsConnectionOptions"/>.</summary>
internal static class AwsClients
{
    public static AmazonSecretsManagerClient CreateSecretsManager(AwsConnectionOptions options, Action<ClientConfig>? configure = null)
    {
        var config = new AmazonSecretsManagerConfig();
        var credentials = Configure(config, options, configure);
        return credentials is null ? new AmazonSecretsManagerClient(config) : new AmazonSecretsManagerClient(credentials, config);
    }

    public static AmazonSimpleSystemsManagementClient CreateParameterStore(AwsConnectionOptions options, Action<ClientConfig>? configure = null)
    {
        var config = new AmazonSimpleSystemsManagementConfig();
        var credentials = Configure(config, options, configure);
        return credentials is null ? new AmazonSimpleSystemsManagementClient(config) : new AmazonSimpleSystemsManagementClient(credentials, config);
    }

    /// <summary>Returns the problem with a service URL, or null when it is safe to send signed requests to.</summary>
    public static string? CheckServiceUrl(Uri? uri, string key)
    {
        if (uri is null)
        {
            return null;
        }

        if (!uri.IsAbsoluteUri)
        {
            return $"{key} must be an absolute URL.";
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            return $"{key} must use https. Plain http is allowed only on loopback, for example http://localhost:4566 for LocalStack.";
        }

        return string.IsNullOrEmpty(uri.UserInfo) ? null : $"{key} must not hold credentials.";
    }

    /// <summary>Returns the problem with the access key settings, or null when they fit together.</summary>
    public static string? CheckAccessKeys(AwsConnectionOptions options, string sectionPath)
    {
        var hasId = !string.IsNullOrWhiteSpace(options.AccessKeyId);
        var hasSecret = !string.IsNullOrWhiteSpace(options.SecretAccessKey);
        if (hasId == hasSecret && (hasId || string.IsNullOrWhiteSpace(options.SessionToken)))
        {
            return null;
        }

        return $"{Key(sectionPath, nameof(options.AccessKeyId))} and {Key(sectionPath, nameof(options.SecretAccessKey))} must be set together "
            + "(and SessionToken only with both). Leave all of them unset to use the default AWS credential chain or a profile.";
    }

    private static string Key(string sectionPath, string name) => string.IsNullOrEmpty(sectionPath) ? name : $"{sectionPath}:{name}";

    private static AWSCredentials? Configure(ClientConfig config, AwsConnectionOptions options, Action<ClientConfig>? configure)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (CheckAccessKeys(options, string.Empty) is { } keys)
        {
            throw new ArgumentException(keys, nameof(options));
        }

        if (CheckServiceUrl(options.ServiceUrl, nameof(options.ServiceUrl)) is { } url)
        {
            throw new ArgumentException(url, nameof(options));
        }

        // Signed requests must never be replayed to another host.
        config.AllowAutoRedirect = false;
        var region = Blank(options.Region);
        var credentials = options.Credentials;
        if (credentials is null && Blank(options.AccessKeyId) is { } id)
        {
            var secret = options.SecretAccessKey!.Trim();
            credentials = Blank(options.SessionToken) is { } token ? new SessionAWSCredentials(id, secret, token) : new BasicAWSCredentials(id, secret);
        }

        if (Blank(options.Profile) is { } profileName)
        {
            var chain = new CredentialProfileStoreChain();
            if (!chain.TryGetProfile(profileName, out var profile))
            {
                throw new InvalidOperationException($"AWS profile {profileName} was not found in the shared config or credentials file.");
            }

            region ??= profile.Region?.SystemName;
            if (credentials is null && !chain.TryGetAWSCredentials(profileName, out credentials))
            {
                throw new InvalidOperationException($"AWS profile {profileName} has no usable credentials.");
            }
        }

        if (options.ServiceUrl is { } serviceUrl)
        {
            config.ServiceURL = serviceUrl.AbsoluteUri;
            config.AuthenticationRegion = region ?? "us-east-1";
        }
        else if (region is not null)
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }

        configure?.Invoke(config);
        return credentials;
    }

    /// <summary>Where requests go, for the report: the region, or the service URL.</summary>
    public static string Describe(IClientConfig config) =>
        config.RegionEndpoint?.SystemName
        ?? (string.IsNullOrEmpty(config.ServiceURL) ? null : config.ServiceURL)
        ?? config.AuthenticationRegion
        ?? "default region";

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
