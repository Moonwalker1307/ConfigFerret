using Microsoft.Extensions.Configuration;

namespace VaultSharp.Extensions.Configuration;

/// <summary>Stands in for the VaultSharp configuration provider: it has the real full type name, which is all ConfigFerret sees.</summary>
internal sealed class VaultConfigurationProvider(Dictionary<string, string?> data) : ConfigurationProvider, IConfigurationSource
{
    public override void Load() => Data = new Dictionary<string, string?>(data, StringComparer.OrdinalIgnoreCase);

    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;
}
