using ConfigFerret.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ConfigFerret.Azure;

/// <summary>
/// Checks <see cref="AzureAppConfigurationOptions"/> bound from configuration at startup: exactly one of the endpoint
/// and the connection string is set, the address uses HTTPS, and no credential is in an appsettings file.
/// </summary>
internal sealed class AzureAppConfigurationOptionsValidator(string sectionPath, IConfiguration? configuration) : IValidateOptions<AzureAppConfigurationOptions>
{
    public ValidateOptionsResult Validate(string? name, AzureAppConfigurationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        var endpointKey = CredentialGuard.Key(sectionPath, nameof(options.Endpoint));
        var connectionKey = CredentialGuard.Key(sectionPath, nameof(options.ConnectionString));
        if (CheckTarget(options, endpointKey, connectionKey) is { } target)
        {
            failures.Add(target);
        }

        if (!string.IsNullOrWhiteSpace(options.ConnectionString) && CredentialGuard.FindAppSettingsFile(configuration, connectionKey) is { } file)
        {
            failures.Add(CredentialGuard.InAppSettings(connectionKey, file));
        }

        AzureOptionsChecks.CheckSignIn(sectionPath, configuration, options.TenantId, options.ClientId, options.ClientSecret, failures);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>Returns the problem with the endpoint or connection string, or null. Never quotes the connection string.</summary>
    internal static string? CheckTarget(AzureAppConfigurationOptions options, string endpointKey, string connectionKey)
    {
        var hasConnection = !string.IsNullOrWhiteSpace(options.ConnectionString);
        if (options.Endpoint is null && !hasConnection)
        {
            return $"{endpointKey} is not set. Set it to the store address, for example https://contoso.azconfig.io in appsettings.json, or set {connectionKey} outside appsettings files.";
        }

        if (options.Endpoint is not null && hasConnection)
        {
            return $"Both {endpointKey} and {connectionKey} are set. Set only one.";
        }

        if (options.Endpoint is { } endpoint)
        {
            return endpoint.IsAbsoluteUri && endpoint.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(endpoint.UserInfo)
                ? null
                : $"{endpointKey} must be an absolute https URL without credentials.";
        }

        try
        {
            AzureAppConfigurationSettings.ReadEndpoint(options.ConnectionString!);
            return null;
        }
        catch (ArgumentException)
        {
            return $"{connectionKey} must hold an https Endpoint=... part.";
        }
    }
}
