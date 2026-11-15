using ConfigFerret.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ConfigFerret.Azure;

/// <summary>
/// Checks <see cref="AzureKeyVaultOptions"/> bound from configuration at startup: the vault address is set and uses
/// HTTPS, and the client secret is complete and not in an appsettings file.
/// </summary>
internal sealed class AzureKeyVaultOptionsValidator(string sectionPath, IConfiguration? configuration) : IValidateOptions<AzureKeyVaultOptions>
{
    public ValidateOptionsResult Validate(string? name, AzureKeyVaultOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        var vaultKey = CredentialGuard.Key(sectionPath, nameof(options.VaultUri));
        if (options.VaultUri is null)
        {
            failures.Add($"{vaultKey} is not set. Set it to the vault address, for example https://contoso.vault.azure.net/ in appsettings.json.");
        }
        else if (!options.VaultUri.IsAbsoluteUri || options.VaultUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(options.VaultUri.UserInfo))
        {
            failures.Add($"{vaultKey} must be an absolute https URL without credentials.");
        }

        AzureOptionsChecks.CheckSignIn(sectionPath, configuration, options.TenantId, options.ClientId, options.ClientSecret, failures);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Startup checks shared by the Key Vault and App Configuration validators.</summary>
internal static class AzureOptionsChecks
{
    public static void CheckSignIn(string sectionPath, IConfiguration? configuration, string? tenantId, string? clientId, string? clientSecret, List<string> failures)
    {
        var secretKey = CredentialGuard.Key(sectionPath, "ClientSecret");
        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            return;
        }

        if (CredentialGuard.FindAppSettingsFile(configuration, secretKey) is { } file)
        {
            failures.Add(CredentialGuard.InAppSettings(secretKey, file));
        }

        if (AzureAuth.Check(tenantId, clientId, clientSecret, secretKey) is { } problem)
        {
            failures.Add(problem);
        }
    }
}
