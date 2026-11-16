using ConfigFerret.AzureDevOps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

// In this namespace so the extension shows up next to the other AddX calls.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers ConfigFerret's Azure DevOps source with dependency injection.</summary>
public static class AzureDevOpsServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="AzureDevOpsOptions"/> and <see cref="AzureDevOpsTraceOptions"/> from <paramref name="configuration"/>
    /// (for example <c>builder.Configuration.GetSection("ConfigFerret:AzureDevOps")</c>), checks them when the host starts,
    /// and registers <see cref="AzureDevOpsSource"/>. Put the organization URL, project and group names in appsettings.json,
    /// and the token in user secrets, an environment variable or a key vault: startup fails if the token is in an
    /// appsettings file.
    /// </summary>
    public static IServiceCollection AddConfigFerretAzureDevOps(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var sectionPath = (configuration as IConfigurationSection)?.Path ?? string.Empty;

        services.AddOptions<AzureDevOpsOptions>().Bind(configuration).ValidateOnStart();
        services.AddOptions<AzureDevOpsTraceOptions>().Bind(configuration);
        services.AddSingleton<IValidateOptions<AzureDevOpsOptions>>(
            sp => new AzureDevOpsOptionsValidator(sectionPath, configuration, sp.GetService<IConfiguration>()));
        services.TryAddSingleton<AzureDevOpsSource>();
        return services;
    }
}
