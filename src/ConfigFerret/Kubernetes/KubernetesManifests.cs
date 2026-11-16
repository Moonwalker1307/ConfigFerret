using System.Text;
using ConfigFerret.Internal;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ConfigFerret.Kubernetes;

/// <summary>
/// The workloads, ConfigMaps and Secrets from a set of Kubernetes manifests, in YAML or JSON.
/// Load the output of <c>helm template</c>, <c>kustomize build</c> or <c>kubectl get pod -o yaml</c>,
/// or read the live pod with <see cref="KubernetesInCluster"/>.
/// </summary>
public sealed class KubernetesManifests
{
    private static readonly HashSet<string> PodTemplateKinds =
        new(StringComparer.Ordinal) { "Deployment", "StatefulSet", "DaemonSet", "ReplicaSet", "Job", "ReplicationController" };

    private readonly List<Workload> _workloads = [];
    private readonly Dictionary<string, KubernetesData> _configMaps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, KubernetesData> _secrets = new(StringComparer.Ordinal);
    private readonly List<ExternalSecret> _externalSecrets = [];
    private readonly List<SecretStore> _secretStores = [];
    private readonly Dictionary<string, SecretProviderClass> _secretProviderClasses = new(StringComparer.Ordinal);
    private Dictionary<string, KubernetesData>? _createdSecrets;

    /// <summary>The workloads found, with their pod specs.</summary>
    public IReadOnlyList<Workload> Workloads => _workloads;

    /// <summary>The ConfigMaps found, by name.</summary>
    public IReadOnlyDictionary<string, KubernetesData> ConfigMaps => _configMaps;

    /// <summary>The Secrets found, by name. Values are decoded but always treated as sensitive.</summary>
    public IReadOnlyDictionary<string, KubernetesData> Secrets => _secrets;

    /// <summary>
    /// The External Secrets Operator <c>ExternalSecret</c>s found. The Secret each one writes is treated as existing,
    /// with its <c>spec.data</c> keys and unknown values, when the Secret itself is not in the manifests.
    /// </summary>
    public IReadOnlyList<ExternalSecret> ExternalSecrets => _externalSecrets;

    /// <summary>The External Secrets Operator <c>SecretStore</c>s and <c>ClusterSecretStore</c>s found.</summary>
    public IReadOnlyList<SecretStore> SecretStores => _secretStores;

    /// <summary>
    /// The Secrets Store CSI driver <c>SecretProviderClass</c>es found, by name. The Secrets they sync
    /// (<c>spec.secretObjects</c>) are treated as existing like an ExternalSecret's target.
    /// </summary>
    public IReadOnlyDictionary<string, SecretProviderClass> SecretProviderClasses => _secretProviderClasses;

    /// <summary>Parses manifests from YAML or JSON text. Multi-document YAML and <c>List</c> kinds are supported.</summary>
    /// <param name="text">The manifest text.</param>
    /// <param name="sourceName">A name for the text, used in output (for example a file name).</param>
    public static KubernetesManifests Parse(string text, string sourceName = "manifests")
    {
        var result = new KubernetesManifests();
        result.Add(text, sourceName);
        return result;
    }

