using ConfigFerret.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ConfigFerret.GitHub;

/// <summary>
/// Checks <see cref="GitHubVariablesOptions"/> bound from configuration at startup: the required values are set, the API
/// address is safe to send the token to, and the token is not in an appsettings file.
/// </summary>
internal sealed class GitHubOptionsValidator(string sectionPath, IConfiguration? configuration) : IValidateOptions<GitHubVariablesOptions>
{
    public ValidateOptionsResult Validate(string? name, GitHubVariablesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Owner))
        {
            failures.Add($"{CredentialGuard.Key(sectionPath, nameof(options.Owner))} is not set. Set it to the repository owner, for example in appsettings.json.");
        }

        if (string.IsNullOrWhiteSpace(options.Repository))
        {
            failures.Add($"{CredentialGuard.Key(sectionPath, nameof(options.Repository))} is not set. Set it to the repository name, for example in appsettings.json.");
        }

        var tokenKey = CredentialGuard.Key(sectionPath, nameof(options.Token));
        if (string.IsNullOrWhiteSpace(options.Token))
        {
            failures.Add(CredentialGuard.Missing(tokenKey));
        }
        else if (CredentialGuard.FindAppSettingsFile(configuration, tokenKey) is { } file)
        {
            failures.Add(CredentialGuard.InAppSettings(tokenKey, file));
        }

        if (CredentialGuard.CheckAddress(options.ApiBaseAddress, options.AllowUnencryptedHttp, CredentialGuard.Key(sectionPath, nameof(options.ApiBaseAddress))) is { } address)
        {
            failures.Add(address);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
