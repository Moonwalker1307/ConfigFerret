using ConfigFerret.AzureDevOps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConfigFerret.Tests.Common;

public partial class CredentialGuardRegistrationTests
{
    private static partial IEnumerable<GuardedRegistration> Registrations() =>
    [
        new(
            nameof(AzureDevOpsServiceCollectionExtensions.AddConfigFerretAzureDevOps),
            "ConfigFerret:AzureDevOps",
            new Dictionary<string, string?> { ["OrganizationUrl"] = "https://dev.azure.com/contoso/", ["Project"] = "api", ["PersonalAccessToken"] = "fake-pat" },
            ["PersonalAccessToken", "AccessToken"],
            (services, configuration) => services.AddConfigFerretAzureDevOps(configuration),
            provider => provider.GetRequiredService<IOptions<AzureDevOpsOptions>>().Value),
    ];
}
