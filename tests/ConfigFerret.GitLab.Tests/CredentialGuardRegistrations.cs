using ConfigFerret.GitLab;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConfigFerret.Tests.Common;

public partial class CredentialGuardRegistrationTests
{
    private static partial IEnumerable<GuardedRegistration> Registrations() =>
    [
        new(
            nameof(GitLabServiceCollectionExtensions.AddConfigFerretGitLab),
            "ConfigFerret:GitLab",
            new Dictionary<string, string?> { ["Project"] = "contoso/api", ["Token"] = "fake-token" },
            ["Token"],
            (services, configuration) => services.AddConfigFerretGitLab(configuration),
            provider => provider.GetRequiredService<IOptions<GitLabOptions>>().Value),
    ];
}
