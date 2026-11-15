using ConfigFerret.KubernetesCluster;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

// In this namespace so the extension shows up next to the other AddX calls.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers ConfigFerret's live Kubernetes cluster source with dependency injection.</summary>
public static class KubernetesClusterServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="KubernetesClusterOptions"/> from <paramref name="configuration"/> (for example
    /// <c>builder.Configuration.GetSection("ConfigFerret:KubernetesCluster")</c>), checks them when the host starts, and
    /// registers <see cref="KubernetesClusterSource"/>. The credential stays in the kubeconfig or the service account,
    /// never in appsettings.
    /// </summary>
    public static IServiceCollection AddConfigFerretKubernetesCluster(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var sectionPath = (configuration as IConfigurationSection)?.Path ?? string.Empty;

        services.AddOptions<KubernetesClusterOptions>().Bind(configuration).ValidateOnStart();
        services.AddSingleton<IValidateOptions<KubernetesClusterOptions>>(new KubernetesClusterOptionsValidator(sectionPath));
        services.TryAddSingleton(sp => new KubernetesClusterSource(sp.GetRequiredService<IOptions<KubernetesClusterOptions>>()));
        return services;
    }
}
