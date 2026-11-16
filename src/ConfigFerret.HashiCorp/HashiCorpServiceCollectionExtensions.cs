using ConfigFerret.HashiCorp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

// In this namespace so the extensions show up next to the other AddX calls.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers ConfigFerret's HashiCorp Vault and Consul sources with dependency injection.</summary>
public static class HashiCorpServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="VaultOptions"/> and <see cref="VaultTraceOptions"/> from <paramref name="configuration"/> (for
    /// example <c>builder.Configuration.GetSection("ConfigFerret:Vault")</c>), checks them when the host starts, and
    /// registers <see cref="VaultSource"/>. Put the address, mount and paths in appsettings.json, and the token in user
    /// secrets, an environment variable or a key vault: startup fails if the token is in an appsettings file.
    /// </summary>
    public static IServiceCollection AddConfigFerretVault(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var sectionPath = (configuration as IConfigurationSection)?.Path ?? string.Empty;

        services.AddOptions<VaultOptions>().Bind(configuration).ValidateOnStart();
        services.AddOptions<VaultTraceOptions>().Bind(configuration);
        services.AddSingleton<IValidateOptions<VaultOptions>>(sp => new VaultOptionsValidator(sectionPath, configuration, sp.GetService<IConfiguration>()));
        services.TryAddSingleton(sp => new VaultSource(sp.GetRequiredService<IOptions<VaultOptions>>(), sp.GetRequiredService<IOptions<VaultTraceOptions>>()));
        return services;
    }

    /// <summary>
    /// Binds <see cref="ConsulOptions"/> and <see cref="ConsulTraceOptions"/> from <paramref name="configuration"/> (for
    /// example <c>builder.Configuration.GetSection("ConfigFerret:Consul")</c>), checks them when the host starts, and
    /// registers <see cref="ConsulSource"/>. Put the address and prefix in appsettings.json, and the ACL token in user
    /// secrets, an environment variable or a key vault: startup fails if the token is in an appsettings file.
    /// </summary>
    public static IServiceCollection AddConfigFerretConsul(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var sectionPath = (configuration as IConfigurationSection)?.Path ?? string.Empty;

        services.AddOptions<ConsulOptions>().Bind(configuration).ValidateOnStart();
        services.AddOptions<ConsulTraceOptions>().Bind(configuration);
        services.AddSingleton<IValidateOptions<ConsulOptions>>(sp => new ConsulOptionsValidator(sectionPath, configuration, sp.GetService<IConfiguration>()));
        services.TryAddSingleton(sp => new ConsulSource(sp.GetRequiredService<IOptions<ConsulOptions>>(), sp.GetRequiredService<IOptions<ConsulTraceOptions>>()));
        return services;
    }
}
