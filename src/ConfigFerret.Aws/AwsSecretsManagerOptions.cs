namespace ConfigFerret.Aws;

/// <summary>
/// What to read from AWS Secrets Manager, and how to sign in. Can be bound from a configuration section, see
/// <c>services.AddConfigFerretAwsSecretsManager(...)</c>.
/// </summary>
public sealed class AwsSecretsManagerOptions : AwsConnectionOptions
{
    /// <summary>
    /// Read only secrets whose name starts with this, for example <c>prod/orders/</c>. AWS matches it case-insensitively.
    /// Null reads every secret the credential can list.
    /// </summary>
    public string? NamePrefix { get; set; }

    /// <summary>
    /// Also read secret values, so JSON secrets show their properties and values can be compared. Off by default: only
    /// names and metadata are read. Values are always marked sensitive and redacted in reports.
    /// </summary>
    public bool IncludeValues { get; set; }
}
