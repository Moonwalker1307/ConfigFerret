using ConfigFerret.Kubernetes;

namespace ConfigFerret.GitOps;

/// <summary>Where to find the local files a GitOps object refers to.</summary>
public sealed class GitOpsOptions
{
    /// <summary>
    /// The chart folder (holding <c>Chart.yaml</c>, <c>values.yaml</c> and <c>templates/</c>). Needed for a chart from a
    /// Helm or OCI repository, which ConfigFerret cannot fetch: run <c>helm pull --untar</c> and pass the folder. For a
    /// chart in Git it defaults to the source path under <see cref="RepositoryRoot"/>.
    /// </summary>
    public string? ChartFolder { get; set; }

    /// <summary>
    /// The local checkout of the Git repository the object deploys from. Values files are looked up under the chart
    /// folder first and then here, and Argo CD <c>$ref/</c> values files and Flux Kustomization paths are resolved here.
    /// </summary>
    public string? RepositoryRoot { get; set; }

    /// <summary>
    /// More ConfigMaps and Secrets for Flux <c>valuesFrom</c> and <c>substituteFrom</c>, for example from
    /// <c>kubectl get configmap -o yaml</c>. Those in the same manifest as the object are always used.
    /// </summary>
    public KubernetesManifests? Data { get; set; }
}
