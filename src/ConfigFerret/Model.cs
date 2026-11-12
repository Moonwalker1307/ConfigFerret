namespace ConfigFerret;

/// <summary>The kind of layer a provenance step comes from.</summary>
public enum LayerKind
{
    /// <summary>A .NET configuration provider such as a JSON file, environment variables or the command line.</summary>
    DotNetProvider,

    /// <summary>A literal value written in a Kubernetes workload spec (Deployment, Pod, StatefulSet and so on).</summary>
    KubernetesWorkload,

    /// <summary>A Kubernetes ConfigMap.</summary>
    KubernetesConfigMap,

    /// <summary>A Kubernetes Secret.</summary>
    KubernetesSecret,

    /// <summary>A Kubernetes downward API field (fieldRef or resourceFieldRef).</summary>
    KubernetesField,

    /// <summary>A Helm template or values file.</summary>
    Helm,

    /// <summary>A Kustomize kustomization (generator or overlay).</summary>
    Kustomize,
}

/// <summary>A machine-readable pointer to where a step's value came from, used to look further upstream.</summary>
public abstract record SourceRef;

/// <summary>An environment variable seen by the process.</summary>
/// <param name="Name">The variable name, for example <c>DB__Timeout</c>.</param>
public sealed record EnvironmentVariableRef(string Name) : SourceRef;

/// <summary>A file on disk, for example an appsettings file or a key-per-file secret.</summary>
/// <param name="Path">The full path of the file.</param>
public sealed record FileRef(string Path) : SourceRef;

/// <summary>A key in a Kubernetes ConfigMap.</summary>
public sealed record ConfigMapKeyRef(string Name, string Key) : SourceRef;

/// <summary>A key in a Kubernetes Secret.</summary>
public sealed record SecretKeyRef(string Name, string Key) : SourceRef;

/// <summary>A literal env entry written in a Kubernetes workload's container spec.</summary>
public sealed record WorkloadEnvRef(string Workload, string Container, string Name) : SourceRef;

/// <summary>A path in the merged Helm values, for example <c>db.timeout</c>.</summary>
public sealed record HelmValueRef(string Path) : SourceRef;

/// <summary>
/// One layer that set a value. Steps in a list are ordered from lowest to highest priority,
/// so the last one is the value that wins.
/// </summary>
public sealed record ProvenanceStep
{
    /// <summary>The kind of layer.</summary>
    public required LayerKind Kind { get; init; }

    /// <summary>A human-readable name for the source, for example <c>appsettings.Production.json</c> or <c>ConfigMap api-config</c>.</summary>
    public required string Source { get; init; }

    /// <summary>Where in the source the value is, for example a line number or a key, when known.</summary>
    public string? Location { get; init; }

    /// <summary>The value this layer set. Null when the value is not known, or when the layer removed the key.</summary>
    public string? Value { get; init; }

    /// <summary>True when this layer removes the key instead of setting it (a Helm <c>null</c>, a Kustomize replace).</summary>
    public bool IsRemoval { get; init; }

    /// <summary>True when the value is secret. Its <see cref="Value"/> is redacted unless revealing is turned on.</summary>
    public bool IsSensitive { get; init; }

    /// <summary>True when the link to this layer is a best guess (for example from scanning Helm templates) rather than certain.</summary>
    public bool IsInferred { get; init; }

    /// <summary>True when this step is the one that wins among its siblings.</summary>
    public bool IsEffective { get; init; }

    /// <summary>An extra note, such as which workload or container an env entry is on.</summary>
    public string? Note { get; init; }

    /// <summary>A pointer used to trace the value further upstream.</summary>
    public SourceRef? Reference { get; init; }

    /// <summary>The upstream layers that produced this step's value, lowest priority first.</summary>
    public IReadOnlyList<ProvenanceStep> Origins { get; init; } = [];
}

/// <summary>One effective configuration setting and every layer that set it.</summary>
/// <param name="Key">The configuration key, for example <c>Db:Timeout</c>.</param>
/// <param name="Value">The effective value (redacted when sensitive).</param>
/// <param name="Steps">The .NET configuration layers that set the key, lowest priority first.</param>
/// <param name="IsSensitive">True when the value is treated as secret.</param>
public sealed record TracedSetting(string Key, string? Value, IReadOnlyList<ProvenanceStep> Steps, bool IsSensitive)
{
    /// <summary>The step that won.</summary>
    public ProvenanceStep? EffectiveStep => Steps.Count == 0 ? null : Steps[^1];

    /// <summary>
    /// A one-sentence history, for example
    /// "set in appsettings.json, overridden by appsettings.Production.json, overridden again by environment variable DB__Timeout from ConfigMap api-config".
    /// </summary>
    public string Story => Internal.StoryWriter.Write(this);
}
