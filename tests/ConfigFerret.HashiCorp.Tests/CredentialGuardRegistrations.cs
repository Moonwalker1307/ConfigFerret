using ConfigFerret.HashiCorp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConfigFerret.Tests.Common;

public partial class CredentialGuardRegistrationTests
{
    private static partial IEnumerable<GuardedRegistration> Registrations() =>
    [
        new(
            nameof(HashiCorpServiceCollectionExtensions.AddConfigFerretVault),
            "ConfigFerret:Vault",
            new Dictionary<string, string?> { ["Address"] = "https://vault.example.com/", ["Token"] = "fake-token" },
            ["Token"],
            (services, configuration) => services.AddConfigFerretVault(configuration),
            provider => provider.GetRequiredService<IOptions<VaultOptions>>().Value),
        new(
            nameof(HashiCorpServiceCollectionExtensions.AddConfigFerretConsul),
            "ConfigFerret:Consul",
            new Dictionary<string, string?> { ["Address"] = "https://consul.example.com/", ["Token"] = "fake-token" },
            ["Token"],
            (services, configuration) => services.AddConfigFerretConsul(configuration),
            provider => provider.GetRequiredService<IOptions<ConsulOptions>>().Value),
    ];
}
