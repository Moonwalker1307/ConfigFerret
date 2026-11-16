using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

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

/// <summary>Stands in for the App Configuration provider: ConfigFerret only sees its type name.</summary>
internal sealed class AzureAppConfigurationProvider(Dictionary<string, string?> data) : ConfigurationProvider, IConfigurationSource
{
    public override void Load() => Data = new Dictionary<string, string?>(data, StringComparer.OrdinalIgnoreCase);

    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;
}

/// <summary>A Key Vault provider of the app's own, which the core does not know: only its type name says it reads Key Vault.</summary>
internal sealed class CachedAzureKeyVaultProvider(Dictionary<string, string?> data) : ConfigurationProvider, IConfigurationSource
{
    public override void Load() => Data = new Dictionary<string, string?>(data, StringComparer.OrdinalIgnoreCase);

    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;
}

/// <summary>Environment variables from a dictionary instead of the test process, described as environment variables.</summary>
internal sealed class FakeEnvironmentVariables(Dictionary<string, string?> variables) : EnvironmentVariablesConfigurationProvider, IConfigurationSource
{
    public override void Load() => Data = new Dictionary<string, string?>(variables, StringComparer.OrdinalIgnoreCase);

    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
