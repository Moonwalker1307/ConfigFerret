using ConfigFerret.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ConfigFerret.GoogleCloud;

/// <summary>
/// Checks <see cref="GcpSecretManagerOptions"/> bound from configuration at startup: the project is set, the location
/// and API address are valid, the key file exists, and no access token or key content is in an appsettings file.
/// Without a credential, Application Default Credentials are used, so a missing credential is not a failure here.
/// </summary>
internal sealed class GcpSecretManagerOptionsValidator(string sectionPath, IConfiguration? configuration, IConfiguration? registered = null) : IValidateOptions<GcpSecretManagerOptions>
{
    public ValidateOptionsResult Validate(string? name, GcpSecretManagerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ProjectId))
        {
            failures.Add($"{CredentialGuard.Key(sectionPath, nameof(options.ProjectId))} is not set. Set it to the Google Cloud project id, for example in appsettings.json.");
        }

        var locationKey = CredentialGuard.Key(sectionPath, nameof(options.Location));
        if (!GcpSecretManagerOptions.IsValidLocation(options.Location))
        {
            failures.Add($"{locationKey} must hold only lower-case letters, digits and dashes, for example europe-west1.");
        }
        else if (CredentialGuard.CheckAddress(options.EffectiveBaseAddress, options.AllowUnencryptedHttp, CredentialGuard.Key(sectionPath, nameof(options.ApiBaseAddress))) is { } address)
        {
            failures.Add(address);
        }

        failures.AddRange(new[] { (Name: nameof(options.AccessToken), Value: options.AccessToken), (Name: nameof(options.CredentialsJson), Value: options.CredentialsJson) }
            .Select(setting => CredentialGuard.CheckSource(configuration, CredentialGuard.Key(sectionPath, setting.Name), setting.Value, registered))
            .OfType<string>());

        if (!string.IsNullOrWhiteSpace(options.CredentialsFile) && !File.Exists(options.CredentialsFile.Trim()))
        {
            failures.Add($"{CredentialGuard.Key(sectionPath, nameof(options.CredentialsFile))} names a file that does not exist: {options.CredentialsFile.Trim()}.");
        }

        if (options.MaxSecrets < 1)
        {
            failures.Add($"{CredentialGuard.Key(sectionPath, nameof(options.MaxSecrets))} must be at least 1.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
