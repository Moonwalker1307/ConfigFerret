namespace ConfigFerret.Terraform;

/// <summary>How to link configuration layers to Terraform outputs. Can be bound from a configuration section.</summary>
public sealed class TerraformTraceOptions
{
    /// <summary>
    /// When true (the default), an environment variable, Kubernetes env entry or ConfigMap/Secret key is linked to the
    /// output with the same name: exactly, then ignoring case, then treating <c>_</c>, <c>__</c>, <c>.</c> and <c>:</c>
    /// as the same separator, so <c>DB_TIMEOUT</c>, <c>Db__Timeout</c> and <c>db.timeout</c> all match <c>db_timeout</c>.
    /// These links are marked as inferred.
    /// </summary>
    public bool MatchByName { get; set; } = true;

    /// <summary>
    /// Explicit links, from layer name (environment variable, env entry or ConfigMap/Secret key) to output name, for
    /// example <c>ORDERS_DB_HOST</c> to <c>db.host</c> (or <c>db:host</c>) for a value nested in an object output.
    /// These win over name matching. Bindable from a <c>Links</c> configuration section.
    /// </summary>
    public IDictionary<string, string> Links { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Links the layer <paramref name="name"/> to the output <paramref name="output"/>.</summary>
    public TerraformTraceOptions Map(string name, string output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        Links[name] = output;
        return this;
    }
}
