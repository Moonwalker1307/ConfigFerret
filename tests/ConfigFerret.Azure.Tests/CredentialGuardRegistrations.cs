using ConfigFerret.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConfigFerret.Tests.Common;

public partial class CredentialGuardRegistrationTests
{
    private static partial IEnumerable<GuardedRegistration> Registrations() =>
    [
        new(
            nameof(AzureServiceCollectionExtensions.AddConfigFerretAzureKeyVault),
            "ConfigFerret:AzureKeyVault",
            new Dictionary<string, string?>
            {
                ["VaultUri"] = "https://contoso.vault.azure.net/",
                ["TenantId"] = "00000000-0000-0000-0000-000000000001",
                ["ClientId"] = "00000000-0000-0000-0000-000000000002",
                ["ClientSecret"] = "fake-client-secret",
            },
            ["ClientSecret"],
            (services, configuration) => services.AddConfigFerretAzureKeyVault(configuration),
            provider => provider.GetRequiredService<IOptions<AzureKeyVaultOptions>>().Value),
        new(
            nameof(AzureServiceCollectionExtensions.AddConfigFerretAzureAppConfiguration),
            "ConfigFerret:AzureAppConfiguration",
            new Dictionary<string, string?>
            {
                ["Endpoint"] = "https://contoso.azconfig.io",
                ["TenantId"] = "00000000-0000-0000-0000-000000000001",
                ["ClientId"] = "00000000-0000-0000-0000-000000000002",
                ["ClientSecret"] = "fake-client-secret",
            },
            ["ConnectionString", "ClientSecret"],
            (services, configuration) => services.AddConfigFerretAzureAppConfiguration(configuration),
            provider => provider.GetRequiredService<IOptions<AzureAppConfigurationOptions>>().Value),
    ];
}