    /// <summary>
    /// Loads manifests from files. Directories are searched for <c>*.yaml</c>, <c>*.yml</c> and <c>*.json</c>, without
    /// following symbolic links below them.
    /// </summary>
    public static KubernetesManifests LoadFiles(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var result = new KubernetesManifests();
        foreach (var path in paths)
        {
            // Links are skipped: a link to a parent folder never ends, and one out of the folder reads other files.
            var files = Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint })
                    .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.Ordinal)
                : (IEnumerable<string>)[path];
            foreach (var file in files)
            {
                result.Add(File.ReadAllText(file), Path.GetFileName(file));
            }
        }

        return result;
    }

    /// <summary>
    /// Creates the resolver that <see cref="ConfigTracer.WithKubernetes"/> adds, for hosts that pass resolvers around
    /// on their own, such as an <see cref="Sources.IConfigSource"/> that reads a live cluster. It links environment
    /// variables and mounted files to the workload, ConfigMaps and Secrets in these manifests, and Secret keys to the
    /// ExternalSecrets and SecretProviderClasses that fill them.
    /// </summary>
    /// <param name="workload">The workload name. Needed only when the manifests hold more than one.</param>
    /// <param name="container">The container name. Needed only when the workload has more than one.</param>
    public IOriginResolver CreateResolver(string? workload = null, string? container = null) => new KubernetesResolver(this, workload, container);

    /// <summary>Loads manifests from a single file.</summary>
    public static KubernetesManifests LoadFile(string path) => LoadFiles(path);

    /// <summary>Adds more manifest text to this set.</summary>
    public void Add(string text, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<YamlNode> docs;
        try
        {
            docs = Yaml.LoadDocuments(text);
        }
        catch (YamlException ex)
        {
            throw new FormatException($"Could not parse Kubernetes manifests in {sourceName}: {ex.Message}", ex);
        }

        foreach (var doc in docs)
        {
            AddObject(doc, sourceName);
        }
    }

    internal void AddObject(YamlNode obj, string sourceName)
    {
        var kind = obj.Str("kind");
        var name = obj.Get("metadata").Str("name") ?? "(unnamed)";
        _createdSecrets = null;
        var apiVersion = obj.Str("apiVersion");
        if (ExternalSecretsReader.IsExternalSecrets(apiVersion))
        {
            switch (kind)
            {
                case "ExternalSecret":
                    _externalSecrets.Add(ExternalSecretsReader.ReadExternalSecret(obj, name, sourceName));
                    return;
                case "SecretStore" or "ClusterSecretStore":
                    _secretStores.RemoveAll(s => s.Kind == kind && s.Name == name);
                    _secretStores.Add(ExternalSecretsReader.ReadSecretStore(obj, kind, name, sourceName));
                    return;
                case "ExternalSecretList" or "SecretStoreList" or "ClusterSecretStoreList":
                    break;
                default:
                    return;
            }
        }
        else if (ExternalSecretsReader.IsSecretsStoreCsi(apiVersion))
        {
            if (kind == "SecretProviderClass")
            {
                _secretProviderClasses[name] = ExternalSecretsReader.ReadSecretProviderClass(obj, name, sourceName);
                return;
            }

            if (kind != "SecretProviderClassList")
            {
                return;
            }
        }

        switch (kind)
        {
            case null:
                return;
            case "List" or "PodList" or "ConfigMapList" or "SecretList" or "DeploymentList"
                or "ExternalSecretList" or "SecretStoreList" or "ClusterSecretStoreList" or "SecretProviderClassList":
                foreach (var item in obj.Get("items").Items())
                {
                    AddObject(item, sourceName);
                }

                return;
            case "ConfigMap":
                _configMaps[name] = new KubernetesData(name, ReadData(obj, decode: false), sourceName);
                return;
            case "Secret":
                _secrets[name] = new KubernetesData(name, ReadSecretData(obj), sourceName);
                return;
            case "Pod":
                AddWorkload(kind, name, obj.Get("spec"), sourceName);
                return;
            case "CronJob":
                AddWorkload(kind, name, obj.Get("spec").Get("jobTemplate").Get("spec").Get("template").Get("spec"), sourceName);
                return;
            default:
                if (PodTemplateKinds.Contains(kind))
                {
                    AddWorkload(kind, name, obj.Get("spec").Get("template").Get("spec"), sourceName);
                }

                return;
        }
    }

    private void AddWorkload(string kind, string name, YamlNode? podSpec, string sourceName)
    {
        if (podSpec is null)
        {
            return;
        }

        var containers = podSpec.Get("containers").Items().Select(c => ReadContainer(c, isInit: false))
            .Concat(podSpec.Get("initContainers").Items().Select(c => ReadContainer(c, isInit: true)))
            .ToList();
        var volumes = podSpec.Get("volumes").Items().Select(ReadVolume).ToList();
        _workloads.Add(new Workload(kind, name, containers, volumes, sourceName));
    }

    private static Container ReadContainer(YamlNode c, bool isInit)
    {
        var env = new List<EnvEntry>();
        foreach (var e in c.Get("env").Items())
        {
            var envName = e.Str("name");
            if (envName is null)
            {
                continue;
            }

            var from = e.Get("valueFrom");
            EnvValueSource source;
            if (from.Get("configMapKeyRef") is { } cm)
            {
                source = new EnvValueSource.FromConfigMap(cm.Str("name") ?? "", cm.Str("key") ?? envName);
            }
            else if (from.Get("secretKeyRef") is { } sk)
            {
                source = new EnvValueSource.FromSecret(sk.Str("name") ?? "", sk.Str("key") ?? envName);
            }
            else if (from.Get("fieldRef") is { } fr)
            {
                source = new EnvValueSource.FromField(fr.Str("fieldPath") ?? "");
            }
            else if (from.Get("resourceFieldRef") is { } rf)
            {
                source = new EnvValueSource.FromField(rf.Str("resource") ?? "");
            }
            else
            {
                source = new EnvValueSource.Literal(e.Get("value").Scalar() ?? "");
            }

            env.Add(new EnvEntry(envName, source, e.Line()));
        }

        var envFrom = new List<EnvFromEntry>();
        foreach (var ef in c.Get("envFrom").Items())
        {
            var prefix = ef.Str("prefix") ?? "";
            if (ef.Get("configMapRef").Str("name") is { } cmName)
            {
                envFrom.Add(new EnvFromEntry(cmName, IsSecret: false, prefix));
            }
            else if (ef.Get("secretRef").Str("name") is { } secretName)
            {
                envFrom.Add(new EnvFromEntry(secretName, IsSecret: true, prefix));
            }
        }

        var mounts = c.Get("volumeMounts").Items()
            .Select(m => new VolumeMount(m.Str("name") ?? "", m.Str("mountPath") ?? "", m.Str("subPath")))
            .ToList();

        return new Container(c.Str("name") ?? "(unnamed)", env, envFrom, mounts, isInit);
    }

    private static Volume ReadVolume(YamlNode v)
    {
        var name = v.Str("name") ?? "";
        var projections = new List<VolumeProjection>();
        if (v.Get("configMap") is { } cm)
        {
            projections.Add(ReadProjection(cm, cm.Str("name"), isSecret: false));
        }

        if (v.Get("secret") is { } s)
        {
            projections.Add(ReadProjection(s, s.Str("secretName"), isSecret: true));
        }

        foreach (var src in v.Get("projected").Get("sources").Items())
        {
            if (src.Get("configMap") is { } pcm)
            {
                projections.Add(ReadProjection(pcm, pcm.Str("name"), isSecret: false));
            }

            if (src.Get("secret") is { } ps)
            {
                projections.Add(ReadProjection(ps, ps.Str("name"), isSecret: true));
            }
        }

        var csi = v.Get("csi");
        var secretProviderClass = csi.Str("driver") == "secrets-store.csi.k8s.io"
            ? csi.Get("volumeAttributes").Str("secretProviderClass")
            : null;
        return new Volume(name, projections) { SecretProviderClass = secretProviderClass };
    }

    private static VolumeProjection ReadProjection(YamlNode node, string? sourceName, bool isSecret)
    {
        var items = node.Get("items").Items()
            .Select(i => new KeyValuePair<string, string>(i.Str("key") ?? "", i.Str("path") ?? i.Str("key") ?? ""))
            .Where(i => i.Key.Length > 0)
            .ToList();
        return new VolumeProjection(sourceName ?? "", isSecret, items);
    }

    /// <summary>
    /// The Secret named <paramref name="name"/>: the one in the manifests, or else the one an ExternalSecret or a
    /// SecretProviderClass creates, with its keys and unknown values.
    /// </summary>
    internal bool TryGetSecret(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out KubernetesData? data)
    {
        if (_secrets.TryGetValue(name, out data))
        {
            return true;
        }

        return CreatedSecrets().TryGetValue(name, out data);
    }

    /// <summary>The ConfigMap or Secret named <paramref name="name"/>, counting Secrets that operators create.</summary>
    internal bool TryGetData(bool isSecret, string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out KubernetesData? data)
    {
        if (isSecret)
        {
            return TryGetSecret(name, out data);
        }

        return _configMaps.TryGetValue(name, out data);
    }

    /// <summary>The ExternalSecrets and SecretProviderClasses that fill key <paramref name="key"/> of Secret <paramref name="secretName"/>.</summary>
    internal List<SecretOrigin> SecretOrigins(string secretName, string key)
    {
        var origins = new List<SecretOrigin>();
        foreach (var es in _externalSecrets.Where(e => string.Equals(e.TargetSecret, secretName, StringComparison.Ordinal)))
        {
            var template = es.HasTemplate ? "; its target template may change the key or the value" : "";
            var matched = false;
            foreach (var item in es.Data.Where(d => string.Equals(d.SecretKey, key, StringComparison.Ordinal)))
            {
                matched = true;
                if (item.RemoteKey is null)
                {
                    continue;
                }

                var store = item.Store ?? es.Store;
                var reference = new ExternalSecretRef(ProviderOf(store), store?.Name, item.RemoteKey, item.Property, item.Version);
                origins.Add(new SecretOrigin(
                    reference,
                    $"ExternalSecret {es.Name}",
                    $"remoteRef {Describe(reference)}, {es.SourceName}:{item.Line}",
                    $"{StoreNote(store, reference.Provider)} writes Secret {secretName} key {key}{template}",
                    IsInferred: false));
            }

            if (matched)
            {
                continue;
            }

            foreach (var item in es.DataFrom.Where(d => d.RemoteKey is not null))
            {
                var store = item.Store ?? es.Store;
                var reference = new ExternalSecretRef(ProviderOf(store), store?.Name, item.RemoteKey!, Version: item.Version);
                origins.Add(new SecretOrigin(
                    reference,
                    $"ExternalSecret {es.Name}",
                    $"dataFrom extract {Describe(reference)}, {es.SourceName}:{item.Line}",
                    $"{StoreNote(store, reference.Provider)} writes every property of the remote secret into Secret {secretName}, so key {key} probably comes from it{template}",
                    IsInferred: true));
            }
        }

        foreach (var spc in _secretProviderClasses.Values.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            foreach (var synced in spc.SecretObjects.Where(s => string.Equals(s.SecretName, secretName, StringComparison.Ordinal)))
            {
                foreach (var (secretKey, objectName) in synced.Keys.Where(k => string.Equals(k.Key, key, StringComparison.Ordinal)))
                {
                    if (FindObject(spc, objectName) is { } origin)
                    {
                        origins.Add(origin with
                        {
                            Note = $"{origin.Note}; synced into Secret {secretName} key {secretKey} while a pod mounts the volume",
                        });
                    }
                }
            }
        }

        return origins;
    }

    /// <summary>The layer for the file <paramref name="fileName"/> that a SecretProviderClass mounts, when it lists that object.</summary>
    internal static SecretOrigin? FindObject(SecretProviderClass spc, string fileName)
    {
        var obj = spc.Objects.FirstOrDefault(o => string.Equals(o.FileName, fileName, StringComparison.Ordinal));
        if (obj is null)
        {
            return null;
        }

        var reference = new ExternalSecretRef(spc.Provider, spc.Name, obj.RemoteKey, obj.Property, obj.Version);
        return new SecretOrigin(
            reference,
            $"SecretProviderClass {spc.Name}",
            $"object {Describe(reference)}, {spc.SourceName}:{spc.Line}",
            spc.Provider is null ? "Secrets Store CSI driver" : $"Secrets Store CSI driver, provider {spc.Provider}",
            IsInferred: false);
    }

    private string? ProviderOf(SecretStoreReference? store) =>
        store is null ? null : _secretStores.FirstOrDefault(s => s.Kind == store.Kind && s.Name == store.Name)?.Provider;

    private static string StoreNote(SecretStoreReference? store, string? provider) =>
        store is null
            ? "External Secrets Operator"
            : $"External Secrets Operator, {store.Kind} {store.Name}" + (provider is null ? " (not loaded)" : $" ({provider})");

    private static string Describe(ExternalSecretRef reference) =>
        reference.RemoteKey
        + (reference.Property is null ? "" : $", property {reference.Property}")
        + (reference.Version is null ? "" : $", version {reference.Version}");

    // The Secrets that ExternalSecrets and SecretProviderClasses create, for names that are not in the manifests.
    private Dictionary<string, KubernetesData> CreatedSecrets()
    {
        if (_createdSecrets is not null)
        {
            return _createdSecrets;
        }

        var created = new Dictionary<string, (Dictionary<string, KubernetesValue> Data, string SourceName, string CreatedBy, bool Unknown)>(StringComparer.Ordinal);
        void Add(string secretName, string sourceName, string createdBy, bool unknownKeys, IEnumerable<(string Key, int Line)> keys)
        {
            if (!created.TryGetValue(secretName, out var entry))
            {
                entry = (new Dictionary<string, KubernetesValue>(StringComparer.Ordinal), sourceName, createdBy, false);
            }

            foreach (var (key, line) in keys)
            {
                entry.Data.TryAdd(key, new KubernetesValue(null, line));
            }

            created[secretName] = entry with { Unknown = entry.Unknown || unknownKeys };
        }

        foreach (var es in _externalSecrets)
        {
            Add(es.TargetSecret, es.SourceName, $"ExternalSecret {es.Name}", es.DataFrom.Count > 0 || es.HasTemplate, es.Data.Select(d => (d.SecretKey, d.Line)));
        }

        foreach (var spc in _secretProviderClasses.Values.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            foreach (var synced in spc.SecretObjects)
            {
                Add(synced.SecretName, spc.SourceName, $"SecretProviderClass {spc.Name}", unknownKeys: false, synced.Keys.Select(k => (k.Key, synced.Line)));
            }
        }

        _createdSecrets = created
            .Where(c => !_secrets.ContainsKey(c.Key))
            .ToDictionary(
                c => c.Key,
                c => new KubernetesData(c.Key, c.Value.Data, c.Value.SourceName) { CreatedBy = c.Value.CreatedBy, HasUnknownKeys = c.Value.Unknown },
                StringComparer.Ordinal);
        return _createdSecrets;
    }

    private static Dictionary<string, KubernetesValue> ReadData(YamlNode obj, bool decode)
    {
        var data = new Dictionary<string, KubernetesValue>(StringComparer.Ordinal);
        foreach (var (key, value) in obj.Get("data").Pairs())
        {
            data[key] = new KubernetesValue(value.Scalar(), value.Line());
        }

        if (!decode)
        {
            foreach (var (key, value) in obj.Get("binaryData").Pairs())
            {
                data.TryAdd(key, new KubernetesValue(null, value.Line()));
            }
        }

        return data;
    }

    private static Dictionary<string, KubernetesValue> ReadSecretData(YamlNode obj)
    {
        var data = new Dictionary<string, KubernetesValue>(StringComparer.Ordinal);
        foreach (var (key, value) in obj.Get("data").Pairs())
        {
            string? decoded = null;
            if (value.Scalar() is { } b64)
            {
                try
                {
                    decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                }
                catch (FormatException)
                {
                    decoded = null;
                }
            }

            data[key] = new KubernetesValue(decoded, value.Line());
        }

        foreach (var (key, value) in obj.Get("stringData").Pairs())
        {
            data[key] = new KubernetesValue(value.Scalar(), value.Line());
        }

        return data;
    }
}

