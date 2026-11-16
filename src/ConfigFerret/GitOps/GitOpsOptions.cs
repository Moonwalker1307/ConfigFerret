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
    /// A values file is read when it is in the chart folder or in this checkout, also when it is named with <c>..</c>.
    /// A source path or Kustomization path that leads outside it, with <c>..</c> or a symbolic link, is not read, and
    /// neither is a chart file in the checkout that links outside it.
    /// </summary>
    public string? RepositoryRoot { get; set; }

    /// <summary>
    /// More ConfigMaps and Secrets for Flux <c>valuesFrom</c> and <c>substituteFrom</c>, for example from
    /// <c>kubectl get configmap -o yaml</c>. Those in the same manifest as the object are always used.
    /// </summary>
    public KubernetesManifests? Data { get; set; }

    /// <summary>How much the scan of a Flux Kustomization path reads.</summary>
    internal FluxScanLimits ScanLimits { get; set; } = FluxScanLimits.Default;
}

/// <summary>How much the scan of a Flux Kustomization path for <c>${VAR}</c> placeholders reads.</summary>
/// <param name="Entries">The files and folders it looks at.</param>
/// <param name="Bytes">The size of the YAML files it reads, all together.</param>
/// <param name="Depth">How many folders below the path it goes.</param>
internal sealed record FluxScanLimits(int Entries, long Bytes, int Depth)
{
    // Far above what the folder of one Kustomization holds in a large repository.
    public static readonly FluxScanLimits Default = new(100_000, 256L * 1024 * 1024, 64);
}
