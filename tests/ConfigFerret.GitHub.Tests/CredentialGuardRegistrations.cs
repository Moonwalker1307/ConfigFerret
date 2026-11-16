using ConfigFerret.GitHub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConfigFerret.Tests.Common;

public partial class CredentialGuardRegistrationTests
{
    private static partial IEnumerable<GuardedRegistration> Registrations() =>
    [
        new(
            nameof(GitHubServiceCollectionExtensions.AddConfigFerretGitHub),
            "ConfigFerret:GitHub",
            new Dictionary<string, string?> { ["Owner"] = "contoso", ["Repository"] = "api", ["Token"] = "fake-token" },
            ["Token"],
            (services, configuration) => services.AddConfigFerretGitHub(configuration),
            provider => provider.GetRequiredService<IOptions<GitHubVariablesOptions>>().Value),
    ];
}
