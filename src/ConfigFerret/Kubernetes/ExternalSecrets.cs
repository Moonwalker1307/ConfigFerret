using ConfigFerret.Internal;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ConfigFerret.Kubernetes;

/// <summary>
/// An External Secrets Operator <c>ExternalSecret</c> (<c>external-secrets.io/v1beta1</c> or <c>v1</c>): it copies
/// secrets from an external store into the Kubernetes Secret <see cref="TargetSecret"/>.
/// </summary>
/// <param name="Name">The ExternalSecret's name.</param>
/// <param name="TargetSecret">The Secret it writes: <c>spec.target.name</c>, or the ExternalSecret's own name.</param>
/// <param name="Store">The store in <c>spec.secretStoreRef</c>, when set.</param>
/// <param name="Data">The <c>spec.data</c> entries, one per Secret key.</param>
/// <param name="DataFrom">The <c>spec.dataFrom</c> entries, which add keys that are only known once the store is read.</param>
/// <param name="HasTemplate">True when <c>spec.target.template</c> is set, so the Secret's keys and values may be computed.</param>
/// <param name="SourceName">The file or text the ExternalSecret was read from.</param>
/// <param name="Line">The line it starts on.</param>
public sealed record ExternalSecret(
    string Name,
    string TargetSecret,
    SecretStoreReference? Store,
    IReadOnlyList<ExternalSecretData> Data,
    IReadOnlyList<ExternalSecretDataFrom> DataFrom,
    bool HasTemplate,
    string SourceName,
    int Line);

/// <summary>A <c>secretStoreRef</c> or <c>sourceRef.storeRef</c>: a <c>SecretStore</c> or <c>ClusterSecretStore</c> by name.</summary>
public sealed record SecretStoreReference(string Kind, string Name);

/// <summary>One <c>spec.data</c> entry of an <see cref="ExternalSecret"/>.</summary>
/// <param name="SecretKey">The key in the target Secret.</param>
/// <param name="RemoteKey">The secret's name or path in the store (<c>remoteRef.key</c>). Null when not set.</param>
/// <param name="Property">The property inside the remote secret (<c>remoteRef.property</c>).</param>
/// <param name="Version">The pinned version (<c>remoteRef.version</c>).</param>
/// <param name="Store">The entry's own store (<c>sourceRef.storeRef</c>), which overrides the ExternalSecret's.</param>
/// <param name="Line">The line the entry is on.</param>
public sealed record ExternalSecretData(string SecretKey, string? RemoteKey, string? Property, string? Version, SecretStoreReference? Store, int Line);

/// <summary>One <c>spec.dataFrom</c> entry of an <see cref="ExternalSecret"/>.</summary>
/// <param name="RemoteKey">The secret whose properties become keys (<c>extract.key</c>). Null for <c>find</c> and generators.</param>
/// <param name="Version">The pinned version (<c>extract.version</c>).</param>
/// <param name="Store">The entry's own store (<c>sourceRef.storeRef</c>).</param>
/// <param name="Line">The line the entry is on.</param>
public sealed record ExternalSecretDataFrom(string? RemoteKey, string? Version, SecretStoreReference? Store, int Line);

/// <summary>An External Secrets Operator <c>SecretStore</c> or <c>ClusterSecretStore</c>.</summary>
/// <param name="Kind"><c>SecretStore</c> or <c>ClusterSecretStore</c>.</param>
/// <param name="Name">The store's name.</param>
/// <param name="Provider">The key under <c>spec.provider</c>, for example <c>azurekv</c>, <c>aws</c>, <c>vault</c> or <c>gcpsm</c>.</param>
/// <param name="SourceName">The file or text the store was read from.</param>
public sealed record SecretStore(string Kind, string Name, string? Provider, string SourceName);

/// <summary>A Secrets Store CSI driver <c>SecretProviderClass</c> (<c>secrets-store.csi.x-k8s.io</c>).</summary>
/// <param name="Name">The SecretProviderClass name.</param>
/// <param name="Provider">
/// <c>spec.provider</c> in External Secrets Operator's naming, so store resolvers match it the same way:
/// <c>azure</c> becomes <c>azurekv</c> and <c>gcp</c> becomes <c>gcpsm</c>; <c>aws</c> and <c>vault</c> stay.
/// </param>
/// <param name="Objects">The objects in <c>spec.parameters.objects</c> (or <c>secrets</c> for Google Cloud), each mounted as a file.</param>
/// <param name="SecretObjects">The Kubernetes Secrets the driver syncs from the mounted objects (<c>spec.secretObjects</c>).</param>
/// <param name="SourceName">The file or text it was read from.</param>
/// <param name="Line">The line it starts on.</param>
public sealed record SecretProviderClass(
    string Name,
    string? Provider,
    IReadOnlyList<SecretProviderObject> Objects,
    IReadOnlyList<SyncedSecret> SecretObjects,
    string SourceName,
    int Line);

/// <summary>An object a <see cref="SecretProviderClass"/> mounts.</summary>
/// <param name="FileName">The file name in the volume: the alias, file name or object name.</param>
/// <param name="RemoteKey">The secret's name or path in the store.</param>
/// <param name="Property">The property inside it (Vault <c>secretKey</c>, AWS <c>jmesPath</c>), when one is picked.</param>
/// <param name="Version">The pinned version, when set.</param>
public sealed record SecretProviderObject(string FileName, string RemoteKey, string? Property, string? Version);

/// <summary>A <c>secretObjects</c> entry: a Kubernetes Secret that the CSI driver fills from mounted objects.</summary>
/// <param name="SecretName">The Secret's name.</param>
/// <param name="Keys">Each Secret key and the object name (file name) it is copied from.</param>
/// <param name="Line">The line the entry is on.</param>
public sealed record SyncedSecret(string SecretName, IReadOnlyList<KeyValuePair<string, string>> Keys, int Line);

