using ConfigFerret.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ConfigFerret.GitLab;

/// <summary>
/// Checks <see cref="GitLabOptions"/> bound from configuration at startup: the project and token are set, the address is
/// safe to send the token to, and the token is not in an appsettings file.
/// </summary>
internal sealed class GitLabOptionsValidator(string sectionPath, IConfiguration? configuration, IConfiguration? registered = null) : IValidateOptions<GitLabOptions>
{
    public ValidateOptionsResult Validate(string? name, GitLabOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Project))
        {
            failures.Add($"{CredentialGuard.Key(sectionPath, nameof(options.Project))} is not set. Set it to the project id or path, for example contoso/api, in appsettings.json.");
        }

        var tokenKey = CredentialGuard.Key(sectionPath, nameof(options.Token));
        if (string.IsNullOrWhiteSpace(options.Token))
        {
            failures.Add(CredentialGuard.Missing(tokenKey));
        }
        else if (CredentialGuard.CheckSource(configuration, tokenKey, options.Token, registered) is { } source)
        {
            failures.Add(source);
        }

        if (CredentialGuard.CheckAddress(options.BaseAddress, options.AllowUnencryptedHttp, CredentialGuard.Key(sectionPath, nameof(options.BaseAddress))) is { } address)
        {
            failures.Add(address);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
