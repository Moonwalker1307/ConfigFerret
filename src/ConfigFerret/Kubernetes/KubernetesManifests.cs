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

    /// <summary>The workloads found, with their pod specs.</summary>
    public IReadOnlyList<Workload> Workloads => _workloads;

    /// <summary>The ConfigMaps found, by name.</summary>
    public IReadOnlyDictionary<string, KubernetesData> ConfigMaps => _configMaps;

    /// <summary>The Secrets found, by name. Values are decoded but always treated as sensitive.</summary>
    public IReadOnlyDictionary<string, KubernetesData> Secrets => _secrets;

    /// <summary>Parses manifests from YAML or JSON text. Multi-document YAML and <c>List</c> kinds are supported.</summary>
    /// <param name="text">The manifest text.</param>
    /// <param name="sourceName">A name for the text, used in output (for example a file name).</param>
    public static KubernetesManifests Parse(string text, string sourceName = "manifests")
    {
        var result = new KubernetesManifests();
        result.Add(text, sourceName);
        return result;
    }

    /// <summary>Loads manifests from files. Directories are searched for <c>*.yaml</c>, <c>*.yml</c> and <c>*.json</c>.</summary>
    public static KubernetesManifests LoadFiles(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var result = new KubernetesManifests();
        foreach (var path in paths)
        {
            var files = Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
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
        switch (kind)
        {
            case null:
                return;
            case "List" or "PodList" or "ConfigMapList" or "SecretList" or "DeploymentList":
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

        var containers = podSpec.Get("containers").Items()
            .Concat(podSpec.Get("initContainers").Items())
            .Select(ReadContainer)
            .ToList();
        var volumes = podSpec.Get("volumes").Items().Select(ReadVolume).ToList();
        _workloads.Add(new Workload(kind, name, containers, volumes, sourceName));
    }

    private static Container ReadContainer(YamlNode c)
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

        return new Container(c.Str("name") ?? "(unnamed)", env, envFrom, mounts);
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

        return new Volume(name, projections);
    }

    private static VolumeProjection ReadProjection(YamlNode node, string? sourceName, bool isSecret)
    {
        var items = node.Get("items").Items()
            .Select(i => new KeyValuePair<string, string>(i.Str("key") ?? "", i.Str("path") ?? i.Str("key") ?? ""))
            .Where(i => i.Key.Length > 0)
            .ToList();
        return new VolumeProjection(sourceName ?? "", isSecret, items);
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
public sealed record Container(string Name, IReadOnlyList<EnvEntry> Env, IReadOnlyList<EnvFromEntry> EnvFrom, IReadOnlyList<VolumeMount> VolumeMounts);

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
public sealed record Volume(string Name, IReadOnlyList<VolumeProjection> Projections);

/// <summary>A ConfigMap or Secret projected into a volume, with optional key-to-path items.</summary>
public sealed record VolumeProjection(string SourceName, bool IsSecret, IReadOnlyList<KeyValuePair<string, string>> Items);

/// <summary>The data of a ConfigMap or Secret.</summary>
public sealed record KubernetesData(string Name, IReadOnlyDictionary<string, KubernetesValue> Data, string SourceName);

/// <summary>A value in a ConfigMap or Secret, with the line it is on when known.</summary>
public sealed record KubernetesValue(string? Value, int Line);
