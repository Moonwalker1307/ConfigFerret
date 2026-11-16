using ConfigFerret.GitLab;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

// In this namespace so the extension shows up next to the other AddX calls.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers ConfigFerret's GitLab CI/CD source with dependency injection.</summary>
public static class GitLabServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="GitLabOptions"/> and <see cref="GitLabTraceOptions"/> from <paramref name="configuration"/> (for
    /// example <c>builder.Configuration.GetSection("ConfigFerret:GitLab")</c>), checks them when the host starts, and
    /// registers <see cref="GitLabSource"/>. Put the project and environment in appsettings.json, and the token in user
    /// secrets, an environment variable or a key vault: startup fails if the token is in an appsettings file.
    /// </summary>
    public static IServiceCollection AddConfigFerretGitLab(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var sectionPath = (configuration as IConfigurationSection)?.Path ?? string.Empty;

        services.AddOptions<GitLabOptions>().Bind(configuration).ValidateOnStart();
        services.AddOptions<GitLabTraceOptions>().Bind(configuration);
        services.AddSingleton<IValidateOptions<GitLabOptions>>(
            sp => new GitLabOptionsValidator(sectionPath, configuration, sp.GetService<IConfiguration>()));
        services.TryAddSingleton<GitLabSource>();
        return services;
    }
}
