using ConfigFerret.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ConfigFerret.HashiCorp;

/// <summary>
/// Checks <see cref="VaultOptions"/> bound from configuration at startup: the address and token are set, the address is
/// safe to send the token to, and the token is not in an appsettings file.
/// </summary>
internal sealed class VaultOptionsValidator(string sectionPath, IConfiguration? configuration) : IValidateOptions<VaultOptions>
{
    public ValidateOptionsResult Validate(string? name, VaultOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        var addressKey = CredentialGuard.Key(sectionPath, nameof(options.Address));
        if (options.Address is null)
        {
            failures.Add($"{addressKey} is not set. Set it to the Vault address, for example https://vault.example.com:8200/, in appsettings.json.");
        }
        else if (CredentialGuard.CheckAddress(options.Address, options.AllowUnencryptedHttp, addressKey) is { } address)
        {
            failures.Add(address);
        }

        var tokenKey = CredentialGuard.Key(sectionPath, nameof(options.Token));
        if (string.IsNullOrWhiteSpace(options.Token))
        {
            failures.Add(CredentialGuard.Missing(tokenKey));
        }
        else if (CredentialGuard.CheckSource(configuration, tokenKey) is { } source)
        {
            failures.Add(source);
        }

        if (string.IsNullOrWhiteSpace(options.Mount))
        {
            failures.Add($"{CredentialGuard.Key(sectionPath, nameof(options.Mount))} is empty. Set it to the KV mount path, for example secret.");
        }

        if (options.KvVersion is not (null or 1 or 2))
        {
            failures.Add($"{CredentialGuard.Key(sectionPath, nameof(options.KvVersion))} must be 1 or 2, or left out to detect it.");
        }

        if (options.MaxDepth < 0 || options.MaxSecrets < 1 || options.MaxRequests < 1)
        {
            failures.Add($"{CredentialGuard.Key(sectionPath, nameof(options.MaxDepth))} must be at least 0, and MaxSecrets and MaxRequests at least 1.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
