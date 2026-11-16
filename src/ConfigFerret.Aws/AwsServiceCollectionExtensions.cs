using ConfigFerret.Aws;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

// In this namespace so the extension shows up next to the other AddX calls.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers ConfigFerret's AWS Secrets Manager and Parameter Store sources with dependency injection.</summary>
public static class AwsServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="AwsSecretsManagerOptions"/> and <see cref="AwsSecretsTraceOptions"/> from <paramref name="configuration"/>
    /// (for example <c>builder.Configuration.GetSection("ConfigFerret:AwsSecretsManager")</c>), checks them when the host
    /// starts, and registers <see cref="AwsSecretsManagerSource"/>. Leave the credentials out to use the default AWS
    /// credential chain; startup fails if an access key is in an appsettings file.
    /// </summary>
    public static IServiceCollection AddConfigFerretAwsSecretsManager(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var sectionPath = (configuration as IConfigurationSection)?.Path ?? string.Empty;

        services.AddOptions<AwsSecretsManagerOptions>().Bind(configuration).ValidateOnStart();
        services.AddOptions<AwsSecretsTraceOptions>().Bind(configuration);
        services.AddSingleton<IValidateOptions<AwsSecretsManagerOptions>>(
            sp => new AwsOptionsValidator<AwsSecretsManagerOptions>(sectionPath, configuration, sp.GetService<IConfiguration>()));
        services.TryAddSingleton(sp => new AwsSecretsManagerSource(
            sp.GetRequiredService<IOptions<AwsSecretsManagerOptions>>().Value,
            sp.GetRequiredService<IOptions<AwsSecretsTraceOptions>>().Value));
        return services;
    }

    /// <summary>
    /// Binds <see cref="AwsParameterStoreOptions"/> and <see cref="AwsParametersTraceOptions"/> from <paramref name="configuration"/>
    /// (for example <c>builder.Configuration.GetSection("ConfigFerret:AwsParameterStore")</c>), checks them when the host
    /// starts, and registers <see cref="AwsParameterStoreSource"/>. <c>Path</c> is required.
    /// </summary>
    public static IServiceCollection AddConfigFerretAwsParameterStore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var sectionPath = (configuration as IConfigurationSection)?.Path ?? string.Empty;

        services.AddOptions<AwsParameterStoreOptions>().Bind(configuration).ValidateOnStart();
        services.AddOptions<AwsParametersTraceOptions>().Bind(configuration);
        services.AddSingleton<IValidateOptions<AwsParameterStoreOptions>>(
            sp => new AwsOptionsValidator<AwsParameterStoreOptions>(sectionPath, configuration, sp.GetService<IConfiguration>()));
        services.TryAddSingleton(sp => new AwsParameterStoreSource(
            sp.GetRequiredService<IOptions<AwsParameterStoreOptions>>().Value,
            sp.GetRequiredService<IOptions<AwsParametersTraceOptions>>().Value));
        return services;
    }
}
