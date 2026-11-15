namespace ConfigFerret.Aws;

/// <summary>
/// What to read from AWS Systems Manager Parameter Store, and how to sign in. Can be bound from a configuration section,
/// see <c>services.AddConfigFerretAwsParameterStore(...)</c>.
/// </summary>
public sealed class AwsParameterStoreOptions : AwsConnectionOptions
{
    /// <summary>The path to read under, for example <c>/orders/prod/</c>. The same path you pass to <c>AddSystemsManager</c>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Read the whole hierarchy under <see cref="Path"/>, not only its direct children. Default true, like <c>AddSystemsManager</c>.</summary>
    public bool Recursive { get; set; } = true;

    /// <summary>
    /// Also decrypt <c>SecureString</c> values. Off by default: their names are read, but not their values.
    /// <c>String</c> and <c>StringList</c> values are always read. Decrypted values are marked sensitive and redacted in reports.
    /// </summary>
    public bool IncludeValues { get; set; }
}
