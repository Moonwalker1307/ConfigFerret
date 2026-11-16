using ConfigFerret.GoogleCloud;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConfigFerret.Tests.Common;

public partial class CredentialGuardRegistrationTests
{
    private static partial IEnumerable<GuardedRegistration> Registrations() =>
    [
        new(
            nameof(GoogleCloudServiceCollectionExtensions.AddConfigFerretGcpSecretManager),
            "ConfigFerret:GcpSecretManager",
            new Dictionary<string, string?> { ["ProjectId"] = "contoso-prod", ["AccessToken"] = "fake-access-token" },
            ["AccessToken", "CredentialsJson"],
            (services, configuration) => services.AddConfigFerretGcpSecretManager(configuration),
            provider => provider.GetRequiredService<IOptions<GcpSecretManagerOptions>>().Value),
    ];
}
