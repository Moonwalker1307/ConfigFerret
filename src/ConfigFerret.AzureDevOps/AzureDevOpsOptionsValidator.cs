using ConfigFerret.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ConfigFerret.AzureDevOps;

/// <summary>
/// Checks <see cref="AzureDevOpsOptions"/> bound from configuration at startup: the required values are set, the address
/// is safe to send the token to, and the token is not in an appsettings file.
/// </summary>
internal sealed class AzureDevOpsOptionsValidator(string sectionPath, IConfiguration? configuration) : IValidateOptions<AzureDevOpsOptions>
{
    public ValidateOptionsResult Validate(string? name, AzureDevOpsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        var urlKey = CredentialGuard.Key(sectionPath, nameof(options.OrganizationUrl));
        if (options.OrganizationUrl is null)
        {
            failures.Add($"{urlKey} is not set. Set it to the organization URL, for example https://dev.azure.com/contoso/, in appsettings.json.");
        }
        else if (CredentialGuard.CheckAddress(options.OrganizationUrl, options.AllowUnencryptedHttp, urlKey) is { } address)
        {
            failures.Add(address);
        }

        if (string.IsNullOrWhiteSpace(options.Project))
        {
            failures.Add($"{CredentialGuard.Key(sectionPath, nameof(options.Project))} is not set. Set it to the project name, for example in appsettings.json.");
        }

        var patKey = CredentialGuard.Key(sectionPath, nameof(options.PersonalAccessToken));
        var bearerKey = CredentialGuard.Key(sectionPath, nameof(options.AccessToken));
        var hasPat = !string.IsNullOrWhiteSpace(options.PersonalAccessToken);
        var hasBearer = !string.IsNullOrWhiteSpace(options.AccessToken);
        if (!hasPat && !hasBearer)
        {
            failures.Add(CredentialGuard.Missing(patKey) + $" Or set {bearerKey} to a Microsoft Entra ID access token.");
        }
        else if (hasPat && hasBearer)
        {
            failures.Add($"{patKey} and {bearerKey} are both set. Set only one.");
        }

        failures.AddRange(new[] { patKey, bearerKey }
            .Select(key => CredentialGuard.CheckSource(configuration, key))
            .OfType<string>());

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
