using ConfigFerret.Sources;

namespace ConfigFerret.Aws;

/// <summary>Describes the AWS Secrets Manager source to hosts such as the desktop app and the command-line tool.</summary>
public sealed class AwsSecretsManagerProvider : IConfigSourceProvider
{
    /// <inheritdoc/>
    public string Id => "aws-secrets-manager";

    /// <inheritdoc/>
    public string DisplayName => "AWS Secrets Manager";

    /// <inheritdoc/>
    public SourceCategory Category => SourceCategory.SecretStore;

    /// <inheritdoc/>
    public IReadOnlyList<SourceSetting> Settings { get; } =
    [
        .. AwsSettings.Connection,
        new("NamePrefix", "Name prefix", Help: "Read only secrets whose name starts with this, for example prod/orders/."),
        new("KeyPrefix", "Key prefix", Help: "The part of secret names that is not part of the .NET key. Empty uses the name prefix."),
        new("IncludeValues", "Read values", SourceSettingKind.Boolean, Help: "Also read secret values, to show JSON properties and compare values. Values are always redacted.", DefaultValue: "false"),
        new("MatchByName", "Match by name", SourceSettingKind.Boolean, Help: "Link environment variables and keys to secrets with the same name or .NET key.", DefaultValue: "true"),
        .. AwsSettings.Credentials,
    ];

    /// <inheritdoc/>
    public IConfigSource Create(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var options = new AwsSecretsManagerOptions
        {
            NamePrefix = AwsSettings.Text(values, "NamePrefix"),
            IncludeValues = AwsSettings.Boolean(values, "IncludeValues", false),
        };
        AwsSettings.Apply(values, options);
        var trace = new AwsSecretsTraceOptions
        {
            KeyPrefix = AwsSettings.Text(values, "KeyPrefix"),
            MatchByName = AwsSettings.Boolean(values, "MatchByName", true),
        };
        return new AwsSecretsManagerSource(options, trace);
    }
}

/// <summary>Describes the AWS Systems Manager Parameter Store source to hosts such as the desktop app and the command-line tool.</summary>
public sealed class AwsParameterStoreProvider : IConfigSourceProvider
{
    /// <inheritdoc/>
    public string Id => "aws-parameter-store";

    /// <inheritdoc/>
    public string DisplayName => "AWS Systems Manager Parameter Store";

    /// <inheritdoc/>
    public SourceCategory Category => SourceCategory.ConfigurationService;

    /// <inheritdoc/>
    public IReadOnlyList<SourceSetting> Settings { get; } =
    [
        new("Path", "Path", Required: true, Help: "The path to read under, the same one passed to AddSystemsManager, for example /orders/prod/."),
        .. AwsSettings.Connection,
        new("Recursive", "Recursive", SourceSettingKind.Boolean, Help: "Read the whole hierarchy under the path.", DefaultValue: "true"),
        new("IncludeValues", "Decrypt SecureString values", SourceSettingKind.Boolean, Help: "Also decrypt SecureString values. String values are always read. Values are always redacted.", DefaultValue: "false"),
        new("MatchByName", "Match by name", SourceSettingKind.Boolean, Help: "Link environment variables and keys to parameters with the same name or .NET key.", DefaultValue: "true"),
        .. AwsSettings.Credentials,
    ];

    /// <inheritdoc/>
    public IConfigSource Create(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var path = AwsSettings.Text(values, "Path") ?? throw new ArgumentException("Path is required, for example /orders/prod/.", nameof(values));
        if (!path.StartsWith('/'))
        {
            throw new ArgumentException("Path must start with /, for example /orders/prod/.", nameof(values));
        }

        var options = new AwsParameterStoreOptions
        {
            Path = path,
            Recursive = AwsSettings.Boolean(values, "Recursive", true),
            IncludeValues = AwsSettings.Boolean(values, "IncludeValues", false),
        };
        AwsSettings.Apply(values, options);
        var trace = new AwsParametersTraceOptions { MatchByName = AwsSettings.Boolean(values, "MatchByName", true) };
        return new AwsParameterStoreSource(options, trace);
    }
}

/// <summary>The settings both AWS providers share.</summary>
internal static class AwsSettings
{
    public static readonly SourceSetting[] Connection =
    [
        new("Region", "Region", Help: "For example eu-west-1. Empty uses the profile's or the environment's region."),
        new("Profile", "Profile", Help: "A named profile from the shared AWS config and credentials files. Empty uses the default credential chain."),
        new("ServiceUrl", "Service URL", SourceSettingKind.Url, Help: "Another endpoint, such as a VPC endpoint or LocalStack. Must use https except on loopback."),
    ];

    public static readonly SourceSetting[] Credentials =
    [
        new("AccessKeyId", "Access key id", SourceSettingKind.Secret, Help: "Only when not using a profile or the default credential chain.", EnvironmentVariables: ["AWS_ACCESS_KEY_ID"]),
        new("SecretAccessKey", "Secret access key", SourceSettingKind.Secret, Help: "The secret for the access key id.", EnvironmentVariables: ["AWS_SECRET_ACCESS_KEY"]),
        new("SessionToken", "Session token", SourceSettingKind.Secret, Help: "For temporary credentials.", EnvironmentVariables: ["AWS_SESSION_TOKEN"]),
    ];

    public static string? Text(IReadOnlyDictionary<string, string?> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    public static bool Boolean(IReadOnlyDictionary<string, string?> values, string name, bool defaultValue) =>
        Text(values, name) switch
        {
            null => defaultValue,
            var text when bool.TryParse(text, out var parsed) => parsed,
            _ => throw new ArgumentException($"{name} must be true or false.", nameof(values)),
        };

    public static void Apply(IReadOnlyDictionary<string, string?> values, AwsConnectionOptions options)
    {
        options.Region = Text(values, "Region");
        options.Profile = Text(values, "Profile");
        options.AccessKeyId = Text(values, "AccessKeyId");
        options.SecretAccessKey = Text(values, "SecretAccessKey");
        options.SessionToken = Text(values, "SessionToken");
        if (Text(values, "ServiceUrl") is { } url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                throw new ArgumentException("ServiceUrl must be an absolute URL.", nameof(values));
            }

            options.ServiceUrl = uri;
        }

        if ((AwsClients.CheckServiceUrl(options.ServiceUrl, "ServiceUrl") ?? AwsClients.CheckAccessKeys(options, string.Empty)) is { } problem)
        {
            throw new ArgumentException(problem, nameof(values));
        }
    }
}
