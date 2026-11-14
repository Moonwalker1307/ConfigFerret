using ConfigFerret.Sources;

namespace ConfigFerret.KubernetesCluster;

/// <summary>Describes <see cref="KubernetesClusterSource"/> to hosts such as the desktop app and the command-line tool.</summary>
public sealed class KubernetesClusterSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Id => "kubernetes-cluster";

    /// <inheritdoc />
    public string DisplayName => "Kubernetes cluster";

    /// <inheritdoc />
    public SourceCategory Category => SourceCategory.Cluster;

    /// <inheritdoc />
    public IReadOnlyList<SourceSetting> Settings { get; } =
    [
        new("Kubeconfig", "Kubeconfig file", SourceSettingKind.Path, Help: "The kubeconfig file. Leave empty for KUBECONFIG, then ~/.kube/config, then the pod's service account.", EnvironmentVariables: ["KUBECONFIG"]),
        new("Context", "Context", Help: "The kubeconfig context. Leave empty for the current context."),
        new("Namespace", "Namespace", Help: "The namespace. Leave empty for the context's namespace."),
        new("Workload", "Workload", Required: true, Help: "The workload as kind/name, for example deployment/api, or a pod name."),
        new("Container", "Container", Help: "The container name. Needed only when the workload has more than one."),
        new("IncludeSecrets", "Read Secrets", SourceSettingKind.Boolean, Help: "Also read the Secrets the workload uses. Their values stay redacted unless revealed.", DefaultValue: "false"),
    ];

    /// <inheritdoc />
    public IConfigSource Create(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var workload = Get(values, "Workload") ?? throw new ArgumentException("Workload is required, for example deployment/api.", nameof(values));
        _ = WorkloadReference.Parse(workload);
        var includeSecrets = Get(values, "IncludeSecrets") switch
        {
            null => false,
            var text when bool.TryParse(text, out var parsed) => parsed,
            _ => throw new ArgumentException("IncludeSecrets must be true or false.", nameof(values)),
        };

        return new KubernetesClusterSource(new KubernetesClusterOptions
        {
            Kubeconfig = Get(values, "Kubeconfig"),
            Context = Get(values, "Context"),
            Namespace = Get(values, "Namespace"),
            Workload = workload,
            Container = Get(values, "Container"),
            IncludeSecrets = includeSecrets,
        });
    }

    private static string? Get(IReadOnlyDictionary<string, string?> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
