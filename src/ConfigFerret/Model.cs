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

    /// <summary>A GitHub Actions variable or secret (organization, repository or environment). Used by the ConfigFerret.GitHub package.</summary>
    GitHubActions,

    /// <summary>An Octopus Deploy project or library variable set variable. Used by the ConfigFerret.Octopus package.</summary>
    OctopusDeploy,

    /// <summary>An Azure Key Vault secret. Used by the ConfigFerret.Azure package.</summary>
    AzureKeyVault,

    /// <summary>An Azure App Configuration key-value. Used by the ConfigFerret.Azure package.</summary>
    AzureAppConfiguration,

    /// <summary>An AWS Secrets Manager secret. Used by the ConfigFerret.Aws package.</summary>
    AwsSecretsManager,

    /// <summary>An AWS Systems Manager Parameter Store parameter. Used by the ConfigFerret.Aws package.</summary>
    AwsParameterStore,

    /// <summary>A HashiCorp Vault secret. Used by the ConfigFerret.HashiCorp package.</summary>
    HashiCorpVault,

    /// <summary>A HashiCorp Consul KV entry. Used by the ConfigFerret.HashiCorp package.</summary>
    Consul,

    /// <summary>A Google Cloud Secret Manager secret. Used by the ConfigFerret.GoogleCloud package.</summary>
    GcpSecretManager,

    /// <summary>An Azure DevOps (Azure Pipelines) variable group variable. Used by the ConfigFerret.AzureDevOps package.</summary>
    AzureDevOps,

    /// <summary>A GitLab CI/CD variable (instance, group, project or environment scoped). Used by the ConfigFerret.GitLab package.</summary>
    GitLabCi,

    /// <summary>An Argo CD Application or Flux HelmRelease/Kustomization that sets Helm values or patches per environment.</summary>
    GitOps,

    /// <summary>A Terraform output.</summary>
    Terraform,

    /// <summary>A dotenv (<c>.env</c>) file.</summary>
    DotEnv,

    /// <summary>A Docker Compose service definition (<c>environment</c>, <c>env_file</c> or interpolation).</summary>
    DockerCompose,

    /// <summary>Another source, from a third-party <see cref="IOriginResolver"/>.</summary>
    Other,
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
/// A secret in an external store that an operator copies into a Kubernetes Secret key: an External Secrets Operator
/// <c>ExternalSecret</c>, or a Secrets Store CSI driver <c>SecretProviderClass</c>. Secret store resolvers (Azure Key
/// Vault, AWS, Vault, Google Cloud) link it to the secret it names.
/// </summary>
/// <param name="Provider">
/// The store type when known, as External Secrets Operator names it: <c>azurekv</c>, <c>aws</c>, <c>vault</c>,
/// <c>gcpsm</c> and so on. Null when the SecretStore manifest was not loaded.
/// </param>
/// <param name="Store">The SecretStore, ClusterSecretStore or SecretProviderClass name, when known.</param>
/// <param name="RemoteKey">The secret's name or path in the store, for example <c>db-password</c> or <c>prod/orders/db</c>.</param>
/// <param name="Property">The property inside a JSON or key-value secret, when the reference picks one.</param>
/// <param name="Version">The pinned version, when the reference pins one.</param>
public sealed record ExternalSecretRef(string? Provider, string? Store, string RemoteKey, string? Property = null, string? Version = null) : SourceRef;

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

    /// <summary>
    /// The configuration key this step sets, for example <c>Db:Timeout</c>. Set on the .NET configuration layers
    /// (the top-level steps of a <see cref="TracedSetting"/>), so resolvers can match on the key; null on upstream layers.
    /// </summary>
    public string? ConfigurationKey { get; init; }

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
