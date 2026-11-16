using Microsoft.Extensions.Configuration;

// Stand-ins for the configuration providers of secret-store packages. Each one has the full type name of the real
// provider, which is all ConfigFerret sees of it, so the tests need none of those packages.

namespace ConfigFerret.Tests
{
    internal abstract class FakeStoreProvider : ConfigurationProvider, IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

        /// <summary>Creates the stand-in with the full type name <paramref name="typeName"/>, holding <paramref name="key"/> = <paramref name="value"/>.</summary>
        public static FakeStoreProvider Create(string typeName, string key, string value)
        {
            var type = typeof(FakeStoreProvider).Assembly.GetType(typeName, throwOnError: true)!;
            var provider = (FakeStoreProvider)Activator.CreateInstance(type)!;
            provider.Set(key, value);
            return provider;
        }
    }

    /// <summary>A provider of the app's own, which ConfigFerret knows nothing about.</summary>
    internal sealed class SettingsServiceConfigurationProvider : FakeStoreProvider;

    /// <summary>An app's own provider built on the Key Vault one.</summary>
    internal sealed class CachingKeyVaultConfigurationProvider : global::Azure.Extensions.AspNetCore.Configuration.Secrets.AzureKeyVaultConfigurationProvider;
}

namespace Azure.Extensions.AspNetCore.Configuration.Secrets
{
    internal class AzureKeyVaultConfigurationProvider : ConfigFerret.Tests.FakeStoreProvider;
}

namespace Microsoft.Extensions.Configuration.AzureKeyVault
{
    internal sealed class AzureKeyVaultConfigurationProvider : ConfigFerret.Tests.FakeStoreProvider;
}

namespace Kralizek.Extensions.Configuration.Internal
{
    internal sealed class SecretsManagerConfigurationProvider : ConfigFerret.Tests.FakeStoreProvider;

    internal sealed class SecretsManagerDiscoveryConfigurationProvider : ConfigFerret.Tests.FakeStoreProvider;

    internal sealed class SecretsManagerKnownSecretConfigurationProvider : ConfigFerret.Tests.FakeStoreProvider;

    internal sealed class SecretsManagerKnownSecretsConfigurationProvider : ConfigFerret.Tests.FakeStoreProvider;
}

namespace VaultSharp.Extensions.Configuration
{
    internal sealed class VaultConfigurationProvider : ConfigFerret.Tests.FakeStoreProvider;
}
