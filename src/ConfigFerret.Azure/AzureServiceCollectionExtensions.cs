using Azure.Core;
using ConfigFerret.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

// In this namespace so the extensions show up next to the other AddX calls.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers ConfigFerret's Azure Key Vault and App Configuration sources with dependency injection.</summary>
public static class AzureServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="AzureKeyVaultOptions"/> and <see cref="AzureKeyVaultTraceOptions"/> from <paramref name="configuration"/>
    /// (for example <c>builder.Configuration.GetSection("ConfigFerret:AzureKeyVault")</c>), checks them when the host
    /// starts, and registers <see cref="AzureKeyVaultSource"/>. It signs in with a <see cref="TokenCredential"/> registered
    /// in the container, else the service principal in the section, else <c>DefaultAzureCredential</c>. Startup fails if
    /// a client secret is in an appsettings file.
    /// </summary>
    public static IServiceCollection AddConfigFerretAzureKeyVault(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var sectionPath = (configuration as IConfigurationSection)?.Path ?? string.Empty;

        services.AddOptions<AzureKeyVaultOptions>().Bind(configuration).ValidateOnStart();
        services.AddOptions<AzureKeyVaultTraceOptions>().Bind(configuration);
        services.AddSingleton<IValidateOptions<AzureKeyVaultOptions>>(
            sp => new AzureKeyVaultOptionsValidator(sectionPath, sp.GetService<IConfiguration>()));
        services.TryAddSingleton(sp => new AzureKeyVaultSource(
            sp.GetRequiredService<IOptions<AzureKeyVaultOptions>>(),
            sp.GetRequiredService<IOptions<AzureKeyVaultTraceOptions>>(),
            sp.GetService<TokenCredential>()));
        return services;
    }

    /// <summary>
    /// Binds <see cref="AzureAppConfigurationOptions"/> and <see cref="AzureAppConfigurationTraceOptions"/> from
    /// <paramref name="configuration"/> (for example <c>builder.Configuration.GetSection("ConfigFerret:AzureAppConfiguration")</c>),
    /// checks them when the host starts, and registers <see cref="AzureAppConfigurationSource"/>. With an endpoint it
    /// signs in with a <see cref="TokenCredential"/> registered in the container, else the service principal in the
    /// section, else <c>DefaultAzureCredential</c>. With a connection string it uses that, even when a credential is
    /// registered. Startup fails if a connection string or client secret is in an appsettings file.
    /// </summary>
    public static IServiceCollection AddConfigFerretAzureAppConfiguration(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var sectionPath = (configuration as IConfigurationSection)?.Path ?? string.Empty;

        services.AddOptions<AzureAppConfigurationOptions>().Bind(configuration).ValidateOnStart();
        services.AddOptions<AzureAppConfigurationTraceOptions>().Bind(configuration);
        services.AddSingleton<IValidateOptions<AzureAppConfigurationOptions>>(
            sp => new AzureAppConfigurationOptionsValidator(sectionPath, sp.GetService<IConfiguration>()));
        services.TryAddSingleton(sp => new AzureAppConfigurationSource(
            sp.GetRequiredService<IOptions<AzureAppConfigurationOptions>>(),
            sp.GetRequiredService<IOptions<AzureAppConfigurationTraceOptions>>(),
            sp.GetService<TokenCredential>()));
        return services;
    }
}
