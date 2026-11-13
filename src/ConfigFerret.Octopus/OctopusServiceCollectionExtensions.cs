using ConfigFerret.Octopus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

// In this namespace so the extension shows up next to the other AddX calls.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers ConfigFerret's Octopus Deploy source with dependency injection.</summary>
public static class OctopusServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="OctopusVariablesOptions"/> and <see cref="OctopusTraceOptions"/> from <paramref name="configuration"/>
    /// (for example <c>builder.Configuration.GetSection("ConfigFerret:Octopus")</c>), checks them when the host starts, and
    /// registers <see cref="OctopusVariablesSource"/>. Put the server address, project and environment in appsettings.json,
    /// and the API key in user secrets, an environment variable or a key vault: startup fails if the API key is in an
    /// appsettings file.
    /// </summary>
    public static IServiceCollection AddConfigFerretOctopus(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var sectionPath = (configuration as IConfigurationSection)?.Path ?? string.Empty;

        services.AddOptions<OctopusVariablesOptions>().Bind(configuration).ValidateOnStart();
        services.AddOptions<OctopusTraceOptions>().Bind(configuration);
        services.AddSingleton<IValidateOptions<OctopusVariablesOptions>>(
            sp => new OctopusOptionsValidator(sectionPath, sp.GetService<IConfiguration>()));
        services.TryAddSingleton<OctopusVariablesSource>();
        return services;
    }
}
