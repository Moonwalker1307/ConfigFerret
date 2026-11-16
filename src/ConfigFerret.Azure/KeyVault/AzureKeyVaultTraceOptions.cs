namespace ConfigFerret.Azure;

/// <summary>How to link configuration layers to Azure Key Vault secrets. Can be bound from a configuration section.</summary>
public sealed class AzureKeyVaultTraceOptions
{
    /// <summary>
    /// When true (the default), an environment variable, Kubernetes env entry or ConfigMap/Secret key is linked to the
    /// secret with the same name, or to the secret the Key Vault configuration provider maps to the same key
    /// (<c>Db__Password</c> and <c>Db--Password</c> both set <c>Db:Password</c>). These links are marked as inferred.
    /// </summary>
    public bool MatchByName { get; set; } = true;

    /// <summary>
    /// When true (the default), a value the Azure Key Vault configuration provider
    /// (<c>AddAzureKeyVault</c>) set in the app is linked to the secret it maps to the same key. These links are marked as
    /// inferred, since the provider may read another vault. This only decides the link: a value the provider set is hidden
    /// either way.
    /// </summary>
    public bool MatchConfigurationProvider { get; set; } = true;

    /// <summary>
    /// Explicit links, from layer name (environment variable, env entry or ConfigMap/Secret key) to secret name, for
    /// example <c>DB_PASSWORD</c> to <c>orders-db-password</c>. Bindable from a <c>Links</c> configuration section.
    /// </summary>
    public IDictionary<string, string> Links { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The clock used to tell whether a secret has expired. Tests replace it.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Links the layer <paramref name="name"/> to the secret <paramref name="secretName"/>.</summary>
    public AzureKeyVaultTraceOptions Map(string name, string secretName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        Links[name] = secretName;
        return this;
    }
}
