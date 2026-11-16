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
    private readonly Namespaced<KubernetesData, string> _configMaps = new(d => d.Namespace, d => d.Name);
    private readonly Namespaced<KubernetesData, string> _secrets = new(d => d.Namespace, d => d.Name);
    private readonly List<ExternalSecret> _externalSecrets = [];
    private readonly Namespaced<SecretStore, (string Kind, string Name)> _secretStores = new(s => s.Namespace, s => (s.Kind, s.Name));
    private readonly Namespaced<SecretProviderClass, string> _secretProviderClasses = new(s => s.Namespace, s => s.Name);
    private Namespaced<KubernetesData, string>? _createdSecrets;
    private List<SecretStore>? _secretStoreList;

    // When each ExternalSecret, and the SecretProviderClass of each namespace and name, was loaded, counted over both.
    private readonly List<long> _externalSecretLoads = [];
    private readonly Dictionary<(string? Namespace, string Name), long> _secretProviderClassLoads = [];
    private long _creatorsLoaded;
    private readonly List<string> _warnings = [];

    /// <summary>The workloads found, with their pod specs.</summary>
    public IReadOnlyList<Workload> Workloads => _workloads;

    /// <summary>
    /// The ConfigMaps found, by name. For a name that is in more than one namespace, the one loaded last; a workload
    /// still gets the one in its own namespace.
    /// </summary>
    public IReadOnlyDictionary<string, KubernetesData> ConfigMaps => _configMaps.Last;

    /// <summary>
    /// The Secrets found, by name like <see cref="ConfigMaps"/>. Values are decoded but always treated as sensitive.
    /// </summary>
    public IReadOnlyDictionary<string, KubernetesData> Secrets => _secrets.Last;

    /// <summary>
    /// The External Secrets Operator <c>ExternalSecret</c>s found. The Secret each one writes is treated as existing,
    /// with its <c>spec.data</c> keys and unknown values, when the Secret itself is not in the manifests.
    /// </summary>
    public IReadOnlyList<ExternalSecret> ExternalSecrets => _externalSecrets;

    /// <summary>
    /// The External Secrets Operator <c>SecretStore</c>s and <c>ClusterSecretStore</c>s found, one per kind, namespace
    /// and name. The list is a snapshot taken when it is read, so it does not show stores added later.
    /// </summary>
    public IReadOnlyList<SecretStore> SecretStores => _secretStoreList ??= [.. _secretStores.All];

    /// <summary>
    /// The Secrets Store CSI driver <c>SecretProviderClass</c>es found, by name like <see cref="ConfigMaps"/>. The
    /// Secrets they sync (<c>spec.secretObjects</c>) are treated as existing like an ExternalSecret's target.
    /// </summary>
    public IReadOnlyDictionary<string, SecretProviderClass> SecretProviderClasses => _secretProviderClasses.Last;

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
    /// following symbolic links below them. A file is named in output by its file name, and a file found in a directory
    /// by its path inside the directory, with forward slashes.
    /// </summary>
    public static KubernetesManifests LoadFiles(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var result = new KubernetesManifests();
        foreach (var path in paths)
        {
            // Links are skipped: a link to a parent folder never ends, and one out of the folder reads other files.
            var isFolder = Directory.Exists(path);
            var files = isFolder
                ? Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint })
                    .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.Ordinal)
                : (IEnumerable<string>)[path];
            foreach (var file in files)
            {
                // Folders such as base and overlays often hold files of the same name, so the path tells them apart. Only
                // the system's own separator becomes "/": on Linux and macOS a "\" is part of a file name.
                result.Add(File.ReadAllText(file), isFolder ? Path.GetRelativePath(path, file).Replace(Path.DirectorySeparatorChar, '/') : Path.GetFileName(file));
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
    /// <param name="workload">
    /// The workload name, or <c>namespace/name</c> when the name is in more than one namespace (<c>/name</c> for the
    /// one without a namespace). Needed only when the manifests hold more than one workload.
    /// </param>
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

    /// <summary>The objects that were left out while loading, such as one whose name holds a slash. They never hold a value.</summary>
    internal IReadOnlyList<string> Warnings => _warnings;

    internal void AddObject(YamlNode obj, string sourceName)
    {
        var kind = obj.Str("kind");
        var name = obj.Get("metadata").Str("name") ?? "(unnamed)";
        var ns = obj.Get("metadata").Str("namespace") is { Length: > 0 } value ? value : null;
        _createdSecrets = null;
        var apiVersion = obj.Str("apiVersion");
        if (ExternalSecretsReader.IsExternalSecrets(apiVersion))
        {
            switch (kind)
            {
                case "ExternalSecret" or "SecretStore" or "ClusterSecretStore" when IsSkipped(kind, name, sourceName):
                    return;
                case "ExternalSecret":
                    _externalSecrets.Add(ExternalSecretsReader.ReadExternalSecret(obj, name, sourceName) with { Namespace = ns });
                    _externalSecretLoads.Add(_creatorsLoaded++);
                    return;
                case "SecretStore" or "ClusterSecretStore":
                    // A ClusterSecretStore is not in a namespace, whatever its metadata says.
                    ns = kind == "SecretStore" ? ns : null;
                    _secretStores.Add(ExternalSecretsReader.ReadSecretStore(obj, kind, name, sourceName) with { Namespace = ns });
                    _secretStoreList = null;
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
                if (!IsSkipped(kind, name, sourceName))
                {
                    _secretProviderClasses.Add(ExternalSecretsReader.ReadSecretProviderClass(obj, name, sourceName) with { Namespace = ns });
                    _secretProviderClassLoads[(ns, name)] = _creatorsLoaded++;
                }

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
            case "ConfigMap" or "Secret" when IsSkipped(kind, name, sourceName):
                return;
            case "ConfigMap":
                _configMaps.Add(new KubernetesData(name, ReadData(obj, decode: false), sourceName) { Namespace = ns });
                return;
            case "Secret":
                _secrets.Add(new KubernetesData(name, ReadSecretData(obj), sourceName) { Namespace = ns });
                return;
            case "Pod":
                AddWorkload(kind, name, ns, obj.Get("spec"), sourceName);
                return;
            case "CronJob":
                AddWorkload(kind, name, ns, obj.Get("spec").Get("jobTemplate").Get("spec").Get("template").Get("spec"), sourceName);
                return;
            default:
                if (PodTemplateKinds.Contains(kind))
                {
                    AddWorkload(kind, name, ns, obj.Get("spec").Get("template").Get("spec"), sourceName);
                }

                return;
        }
    }

    private void AddWorkload(string kind, string name, string? ns, YamlNode? podSpec, string sourceName)
    {
        if (podSpec is null || IsSkipped(kind, name, sourceName))
        {
            return;
        }

        var containers = podSpec.Get("containers").Items().Select(c => ReadContainer(c, isInit: false))
            .Concat(podSpec.Get("initContainers").Items().Select(c => ReadContainer(c, isInit: true)))
            .ToList();
        var volumes = podSpec.Get("volumes").Items().Select(ReadVolume).ToList();
        _workloads.Add(new Workload(kind, name, containers, volumes, sourceName) { Namespace = ns });
    }

    // Kubernetes refuses a name with a slash. Such a name reads like namespace/name, so the object could pass for one of
    // another namespace; it is left out with a warning instead.
    private bool IsSkipped(string kind, string name, string sourceName)
    {
        if (!name.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        _warnings.Add($"Skipped {kind} {name} in {sourceName}: a Kubernetes name cannot hold a slash.");
        return true;
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
    /// The Secret named <paramref name="name"/> that a workload in namespace <paramref name="ns"/> sees: the one in the
    /// manifests, or else the one an ExternalSecret or a SecretProviderClass creates, with its keys and unknown values.
    /// </summary>
    internal bool TryGetSecret(string? ns, string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out KubernetesData? data)
    {
        data = _secrets.Find(ns, name) ?? CreatedSecrets().Find(ns, name);
        return data is not null;
    }

    /// <summary>
    /// The ConfigMap or Secret named <paramref name="name"/> that a workload in namespace <paramref name="ns"/> sees,
    /// counting Secrets that operators create.
    /// </summary>
    internal bool TryGetData(bool isSecret, string? ns, string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out KubernetesData? data)
    {
        if (isSecret)
        {
            return TryGetSecret(ns, name, out data);
        }

        data = _configMaps.Find(ns, name);
        return data is not null;
    }

    /// <summary>The ConfigMap or Secret named <paramref name="name"/> in the manifests that an object in namespace <paramref name="ns"/> sees.</summary>
    internal KubernetesData? FindLoaded(bool isSecret, string? ns, string name) => (isSecret ? _secrets : _configMaps).Find(ns, name);

    /// <summary>The SecretProviderClass named <paramref name="name"/> that a workload in namespace <paramref name="ns"/> sees.</summary>
    internal SecretProviderClass? FindSecretProviderClass(string? ns, string name) => _secretProviderClasses.Find(ns, name);

    /// <summary>
    /// True when the ConfigMap or Secret named <paramref name="name"/> is in more than one namespace, counting none as
    /// one and counting the Secrets that operators create, so that a workload without a namespace gets one of them. It
    /// gets the one loaded last, whose namespace is <paramref name="used"/>; for a Secret, the one in the manifests
    /// comes before created ones, and <paramref name="overCreated"/> says that a created one in another namespace lost.
    /// </summary>
    internal bool IsDataInSeveralNamespaces(bool isSecret, string name, out string? used, out bool overCreated)
    {
        overCreated = false;
        if (!isSecret)
        {
            used = _configMaps.Find(null, name)?.Namespace;
            return _configMaps.IsInSeveralNamespaces(name);
        }

        var created = CreatedSecrets();
        if (!_secrets.Last.TryGetValue(name, out var loaded))
        {
            used = created.Find(null, name)?.Namespace;
            return created.IsInSeveralNamespaces(name);
        }

        used = loaded.Namespace;
        overCreated = created.Last.TryGetValue(name, out var other) && (created.IsInSeveralNamespaces(name) || other.Namespace != used);
        return overCreated || _secrets.IsInSeveralNamespaces(name);
    }

    /// <summary>Like <see cref="IsDataInSeveralNamespaces"/>, for the SecretProviderClass named <paramref name="name"/>.</summary>
    internal bool IsSecretProviderClassInSeveralNamespaces(string name, out string? used)
    {
        used = _secretProviderClasses.Find(null, name)?.Namespace;
        return _secretProviderClasses.IsInSeveralNamespaces(name);
    }

    /// <summary>
    /// The ExternalSecrets and SecretProviderClasses that fill key <paramref name="key"/> of Secret <paramref name="secretName"/>
    /// for a workload in namespace <paramref name="ns"/>.
    /// </summary>
    internal List<SecretOrigin> SecretOrigins(string? ns, string secretName, string key)
    {
        var origins = new List<SecretOrigin>();
        foreach (var es in _externalSecrets.Where(e => Sees(ns, e.Namespace) && string.Equals(e.TargetSecret, secretName, StringComparison.Ordinal)))
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
                var reference = new ExternalSecretRef(ProviderOf(es.Namespace, store), store?.Name, item.RemoteKey, item.Property, item.Version);
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
                var reference = new ExternalSecretRef(ProviderOf(es.Namespace, store), store?.Name, item.RemoteKey!, Version: item.Version);
                origins.Add(new SecretOrigin(
                    reference,
                    $"ExternalSecret {es.Name}",
                    $"dataFrom extract {Describe(reference)}, {es.SourceName}:{item.Line}",
                    $"{StoreNote(store, reference.Provider)} writes every property of the remote secret into Secret {secretName}, so key {key} probably comes from it{template}",
                    IsInferred: true));
            }
        }

        foreach (var spc in _secretProviderClasses.All.Where(s => Sees(ns, s.Namespace)).OrderBy(s => s.Name, StringComparer.Ordinal))
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

    // An ExternalSecret in namespace ns uses the SecretStore of that name in its namespace, or a ClusterSecretStore,
    // which has no namespace.
    private string? ProviderOf(string? ns, SecretStoreReference? store) =>
        store is null ? null : _secretStores.Find(ns, (store.Kind, store.Name))?.Provider;

    private static string StoreNote(SecretStoreReference? store, string? provider) =>
        store is null
            ? "External Secrets Operator"
            : $"External Secrets Operator, {store.Kind} {store.Name}" + (provider is null ? " (not loaded)" : $" ({provider})");

    private static string Describe(ExternalSecretRef reference) =>
        reference.RemoteKey
        + (reference.Property is null ? "" : $", property {reference.Property}")
        + (reference.Version is null ? "" : $", version {reference.Version}");

    // The Secrets that ExternalSecrets and SecretProviderClasses create, each in the namespace of what creates it. A
    // workload sees one only when no Secret of that name in the manifests is seen first.
    private Namespaced<KubernetesData, string> CreatedSecrets()
    {
        if (_createdSecrets is not null)
        {
            return _createdSecrets;
        }

        var created = new Dictionary<(string? Namespace, string Name), (Dictionary<string, KubernetesValue> Data, string SourceName, string CreatedBy, bool Unknown)>();
        void Add(string? ns, string secretName, string sourceName, string createdBy, bool unknownKeys, IEnumerable<(string Key, int Line)> keys)
        {
            if (!created.TryGetValue((ns, secretName), out var entry))
            {
                entry = (new Dictionary<string, KubernetesValue>(StringComparer.Ordinal), sourceName, createdBy, false);
            }

            foreach (var (key, line) in keys)
            {
                entry.Data.TryAdd(key, new KubernetesValue(null, line));
            }

            created[(ns, secretName)] = entry with { Unknown = entry.Unknown || unknownKeys };
        }

        // When the last object that creates each Secret was loaded, so that the one loaded last is also the last one added.
        var lastLoaded = new Dictionary<(string? Namespace, string Name), long>();
        void Loaded(string? ns, string secretName, long load) =>
            lastLoaded[(ns, secretName)] = Math.Max(load, lastLoaded.GetValueOrDefault((ns, secretName), -1));

        for (var i = 0; i < _externalSecrets.Count; i++)
        {
            var es = _externalSecrets[i];
            Add(es.Namespace, es.TargetSecret, es.SourceName, $"ExternalSecret {es.Name}", es.DataFrom.Count > 0 || es.HasTemplate, es.Data.Select(d => (d.SecretKey, d.Line)));
            Loaded(es.Namespace, es.TargetSecret, _externalSecretLoads[i]);
        }

        foreach (var spc in _secretProviderClasses.All.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            foreach (var synced in spc.SecretObjects)
            {
                Add(spc.Namespace, synced.SecretName, spc.SourceName, $"SecretProviderClass {spc.Name}", unknownKeys: false, synced.Keys.Select(k => (k.Key, synced.Line)));
                Loaded(spc.Namespace, synced.SecretName, _secretProviderClassLoads[(spc.Namespace, spc.Name)]);
            }
        }

        _createdSecrets = new Namespaced<KubernetesData, string>(d => d.Namespace, d => d.Name);
        foreach (var ((ns, secretName), entry) in created.OrderBy(c => lastLoaded[c.Key]))
        {
            _createdSecrets.Add(new KubernetesData(secretName, entry.Data, entry.SourceName) { Namespace = ns, CreatedBy = entry.CreatedBy, HasUnknownKeys = entry.Unknown });
        }

        return _createdSecrets;
    }

    // Whether something in namespace ns can use an object in namespace objectNamespace. Null is no namespace: an
    // object without one goes to whatever namespace it is applied to, and a workload without one may be in any.
    private static bool Sees(string? ns, string? objectNamespace) => ns is null || objectNamespace is null || ns == objectNamespace;

    // ConfigMaps, Secrets, SecretProviderClasses or SecretStores: one per namespace and name, a later one replacing an
    // earlier one. Every lookup is by key, so neither adding an object nor finding one walks the others of its name.
    internal sealed class Namespaced<T, TName>(Func<T, string?> namespaceOf, Func<T, TName> nameOf)
        where T : class
        where TName : notnull
    {
        private readonly Dictionary<(string? Namespace, TName Name), LinkedListNode<T>> _exact = [];
        private readonly Dictionary<TName, T> _last = [];
        private readonly HashSet<TName> _inSeveral = [];
        private readonly LinkedList<T> _all = new();

        /// <summary>The object of each name loaded last, whatever its namespace, in the order the names were first loaded.</summary>
        public IReadOnlyDictionary<TName, T> Last => _last;

        /// <summary>Every object, in the order loaded; one that replaces another of its namespace and name goes last.</summary>
        public IEnumerable<T> All => _all;

        public void Add(T item)
        {
            var key = (namespaceOf(item), nameOf(item));
            if (_exact.Remove(key, out var replaced))
            {
                _all.Remove(replaced);
            }
            else if (_last.ContainsKey(key.Item2))
            {
                _inSeveral.Add(key.Item2);
            }

            _exact[key] = _all.AddLast(item);
            _last[key.Item2] = item;
        }

        /// <summary>
        /// The object a reference from namespace <paramref name="ns"/> gets: the one in that namespace, else the one
        /// without a namespace. A reference without a namespace sees every namespace and gets the one loaded last.
        /// </summary>
        public T? Find(string? ns, TName name) => ns is null ? _last.GetValueOrDefault(name) : Exact(ns, name) ?? Exact(null, name);

        /// <summary>True when objects of the name are in more than one namespace, counting none as one.</summary>
        public bool IsInSeveralNamespaces(TName name) => _inSeveral.Contains(name);

        private T? Exact(string? ns, TName name) => _exact.TryGetValue((ns, name), out var node) ? node.Value : null;
    }

    private static Dictionary<string, KubernetesValue> ReadData(YamlNode obj, bool decode)
    {
        var data = new Dictionary<string, KubernetesValue>(StringComparer.Ordinal);
        foreach (var (key, value) in obj.Get("data").Pairs())
        {
            data[key] = new KubernetesValue(value.Scalar(), value.Line()) { IsLiteralBlock = IsLiteralBlock(value) };
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
            data[key] = new KubernetesValue(value.Scalar(), value.Line()) { IsLiteralBlock = IsLiteralBlock(value) };
        }

        return data;
    }

    private static bool IsLiteralBlock(YamlNode value) => value is YamlScalarNode { Style: ScalarStyle.Literal };
}

/// <summary>A workload with a pod spec.</summary>
public sealed record Workload(string Kind, string Name, IReadOnlyList<Container> Containers, IReadOnlyList<Volume> Volumes, string SourceName)
{
    /// <summary>
    /// The workload's <c>metadata.namespace</c>, or null when it has none. Its ConfigMaps, Secrets and
    /// SecretProviderClasses are looked up in this namespace, and then among the objects without a namespace. A
    /// workload without a namespace sees every namespace, and gets the object of each name loaded last.
    /// </summary>
    public string? Namespace { get; init; }

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
    /// The object's <c>metadata.namespace</c>, or null when it has none. For a Secret that an operator creates, the
    /// namespace of what creates it.
    /// </summary>
    public string? Namespace { get; init; }

    /// <summary>
    /// For a Secret that is not in the manifests but that an operator creates, what creates it, for example
    /// <c>ExternalSecret db</c>. Its values are then unknown. Null for a ConfigMap or Secret that was loaded.
    /// </summary>
    public string? CreatedBy { get; init; }

    /// <summary>True when the Secret may hold keys that are not listed, because an ExternalSecret's <c>dataFrom</c> or template adds them.</summary>
    public bool HasUnknownKeys { get; init; }
}

/// <summary>A value in a ConfigMap or Secret, with the line it is on when known.</summary>
public sealed record KubernetesValue(string? Value, int Line)
{
    /// <summary>True when the value is a literal block (<c>|</c>), so its lines are the lines below <see cref="Line"/>.</summary>
    internal bool IsLiteralBlock { get; init; }
}
