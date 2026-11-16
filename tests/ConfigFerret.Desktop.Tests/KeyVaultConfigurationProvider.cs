using Microsoft.Extensions.Configuration;

namespace Azure.Extensions.AspNetCore.Configuration.Secrets;

/// <summary>Stands in for the Azure SDK's Key Vault configuration provider: it has the real full type name, which is all ConfigFerret sees.</summary>
internal sealed class AzureKeyVaultConfigurationProvider(Dictionary<string, string?> data) : ConfigurationProvider, IConfigurationSource
{
    public override void Load() => Data = new Dictionary<string, string?>(data, StringComparer.OrdinalIgnoreCase);

    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;
}
