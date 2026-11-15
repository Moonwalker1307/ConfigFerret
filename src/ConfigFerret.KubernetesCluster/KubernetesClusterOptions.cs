namespace ConfigFerret.KubernetesCluster;

/// <summary>
/// Which cluster, namespace and workload to read, and how to sign in. Can be bound from a configuration section, see
/// <c>services.AddConfigFerretKubernetesCluster(...)</c>.
/// </summary>
public sealed class KubernetesClusterOptions
{
    /// <summary>
    /// The kubeconfig file. Default: the files in <c>KUBECONFIG</c>, then <c>~/.kube/config</c>, then the pod's
    /// service account when the process runs in a cluster. The kubeconfig's own sign-in is used: a token, a client
    /// certificate or an exec credential plugin such as <c>kubelogin</c>, <c>aws eks get-token</c> or
    /// <c>gke-gcloud-auth-plugin</c>.
    /// </summary>
    public string? Kubeconfig { get; set; }

    /// <summary>The kubeconfig context. Default: the kubeconfig's current context.</summary>
    public string? Context { get; set; }

    /// <summary>The namespace. Default: the context's namespace, then the service account's, then <c>default</c>.</summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// The workload as <c>kind/name</c>, for example <c>deployment/api</c>, <c>statefulset/db</c>, <c>daemonset/agent</c>,
    /// <c>cronjob/report</c>, <c>job/migrate</c> or <c>pod/api-7d9f8c-x2kqp</c>. A name without a kind is a pod name.
    /// </summary>
    public string Workload { get; set; } = string.Empty;

    /// <summary>The container name. Needed only when the workload has more than one.</summary>
    public string? Container { get; set; }

    /// <summary>
    /// Also read the Secrets the workload uses, so their keys and values are known. Off by default: a
    /// <c>secretKeyRef</c> then shows as a secret with an unknown value. Values read are always treated as sensitive
    /// and redacted unless the trace reveals them. Needs <c>get</c> on <c>secrets</c>.
    /// </summary>
    public bool IncludeSecrets { get; set; }

    /// <summary>Use the pod's service account instead of a kubeconfig file. Default: only when no kubeconfig file is found.</summary>
    public bool InCluster { get; set; }

    /// <summary>Allows a plain-http API server that is not loopback. Off by default, so the credential is not sent in clear text.</summary>
    public bool AllowUnencryptedHttp { get; set; }
}
