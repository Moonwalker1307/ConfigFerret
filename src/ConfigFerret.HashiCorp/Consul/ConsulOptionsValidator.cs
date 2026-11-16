using ConfigFerret.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ConfigFerret.HashiCorp;

/// <summary>
/// Checks <see cref="ConsulOptions"/> bound from configuration at startup: the token is set (unless
/// <see cref="ConsulOptions.AllowNoToken"/>), the address is safe to send it to, and it is not in an appsettings file.
/// </summary>
internal sealed class ConsulOptionsValidator(string sectionPath, IConfiguration? configuration) : IValidateOptions<ConsulOptions>
{
    public ValidateOptionsResult Validate(string? name, ConsulOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        var tokenKey = CredentialGuard.Key(sectionPath, nameof(options.Token));
        if (string.IsNullOrWhiteSpace(options.Token))
        {
            if (!options.AllowNoToken)
            {
                failures.Add(CredentialGuard.Missing(tokenKey) + $" If the Consul agent has no ACLs, set {CredentialGuard.Key(sectionPath, nameof(options.AllowNoToken))} to true instead.");
            }
        }
        else if (CredentialGuard.CheckSource(configuration, tokenKey) is { } source)
        {
            failures.Add(source);
        }

        if (CredentialGuard.CheckAddress(options.Address, options.AllowUnencryptedHttp, CredentialGuard.Key(sectionPath, nameof(options.Address))) is { } address)
        {
            failures.Add(address);
        }

        if (options.MaxKeys < 1)
        {
            failures.Add($"{CredentialGuard.Key(sectionPath, nameof(options.MaxKeys))} must be at least 1.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
