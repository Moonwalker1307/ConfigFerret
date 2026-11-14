using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Azure.Tests;

internal static class TestLayers
{
    public static ProvenanceStep Env(string name, string? value = null) => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = $"environment variable {name}",
        Value = value,
        Reference = new EnvironmentVariableRef(name),
    };

    public static ProvenanceStep Provider(string source, string key, string? value = null) => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = source,
        Value = value,
        ConfigurationKey = key,
    };

    public static ProvenanceStep External(string? provider, string remoteKey, string? property = null, string? version = null, string? store = "azure-store") => new()
    {
        Kind = LayerKind.KubernetesSecret,
        Source = "ExternalSecret db",
        Reference = new ExternalSecretRef(provider, store, remoteKey, property, version),
    };
}

/// <summary>Stands in for the Azure SDK's Key Vault configuration provider: ConfigFerret only sees its type name.</summary>
internal sealed class AzureKeyVaultConfigurationProvider(Dictionary<string, string?> data) : ConfigurationProvider, IConfigurationSource
{
    public override void Load() => Data = new Dictionary<string, string?>(data, StringComparer.OrdinalIgnoreCase);

    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;
}

/// <summary>Stands in for the App Configuration provider: ConfigFerret only sees its type name.</summary>
internal sealed class AzureAppConfigurationProvider(Dictionary<string, string?> data) : ConfigurationProvider, IConfigurationSource
{
    public override void Load() => Data = new Dictionary<string, string?>(data, StringComparer.OrdinalIgnoreCase);

    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