/// <summary>Reads External Secrets Operator and Secrets Store CSI driver objects.</summary>
internal static class ExternalSecretsReader
{
    public static bool IsExternalSecrets(string? apiVersion) =>
        apiVersion?.StartsWith("external-secrets.io/", StringComparison.Ordinal) == true;

    public static bool IsSecretsStoreCsi(string? apiVersion) =>
        apiVersion?.StartsWith("secrets-store.csi.x-k8s.io/", StringComparison.Ordinal) == true;

    public static ExternalSecret ReadExternalSecret(YamlNode obj, string name, string sourceName)
    {
        var spec = obj.Get("spec");
        var target = spec.Get("target");
        var data = new List<ExternalSecretData>();
        foreach (var item in spec.Get("data").Items())
        {
            if (item.Str("secretKey") is not { Length: > 0 } secretKey)
            {
                continue;
            }

            var remote = item.Get("remoteRef");
            data.Add(new ExternalSecretData(
                secretKey,
                NonEmpty(remote.Str("key")),
                NonEmpty(remote.Str("property")),
                NonEmpty(remote.Str("version")),
                ReadStore(item.Get("sourceRef").Get("storeRef")),
                item.Line()));
        }

        var dataFrom = spec.Get("dataFrom").Items()
            .Select(item => new ExternalSecretDataFrom(
                NonEmpty(item.Get("extract").Str("key")),
                NonEmpty(item.Get("extract").Str("version")),
                ReadStore(item.Get("sourceRef").Get("storeRef")),
                item.Line()))
            .ToList();

        return new ExternalSecret(
            name,
            NonEmpty(target.Str("name")) ?? name,
            ReadStore(spec.Get("secretStoreRef")),
            data,
            dataFrom,
            target.Get("template") is { } template && !template.IsNull(),
            sourceName,
            obj.Line());
    }

    public static SecretStore ReadSecretStore(YamlNode obj, string kind, string name, string sourceName) =>
        new(kind, name, obj.Get("spec").Get("provider").Pairs().Select(p => p.Key).FirstOrDefault(), sourceName);

    public static SecretProviderClass ReadSecretProviderClass(YamlNode obj, string name, string sourceName)
    {
        var spec = obj.Get("spec");
        var provider = spec.Str("provider") switch
        {
            null => null,
            "azure" => "azurekv",
            "gcp" => "gcpsm",
            var other => other,
        };

        var parameters = spec.Get("parameters");
        var objects = ReadObjects(parameters.Str("objects")).Concat(ReadObjects(parameters.Str("secrets"))).ToList();

        var synced = new List<SyncedSecret>();
        foreach (var item in spec.Get("secretObjects").Items())
        {
            if (item.Str("secretName") is not { Length: > 0 } secretName)
            {
                continue;
            }

            var keys = item.Get("data").Items()
                .Select(d => new KeyValuePair<string, string>(d.Str("key") ?? "", d.Str("objectName") ?? ""))
                .Where(k => k.Key.Length > 0 && k.Value.Length > 0)
                .ToList();
            synced.Add(new SyncedSecret(secretName, keys, item.Line()));
        }

        return new SecretProviderClass(name, provider, objects, synced, sourceName, obj.Line());
    }

    // The objects parameter is YAML inside a string: a list of maps (AWS, Vault, Google Cloud), or for Azure a map
    // with an "array" of strings that each hold one object's YAML. Text that is not valid YAML adds no objects.
    private static List<SecretProviderObject> ReadObjects(string? text)
    {
        var result = new List<SecretProviderObject>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var root in TryLoad(text))
        {
            var items = root.Get("array") is { } array
                ? array.Items().SelectMany(i => i.Scalar() is { } inner ? TryLoad(inner) : [i])
                : root.Items();
            foreach (var item in items.Take(1000))
            {
                AddObject(item, result);
            }
        }

        return result;
    }

    private static void AddObject(YamlNode item, List<SecretProviderObject> result)
    {
        var objectName = NonEmpty(item.Str("objectName"));
        var remoteKey = NonEmpty(item.Str("secretPath")) ?? NonEmpty(item.Str("resourceName")) ?? objectName;
        if (remoteKey is null)
        {
            return;
        }

        var fileName = NonEmpty(item.Str("objectAlias")) ?? NonEmpty(item.Str("fileName")) ?? NonEmpty(item.Str("path")) ?? objectName ?? remoteKey;
        var version = NonEmpty(item.Str("objectVersion"));
        result.Add(new SecretProviderObject(fileName, remoteKey, NonEmpty(item.Str("secretKey")), version));

        // AWS can pick JSON properties out of a secret into files of their own.
        foreach (var jmes in item.Get("jmesPath").Items())
        {
            if (NonEmpty(jmes.Str("objectAlias")) is { } alias)
            {
                result.Add(new SecretProviderObject(alias, remoteKey, NonEmpty(jmes.Str("path")), version));
            }
        }
    }

    private static List<YamlNode> TryLoad(string text)
    {
        try
        {
            return Yaml.LoadDocuments(text);
        }
        catch (YamlException)
        {
            return [];
        }
    }

    private static SecretStoreReference? ReadStore(YamlNode? node) =>
        NonEmpty(node.Str("name")) is { } storeName ? new SecretStoreReference(NonEmpty(node.Str("kind")) ?? "SecretStore", storeName) : null;

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>A layer upstream of a Secret key: the ExternalSecret or SecretProviderClass that fills it.</summary>
internal sealed record SecretOrigin(ExternalSecretRef Reference, string Source, string Location, string Note, bool IsInferred);