/// <summary>A workload with a pod spec.</summary>
public sealed record Workload(string Kind, string Name, IReadOnlyList<Container> Containers, IReadOnlyList<Volume> Volumes, string SourceName)
{
    /// <summary>The workload as <c>Kind/name</c>.</summary>
    public string DisplayName => $"{Kind}/{Name}";
}

/// <summary>A container in a pod spec.</summary>
/// <param name="Name">The container name.</param>
/// <param name="Env">The <c>env</c> entries.</param>
/// <param name="EnvFrom">The <c>envFrom</c> entries.</param>
/// <param name="VolumeMounts">The <c>volumeMounts</c> entries.</param>
/// <param name="IsInitContainer">True for an entry in <c>initContainers</c>.</param>
public sealed record Container(
    string Name,
    IReadOnlyList<EnvEntry> Env,
    IReadOnlyList<EnvFromEntry> EnvFrom,
    IReadOnlyList<VolumeMount> VolumeMounts,
    bool IsInitContainer = false);

/// <summary>An <c>env</c> entry in a container.</summary>
public sealed record EnvEntry(string Name, EnvValueSource Source, int Line);

/// <summary>Where an <c>env</c> entry takes its value from.</summary>
public abstract record EnvValueSource
{
    private EnvValueSource()
    {
    }

