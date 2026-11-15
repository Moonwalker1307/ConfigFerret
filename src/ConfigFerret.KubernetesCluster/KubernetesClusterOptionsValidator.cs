using Microsoft.Extensions.Options;

namespace ConfigFerret.KubernetesCluster;

/// <summary>Checks <see cref="KubernetesClusterOptions"/> bound from configuration at startup.</summary>
internal sealed class KubernetesClusterOptionsValidator(string sectionPath) : IValidateOptions<KubernetesClusterOptions>
{
    public ValidateOptionsResult Validate(string? name, KubernetesClusterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        var workloadKey = Key(nameof(options.Workload));
        if (string.IsNullOrWhiteSpace(options.Workload))
        {
            failures.Add($"{workloadKey} is not set. Set it to the workload as kind/name, for example deployment/api.");
        }
        else
        {
            try
            {
                _ = WorkloadReference.Parse(options.Workload);
            }
            catch (ArgumentException ex)
            {
                failures.Add($"{workloadKey}: {ex.Message.Split(" (Parameter", 2)[0]}");
            }
        }

        if (!string.IsNullOrWhiteSpace(options.Kubeconfig) && !File.Exists(options.Kubeconfig))
        {
            failures.Add($"{Key(nameof(options.Kubeconfig))} names {options.Kubeconfig}, which does not exist.");
        }

        if (options.InCluster && !string.IsNullOrWhiteSpace(options.Kubeconfig))
        {
            failures.Add($"{Key(nameof(options.InCluster))} and {Key(nameof(options.Kubeconfig))} are both set. Use one.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private string Key(string option) => string.IsNullOrEmpty(sectionPath) ? option : $"{sectionPath}:{option}";
}
