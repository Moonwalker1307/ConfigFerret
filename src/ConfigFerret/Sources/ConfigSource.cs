namespace ConfigFerret.Sources;

/// <summary>
/// A store that is read before a trace, such as a secret store, a configuration service or a CI/CD variable service.
/// Each ConfigFerret extension package implements it, so hosts like the desktop app and the command-line tool can
/// list, configure and load every source the same way. See <see cref="IConfigSourceProvider"/>.
/// </summary>
public interface IConfigSource
{
    /// <summary>A name for the source, for example <c>Azure Key Vault contoso-prod</c>.</summary>
    string DisplayName { get; }

    /// <summary>Reads the store once and returns what was read, ready to add to a trace.</summary>
    Task<LoadedConfigSource> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>A snapshot read from an <see cref="IConfigSource"/>: the resolver that links layers to it, and its entries.</summary>
public sealed class LoadedConfigSource
{
    /// <summary>Creates a snapshot.</summary>
    /// <param name="displayName">A name for the source.</param>
    /// <param name="resolver">Links layers to the entries.</param>
    /// <param name="entries">Every entry that was read, so unused ones can be listed.</param>
    /// <param name="warnings">Problems found while reading, such as a scope the credential could not read.</param>
    public LoadedConfigSource(string displayName, IOriginResolver resolver, IEnumerable<SourceEntry> entries, IEnumerable<string>? warnings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(entries);
        DisplayName = displayName;
        Resolver = resolver;
        Entries = [.. entries];
        Warnings = [.. warnings ?? []];
    }

    /// <summary>A name for the source.</summary>
    public string DisplayName { get; }

    /// <summary>Links layers to the entries. Add it with <see cref="ConfigSourceTracerExtensions.WithSource"/>.</summary>
    public IOriginResolver Resolver { get; }

    /// <summary>Every entry that was read.</summary>
    public IReadOnlyList<SourceEntry> Entries { get; }

    /// <summary>Problems found while reading. The resolver also adds them to the trace's warnings.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// The entries that no step in <paramref name="report"/> links to and that the resolver does not otherwise know to be
    /// used, so nothing seems to use them.
    /// </summary>
    public IReadOnlyList<SourceEntry> FindUnused(ProvenanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var used = new HashSet<SourceRef>();
        var pending = new Stack<ProvenanceStep>(report.Settings.SelectMany(s => s.Steps));
        while (pending.Count > 0)
        {
            var step = pending.Pop();
            if (step.Reference is not null)
            {
                used.Add(step.Reference);
            }

            foreach (var origin in step.Origins)
            {
                pending.Push(origin);
            }
        }

        var knows = Resolver as IKnowsUses;
        return Entries.Where(e => !used.Contains(e.Reference) && knows?.IsUsed(e.Reference) != true).ToList();
    }
}

/// <summary>One entry read from a store: a secret, a key-value, a variable or an output.</summary>
/// <param name="Name">The entry's name in the store, for example <c>Db--Password</c> or <c>/orders/prod/Db/Timeout</c>.</param>
/// <param name="Reference">The reference the resolver puts on steps it creates for this entry, so <see cref="LoadedConfigSource.FindUnused"/> can match them.</param>
/// <param name="IsSensitive">True when the entry is secret.</param>
/// <param name="Detail">A short description, for example the content type, the label or the version.</param>
/// <param name="Location">Where the entry lives, for example the vault, the environment scope or the variable group, so a host can say where an unused entry is.</param>
public sealed record SourceEntry(string Name, SourceRef Reference, bool IsSensitive, string? Detail = null, string? Location = null);

/// <summary>
/// Describes a kind of <see cref="IConfigSource"/> and creates one from plain settings, so a host can show a form for it,
/// save the non-secret settings and keep the secret ones in a credential store.
/// </summary>
public interface IConfigSourceProvider
{
    /// <summary>A stable id, for example <c>azure-key-vault</c>. Hosts save it with a profile.</summary>
    string Id { get; }

    /// <summary>The name shown to people, for example <c>Azure Key Vault</c>.</summary>
    string DisplayName { get; }

    /// <summary>What kind of store this is, so a host can group sources or pick an icon or colour for them.</summary>
    SourceCategory Category { get; }

    /// <summary>The settings a source needs, in the order a form should show them.</summary>
    IReadOnlyList<SourceSetting> Settings { get; }

    /// <summary>
    /// Creates a source from values keyed by <see cref="SourceSetting.Name"/>. Values for <see cref="SourceSettingKind.Secret"/>
    /// settings come from a credential store or an environment variable, never from a saved profile.
    /// </summary>
    /// <exception cref="ArgumentException">A required setting is missing or a value is not valid.</exception>
    IConfigSource Create(IReadOnlyDictionary<string, string?> values);
}

/// <summary>What kind of store an <see cref="IConfigSourceProvider"/> reads.</summary>
public enum SourceCategory
{
    /// <summary>A secret store, such as Azure Key Vault, AWS Secrets Manager or HashiCorp Vault.</summary>
    SecretStore,

