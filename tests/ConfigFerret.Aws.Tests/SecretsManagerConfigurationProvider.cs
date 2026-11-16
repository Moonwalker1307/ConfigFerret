using Microsoft.Extensions.Configuration;

namespace Kralizek.Extensions.Configuration.Internal;

/// <summary>Stands in for Kralizek's Secrets Manager configuration provider: it has the real full type name, which is all ConfigFerret sees.</summary>
internal sealed class SecretsManagerConfigurationProvider : ConfigurationProvider
{
    public SecretsManagerConfigurationProvider(Dictionary<string, string?> data) => Data = new Dictionary<string, string?>(data, StringComparer.OrdinalIgnoreCase);
}
