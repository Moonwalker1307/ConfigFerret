using Amazon.Runtime;

namespace ConfigFerret.Aws;

/// <summary>
/// How to reach AWS and sign in. Shared by <see cref="AwsSecretsManagerOptions"/> and <see cref="AwsParameterStoreOptions"/>.
/// Leave every credential unset to use the default AWS credential chain (environment variables, the shared config and
/// credentials files, SSO, web identity, container and instance roles).
/// </summary>
public abstract class AwsConnectionOptions
{
    /// <summary>The region, for example <c>eu-west-1</c>. Leave null to use the profile's or the environment's region (<c>AWS_REGION</c>).</summary>
    public string? Region { get; set; }

    /// <summary>A named profile from the shared AWS config and credentials files, for example <c>prod-readonly</c>.</summary>
    public string? Profile { get; set; }

    /// <summary>Credentials to use from code, for example from your own role assumption. Wins over every other credential setting. Not bound from configuration.</summary>
    public AWSCredentials? Credentials { get; set; }

    /// <summary>
    /// An access key id, used with <see cref="SecretAccessKey"/>. Prefer a profile or a role. Read it from user secrets or
    /// an environment variable (<c>AWS_ACCESS_KEY_ID</c>), never from a committed appsettings file.
    /// </summary>
    public string? AccessKeyId { get; set; }

    /// <summary>The secret access key for <see cref="AccessKeyId"/>. Never put it in an appsettings file. ConfigFerret never writes it to a report or exception.</summary>
    public string? SecretAccessKey { get; set; }

    /// <summary>The session token for temporary credentials, used with <see cref="AccessKeyId"/> and <see cref="SecretAccessKey"/>.</summary>
    public string? SessionToken { get; set; }

    /// <summary>
    /// Another endpoint, for example a VPC endpoint or LocalStack (<c>http://localhost:4566</c>). It must use https,
    /// except on loopback.
    /// </summary>
    public Uri? ServiceUrl { get; set; }
}
