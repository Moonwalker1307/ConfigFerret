namespace ConfigFerret.Aws;

/// <summary>The type of a Parameter Store parameter.</summary>
public enum AwsParameterType
{
    /// <summary>A plain string.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "The name AWS gives the type.")]
    String,

    /// <summary>A comma-separated list of strings.</summary>
    StringList,

    /// <summary>A string encrypted with KMS. Treated as secret.</summary>
    SecureString,

    /// <summary>A type ConfigFerret does not know. Treated as secret, to be safe.</summary>
    Unknown,
}

/// <summary>One AWS Systems Manager Parameter Store parameter.</summary>
/// <param name="Name">The full name, for example <c>/orders/prod/Db/Timeout</c>. AWS compares names case-sensitively.</param>
/// <param name="Type">String, StringList or SecureString.</param>
/// <param name="Value">The value. Null for a SecureString whose value was not decrypted.</param>
/// <param name="Version">The parameter version, when known.</param>
/// <param name="LastModified">When the parameter was last changed, when known.</param>
/// <param name="Arn">The parameter's ARN, when known.</param>
public sealed record AwsParameter(
    string Name,
    AwsParameterType Type,
    string? Value,
    long? Version = null,
    DateTimeOffset? LastModified = null,
    string? Arn = null)
{
    /// <summary>True for a SecureString (or unknown type) parameter.</summary>
    public bool IsSensitive => Type is AwsParameterType.SecureString or AwsParameterType.Unknown;

    /// <summary>The items of a StringList, split on commas the way <c>AddSystemsManager</c> does. Empty for other types.</summary>
    public IReadOnlyList<string> Items => Type == AwsParameterType.StringList && Value is not null ? Value.Split(',') : [];

    /// <summary>Where the parameter lives: the region and account from its ARN, when known.</summary>
    public string? Location => AwsArn.Describe(Arn);
}

/// <summary>A pointer to an AWS Systems Manager Parameter Store parameter.</summary>
/// <param name="Name">The full parameter name.</param>
public sealed record AwsParameterRef(string Name) : SourceRef
{
    /// <summary>The last part of the name: the folders above it are where the parameter is kept, not its name.</summary>
    protected override string? ValueName() => Name[(Name.LastIndexOf('/') + 1)..];
}
