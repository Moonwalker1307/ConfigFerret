using ConfigFerret.GoogleCloud;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

// In this namespace so the extension shows up next to the other AddX calls.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers ConfigFerret's Google Cloud Secret Manager source with dependency injection.</summary>
public static class GoogleCloudServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="GcpSecretManagerOptions"/> and <see cref="GcpSecretManagerTraceOptions"/> from
    /// <paramref name="configuration"/> (for example <c>builder.Configuration.GetSection("ConfigFerret:GcpSecretManager")</c>),
    /// checks them when the host starts, and registers <see cref="GcpSecretManagerSource"/>. Put the project, location and
    /// prefix in appsettings.json. Sign in with Application Default Credentials, a key file path, or an access token or key
    /// content from user secrets, an environment variable or a key vault: startup fails if either is in an appsettings file.
    /// </summary>
    public static IServiceCollection AddConfigFerretGcpSecretManager(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var sectionPath = (configuration as IConfigurationSection)?.Path ?? string.Empty;

        services.AddOptions<GcpSecretManagerOptions>().Bind(configuration).ValidateOnStart();
        services.AddOptions<GcpSecretManagerTraceOptions>().Bind(configuration);
        services.AddSingleton<IValidateOptions<GcpSecretManagerOptions>>(sp => new GcpSecretManagerOptionsValidator(sectionPath, sp.GetService<IConfiguration>()));
        services.TryAddSingleton(sp => new GcpSecretManagerSource(
            sp.GetRequiredService<IOptions<GcpSecretManagerOptions>>(),
            sp.GetRequiredService<IOptions<GcpSecretManagerTraceOptions>>()));
        return services;
    }
}
