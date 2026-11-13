using ConfigFerret.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ConfigFerret.Octopus;

/// <summary>
/// Checks <see cref="OctopusVariablesOptions"/> bound from configuration at startup: the required values are set, the
/// server address is safe to send the API key to, and the API key is not in an appsettings file.
/// </summary>
internal sealed class OctopusOptionsValidator(string sectionPath, IConfiguration? configuration) : IValidateOptions<OctopusVariablesOptions>
{
    public ValidateOptionsResult Validate(string? name, OctopusVariablesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        var serverKey = CredentialGuard.Key(sectionPath, nameof(options.ServerUri));
        if (options.ServerUri is null)
        {
            failures.Add($"{serverKey} is not set. Set it to the Octopus server address, for example in appsettings.json.");
        }
        else if (CredentialGuard.CheckAddress(options.ServerUri, options.AllowUnencryptedHttp, serverKey) is { } address)
        {
            failures.Add(address);
        }

        if (string.IsNullOrWhiteSpace(options.Project))
        {
            failures.Add($"{CredentialGuard.Key(sectionPath, nameof(options.Project))} is not set. Set it to the project name, slug or id, for example in appsettings.json.");
        }

        var keyKey = CredentialGuard.Key(sectionPath, nameof(options.ApiKey));
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            failures.Add(CredentialGuard.Missing(keyKey));
        }
        else if (CredentialGuard.FindAppSettingsFile(configuration, keyKey) is { } file)
        {
            failures.Add(CredentialGuard.InAppSettings(keyKey, file));
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