    /// <summary>A literal <c>value</c>.</summary>
    public sealed record Literal(string Value) : EnvValueSource;

    /// <summary><c>valueFrom.configMapKeyRef</c>.</summary>
    public sealed record FromConfigMap(string Name, string Key) : EnvValueSource;

    /// <summary><c>valueFrom.secretKeyRef</c>.</summary>
    public sealed record FromSecret(string Name, string Key) : EnvValueSource;

    /// <summary><c>valueFrom.fieldRef</c> or <c>valueFrom.resourceFieldRef</c>.</summary>
    public sealed record FromField(string FieldPath) : EnvValueSource;
}

/// <summary>An <c>envFrom</c> entry in a container.</summary>
public sealed record EnvFromEntry(string Name, bool IsSecret, string Prefix);

/// <summary>A <c>volumeMounts</c> entry in a container.</summary>
public sealed record VolumeMount(string Name, string MountPath, string? SubPath);

/// <summary>A pod volume and the ConfigMaps or Secrets it projects.</summary>
public sealed record Volume(string Name, IReadOnlyList<VolumeProjection> Projections)
{
    /// <summary>
    /// The <c>SecretProviderClass</c> a Secrets Store CSI volume (<c>csi.driver: secrets-store.csi.k8s.io</c>) mounts,
    /// or null for other volumes.
    /// </summary>
    public string? SecretProviderClass { get; init; }
}

/// <summary>A ConfigMap or Secret projected into a volume, with optional key-to-path items.</summary>
public sealed record VolumeProjection(string SourceName, bool IsSecret, IReadOnlyList<KeyValuePair<string, string>> Items);

/// <summary>The data of a ConfigMap or Secret.</summary>
public sealed record KubernetesData(string Name, IReadOnlyDictionary<string, KubernetesValue> Data, string SourceName)
{
    /// <summary>
    /// For a Secret that is not in the manifests but that an operator creates, what creates it, for example
    /// <c>ExternalSecret db</c>. Its values are then unknown. Null for a ConfigMap or Secret that was loaded.
    /// </summary>
    public string? CreatedBy { get; init; }

    /// <summary>True when the Secret may hold keys that are not listed, because an ExternalSecret's <c>dataFrom</c> or template adds them.</summary>
    public bool HasUnknownKeys { get; init; }
}

/// <summary>A value in a ConfigMap or Secret, with the line it is on when known.</summary>
public sealed record KubernetesValue(string? Value, int Line);
