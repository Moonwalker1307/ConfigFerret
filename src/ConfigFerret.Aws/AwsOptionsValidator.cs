using ConfigFerret.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ConfigFerret.Aws;

/// <summary>
/// Checks AWS options bound from configuration at startup: access keys fit together and are not in an appsettings file,
/// the service URL is safe to send signed requests to, and (for Parameter Store) the path is set.
/// </summary>
internal sealed class AwsOptionsValidator<TOptions>(string sectionPath, IConfiguration? configuration, IConfiguration? registered = null) : IValidateOptions<TOptions>
    where TOptions : AwsConnectionOptions
{
    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options is AwsParameterStoreOptions parameters && !(parameters.Path?.Trim().StartsWith('/') ?? false))
        {
            failures.Add($"{CredentialGuard.Key(sectionPath, nameof(parameters.Path))} must be set to a path that starts with /, for example /orders/prod/, in appsettings.json.");
        }

        if (AwsClients.CheckAccessKeys(options, sectionPath) is { } keys)
        {
            failures.Add(keys);
        }

        failures.AddRange(new[]
            {
                (Name: nameof(options.AccessKeyId), Value: options.AccessKeyId),
                (Name: nameof(options.SecretAccessKey), Value: options.SecretAccessKey),
                (Name: nameof(options.SessionToken), Value: options.SessionToken),
            }
            .Select(setting => CredentialGuard.CheckSource(configuration, CredentialGuard.Key(sectionPath, setting.Name), setting.Value, registered))
            .OfType<string>());

        if (AwsClients.CheckServiceUrl(options.ServiceUrl, CredentialGuard.Key(sectionPath, nameof(options.ServiceUrl))) is { } url)
        {
            failures.Add(url);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
