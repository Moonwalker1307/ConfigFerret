namespace ConfigFerret.KubernetesCluster;

/// <summary>A workload by kind and name, parsed from <c>kind/name</c> the way <c>kubectl</c> takes it.</summary>
/// <param name="Kind">The kind: <c>Deployment</c>, <c>StatefulSet</c>, <c>DaemonSet</c>, <c>CronJob</c>, <c>Job</c> or <c>Pod</c>.</param>
/// <param name="Name">The name.</param>
public sealed record WorkloadReference(string Kind, string Name)
{
    /// <summary>Parses <c>kind/name</c> or a pod name.</summary>
    /// <exception cref="ArgumentException">The text is empty, the kind is not supported, or the name is not valid.</exception>
    public static WorkloadReference Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var trimmed = text.Trim();
        var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        string kind;
        string name;
        if (slash < 0)
        {
            kind = "Pod";
            name = trimmed;
        }
        else
        {
            // kubectl also takes kind.group, for example deployment.apps/api.
            var kindText = trimmed[..slash];
            var dot = kindText.IndexOf('.', StringComparison.Ordinal);
            kind = KindOf(dot < 0 ? kindText : kindText[..dot])
                ?? throw new ArgumentException($"Workload kind '{kindText}' is not supported. Use deployment, statefulset, daemonset, cronjob, job or pod.", nameof(text));
            name = trimmed[(slash + 1)..];
        }

        if (name.Length is 0 or > 253 || !name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.'))
        {
            throw new ArgumentException($"'{name}' is not a valid Kubernetes object name: use lowercase letters, digits, '-' and '.'.", nameof(text));
        }

        return new WorkloadReference(kind, name);
    }

    // The names kubectl takes for each kind: singular, plural and short.
    private static string? KindOf(string text) => text.ToLowerInvariant() switch
    {
        "deployment" or "deployments" or "deploy" => "Deployment",
        "statefulset" or "statefulsets" or "sts" => "StatefulSet",
        "daemonset" or "daemonsets" or "ds" => "DaemonSet",
        "cronjob" or "cronjobs" or "cj" => "CronJob",
        "job" or "jobs" => "Job",
        "pod" or "pods" or "po" => "Pod",
        _ => null,
    };

    /// <inheritdoc />
    public override string ToString() => $"{Kind}/{Name}";
}
