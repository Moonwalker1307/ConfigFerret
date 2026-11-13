using ConfigFerret.GitHub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

// In this namespace so the extension shows up next to the other AddX calls.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers ConfigFerret's GitHub Actions source with dependency injection.</summary>
public static class GitHubServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="GitHubVariablesOptions"/> and <see cref="GitHubTraceOptions"/> from <paramref name="configuration"/>
    /// (for example <c>builder.Configuration.GetSection("ConfigFerret:GitHub")</c>), checks them when the host starts, and
    /// registers <see cref="GitHubVariablesSource"/>. Put the owner, repository and environment in appsettings.json, and the
    /// token in user secrets, an environment variable or a key vault: startup fails if the token is in an appsettings file.
    /// </summary>
    public static IServiceCollection AddConfigFerretGitHub(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var sectionPath = (configuration as IConfigurationSection)?.Path ?? string.Empty;

        services.AddOptions<GitHubVariablesOptions>().Bind(configuration).ValidateOnStart();
        services.AddOptions<GitHubTraceOptions>().Bind(configuration);
        services.AddSingleton<IValidateOptions<GitHubVariablesOptions>>(
            sp => new GitHubOptionsValidator(sectionPath, sp.GetService<IConfiguration>()));
        services.TryAddSingleton<GitHubVariablesSource>();
        return services;
    }
}