    /// <summary>A configuration service, such as Azure App Configuration, AWS Parameter Store or Consul KV.</summary>
    ConfigurationService,

    /// <summary>CI/CD or deployment tool variables, such as GitHub Actions, GitLab CI, Azure DevOps or Octopus Deploy.</summary>
    CiCd,

    /// <summary>A GitOps tool or infrastructure as code, such as Argo CD, Flux or Terraform.</summary>
    GitOps,

    /// <summary>A live cluster, such as Kubernetes.</summary>
    Cluster,

    /// <summary>Local files, such as dotenv or Docker Compose files.</summary>
    File,
}

/// <summary>What kind of value a <see cref="SourceSetting"/> holds, which decides how a form shows it and where it is kept.</summary>
public enum SourceSettingKind
{
    /// <summary>One line of text.</summary>
    Text,

    /// <summary>An absolute URL.</summary>
    Url,

    /// <summary><c>true</c> or <c>false</c>.</summary>
    Boolean,

    /// <summary>Several values, one per line.</summary>
    Lines,

    /// <summary>A file or folder path.</summary>
    Path,

    /// <summary>A credential. Hosts keep it in a credential store or read it from an environment variable, and never save it in a profile.</summary>
    Secret,
}

/// <summary>One setting of an <see cref="IConfigSourceProvider"/>.</summary>
/// <param name="Name">The key in the values passed to <see cref="IConfigSourceProvider.Create"/>, for example <c>VaultUri</c>.</param>
/// <param name="Label">The label shown on a form.</param>
/// <param name="Kind">The kind of value.</param>
/// <param name="Required">True when the source cannot be created without it.</param>
/// <param name="Help">One sentence on what to enter.</param>
/// <param name="DefaultValue">The value used when none is given.</param>
/// <param name="EnvironmentVariables">Environment variables a host may read a <see cref="SourceSettingKind.Secret"/> from, in order, for example <c>VAULT_TOKEN</c>.</param>
public sealed record SourceSetting(
    string Name,
    string Label,
    SourceSettingKind Kind = SourceSettingKind.Text,
    bool Required = false,
    string? Help = null,
    string? DefaultValue = null,
    IReadOnlyList<string>? EnvironmentVariables = null);

/// <summary>Adds loaded <see cref="IConfigSource"/> snapshots to a <see cref="ConfigTracer"/>.</summary>
public static class ConfigSourceTracerExtensions
{
    /// <summary>Links layers to the entries of a source you already loaded.</summary>
    public static ConfigTracer WithSource(this ConfigTracer tracer, LoadedConfigSource source)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(source);
        return tracer.WithResolver(source.Resolver);
    }

    /// <summary>Loads <paramref name="source"/> and links layers to its entries. Returns the snapshot, for <see cref="LoadedConfigSource.FindUnused"/>.</summary>
    public static async Task<LoadedConfigSource> WithSourceAsync(this ConfigTracer tracer, IConfigSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(source);
        var loaded = await source.LoadAsync(cancellationToken).ConfigureAwait(false);
        tracer.WithSource(loaded);
        return loaded;
    }
}

/// <summary>Helpers for resolvers that link layers to entries in a store by name.</summary>
public static class LayerNames
{
    /// <summary>
    /// The name a store would know <paramref name="layer"/> by: the environment variable name, the Kubernetes env entry
    /// name, the ConfigMap or Secret key, or an <see cref="ExternalSecretRef"/>'s remote key. Null for other layers.
    /// </summary>
    public static string? Of(ProvenanceStep layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        return layer.Reference switch
        {
            EnvironmentVariableRef env => env.Name,
            WorkloadEnvRef env => env.Name,
            ConfigMapKeyRef cm => cm.Key,
            SecretKeyRef s => s.Key,
            ExternalSecretRef e => e.RemoteKey,
            _ => null,
        };
    }

    /// <summary>
    /// Turns a name into the configuration key it sets, the way the .NET environment variables provider does:
    /// <c>Db__Timeout</c> becomes <c>Db:Timeout</c>. Pass the store's own separator too, for example <c>--</c> for
    /// Azure Key Vault or <c>/</c> for a path, to replace it the same way.
    /// </summary>
    public static string ToConfigurationKey(string name, string? separator = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        var key = name.Replace("__", ":", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(separator))
        {
            key = key.Replace(separator, ":", StringComparison.Ordinal);
        }

        return key.Trim(':');
    }
}
