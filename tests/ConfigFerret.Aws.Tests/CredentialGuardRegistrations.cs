using ConfigFerret.Aws;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConfigFerret.Tests.Common;

public partial class CredentialGuardRegistrationTests
{
    private static readonly Dictionary<string, string?> AwsKeys = new()
    {
        ["Region"] = "eu-west-1",
        ["AccessKeyId"] = "fake-access-key-id",
        ["SecretAccessKey"] = "fake-secret-access-key",
        ["SessionToken"] = "fake-session-token",
    };

    private static partial IEnumerable<GuardedRegistration> Registrations() =>
    [
        new(
            nameof(AwsServiceCollectionExtensions.AddConfigFerretAwsSecretsManager),
            "ConfigFerret:AwsSecretsManager",
            AwsKeys,
            ["AccessKeyId", "SecretAccessKey", "SessionToken"],
            (services, configuration) => services.AddConfigFerretAwsSecretsManager(configuration),
            provider => provider.GetRequiredService<IOptions<AwsSecretsManagerOptions>>().Value),
        new(
            nameof(AwsServiceCollectionExtensions.AddConfigFerretAwsParameterStore),
            "ConfigFerret:AwsParameterStore",
            new Dictionary<string, string?>(AwsKeys) { ["Path"] = "/orders/prod/" },
            ["AccessKeyId", "SecretAccessKey", "SessionToken"],
            (services, configuration) => services.AddConfigFerretAwsParameterStore(configuration),
            provider => provider.GetRequiredService<IOptions<AwsParameterStoreOptions>>().Value),
    ];
}
