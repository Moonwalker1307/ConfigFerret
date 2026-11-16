using ConfigFerret.Octopus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConfigFerret.Tests.Common;

public partial class CredentialGuardRegistrationTests
{
    private static partial IEnumerable<GuardedRegistration> Registrations() =>
    [
        new(
            nameof(OctopusServiceCollectionExtensions.AddConfigFerretOctopus),
            "ConfigFerret:Octopus",
            new Dictionary<string, string?> { ["ServerUri"] = "https://octopus.example.com/", ["Project"] = "billing-api", ["ApiKey"] = "fake-api-key" },
            ["ApiKey"],
            (services, configuration) => services.AddConfigFerretOctopus(configuration),
            provider => provider.GetRequiredService<IOptions<OctopusVariablesOptions>>().Value),
    ];
}
