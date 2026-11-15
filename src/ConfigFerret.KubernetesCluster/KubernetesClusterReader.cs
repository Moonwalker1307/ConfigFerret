using System.Net;
using System.Text.Json;
using ConfigFerret.Kubernetes;
using k8s;
using k8s.Autorest;
using k8s.Exceptions;
using k8s.KubeConfigModels;

namespace ConfigFerret.KubernetesCluster;

/// <summary>
/// Reads a workload, and the ConfigMaps, Secrets, ExternalSecrets and SecretProviderClasses behind it, from a live
/// cluster, and returns them as core <see cref="KubernetesManifests"/> so the usual Kubernetes linking applies.
/// </summary>
public static class KubernetesClusterReader
{
    // How many ConfigMaps or Secrets one workload may reference before the rest are skipped.
    private const int MaxReferenced = 200;

    /// <summary>Reads the workload named in <paramref name="options"/> and everything it uses.</summary>
    /// <exception cref="ArgumentException">An option is not valid, for example the workload.</exception>
    /// <exception cref="FileNotFoundException">The kubeconfig file in <see cref="KubernetesClusterOptions.Kubeconfig"/> does not exist.</exception>
    /// <exception cref="InvalidOperationException">No kubeconfig was found, it could not be used, or the workload could not be read.</exception>
    /// <exception cref="HttpRequestException">The API server could not be reached.</exception>
    /// <example>
    /// <code>
    /// var cluster = await KubernetesClusterReader.LoadAsync(new KubernetesClusterOptions
    /// {
    ///     Context = "prod", Namespace = "orders", Workload = "deployment/orders-api",
    /// });
    /// var report = ConfigTracer.For(configuration).WithKubernetesCluster(cluster).Trace();
    /// </code>
    /// </example>
    public static Task<KubernetesClusterSnapshot> LoadAsync(KubernetesClusterOptions options, CancellationToken cancellationToken = default) =>
        LoadAsync(options, [], Environment.GetEnvironmentVariable, cancellationToken);

    internal static async Task<KubernetesClusterSnapshot> LoadAsync(
        KubernetesClusterOptions options, DelegatingHandler[] handlers, Func<string, string?> getEnvironmentVariable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var workload = WorkloadReference.Parse(options.Workload);
        var (config, context) = BuildConfiguration(options, getEnvironmentVariable);
        using var client = new k8s.Kubernetes(config, handlers);
        var @namespace = string.IsNullOrWhiteSpace(options.Namespace) ? config.Namespace ?? "default" : options.Namespace;
        return await LoadAsync(client, @namespace, context, workload, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Works out the client configuration and the context name from the options and the environment.</summary>
    internal static (KubernetesClientConfiguration Config, string? Context) BuildConfiguration(KubernetesClusterOptions options, Func<string, string?> getEnvironmentVariable)
    {
        KubernetesClientConfiguration config;
        string? context = null;
        var files = KubeconfigFiles(options, getEnvironmentVariable);
        if (options.InCluster || (files.Count == 0 && KubernetesClientConfiguration.IsInCluster()))
        {
            try
            {
                config = KubernetesClientConfiguration.InClusterConfig();
            }
            catch (Exception ex) when (ex is KubeConfigException or IOException or UnauthorizedAccessException or FormatException)
            {
                throw new InvalidOperationException($"Could not use the pod's service account: {ex.Message}", ex);
            }
        }
        else if (files.Count == 0)
        {
            throw new InvalidOperationException("No kubeconfig found: set Kubeconfig or KUBECONFIG, create ~/.kube/config, or run in a pod.");
        }
        else
        {
            try
            {
                var kubeconfig = Merge(files.Select(f => KubernetesClientConfiguration.LoadKubeConfig(new FileInfo(f))).ToList());
                context = string.IsNullOrWhiteSpace(options.Context) ? kubeconfig.CurrentContext : options.Context;
                config = KubernetesClientConfiguration.BuildConfigFromConfigObject(kubeconfig, context);
            }
            catch (Exception ex) when (ex is KubeConfigException or YamlDotNet.Core.YamlException or FormatException or ArgumentException)
            {
                throw new InvalidOperationException($"Could not use the kubeconfig ({string.Join(", ", files.Select(Path.GetFileName))}): {ex.Message}", ex);
            }
        }

        if (!Uri.TryCreate(config.Host, UriKind.Absolute, out var host) || host.Scheme is not ("https" or "http"))
        {
            throw new InvalidOperationException("The kubeconfig's cluster has no valid server address.");
        }

        if (host.Scheme == "http" && !host.IsLoopback && !options.AllowUnencryptedHttp)
        {
            throw new InvalidOperationException($"The API server {host.Host} uses plain http, which would send the credential in clear text. Use https, or set AllowUnencryptedHttp.");
        }

        return (config, context);
    }

    // Merges KUBECONFIG files like kubectl: the first file that sets the current context wins, and the first file that
    // defines a cluster, user or context by a name wins.
    private static K8SConfiguration Merge(List<K8SConfiguration> configs)
    {
        if (configs.Count == 1)
        {
            return configs[0];
        }

        static List<T> First<T>(IEnumerable<IEnumerable<T>?> lists, Func<T, string?> name) =>
            lists.SelectMany(l => l ?? []).DistinctBy(name, StringComparer.Ordinal).ToList();

        return new K8SConfiguration
        {
            ApiVersion = configs[0].ApiVersion,
            Kind = configs[0].Kind,
            CurrentContext = configs.Select(c => c.CurrentContext).FirstOrDefault(c => !string.IsNullOrEmpty(c)),
            Clusters = First(configs.Select(c => c.Clusters), c => c.Name),
            Users = First(configs.Select(c => c.Users), u => u.Name),
            Contexts = First(configs.Select(c => c.Contexts), c => c.Name),
        };
    }

    private static List<string> KubeconfigFiles(KubernetesClusterOptions options, Func<string, string?> getEnvironmentVariable)
    {
        if (!string.IsNullOrWhiteSpace(options.Kubeconfig))
        {
            return File.Exists(options.Kubeconfig)
                ? [options.Kubeconfig]
                : throw new FileNotFoundException($"The kubeconfig file {options.Kubeconfig} does not exist.", options.Kubeconfig);
        }

        // Like kubectl, every file in KUBECONFIG that exists is merged, and the first one wins.
        var fromEnvironment = (getEnvironmentVariable("KUBECONFIG") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(File.Exists)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (fromEnvironment.Count > 0)
        {
            return fromEnvironment;
        }

        var home = getEnvironmentVariable("HOME") ?? getEnvironmentVariable("USERPROFILE");
        var defaultPath = home is null ? null : Path.Join(home, ".kube", "config");
        return defaultPath is not null && File.Exists(defaultPath) ? [defaultPath] : [];
    }

    /// <summary>Reads the workload and everything it uses through <paramref name="client"/>.</summary>
    internal static async Task<KubernetesClusterSnapshot> LoadAsync(
        IKubernetes client, string @namespace, string? context, WorkloadReference workload, KubernetesClusterOptions options, CancellationToken cancellationToken)
    {
        var manifests = new KubernetesManifests();
        var warnings = new List<string>();
        var where = $"namespace {@namespace}";

        var json = await ReadWorkloadAsync(client, @namespace, workload, cancellationToken).ConfigureAwait(false);
        try
        {
            manifests.Add(json, workload.ToString().ToLowerInvariant());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"The Kubernetes API returned {workload} in a form that could not be read.", ex);
        }

        var read = (manifests.Workloads.Count > 0 ? manifests.Workloads[0] : null)
            ?? throw new InvalidOperationException($"{workload} in {where} has no pod template.");

        // Operator objects first: a Secret an ExternalSecret writes is then known even when Secrets are not read.
        await AddCustomObjectsAsync(client, manifests, warnings, @namespace, "external-secrets.io", ["v1", "v1beta1"], "externalsecrets", "ExternalSecret", cancellationToken).ConfigureAwait(false);
        await AddCustomObjectsAsync(client, manifests, warnings, @namespace, "external-secrets.io", ["v1", "v1beta1"], "secretstores", "SecretStore", cancellationToken).ConfigureAwait(false);
        await AddCustomObjectsAsync(client, manifests, warnings, null, "external-secrets.io", ["v1", "v1beta1"], "clustersecretstores", "ClusterSecretStore", cancellationToken).ConfigureAwait(false);
        await AddCustomObjectsAsync(client, manifests, warnings, @namespace, "secrets-store.csi.x-k8s.io", ["v1", "v1alpha1"], "secretproviderclasses", "SecretProviderClass", cancellationToken).ConfigureAwait(false);

        var (configMaps, secrets) = Referenced(read);
        foreach (var name in configMaps.Take(MaxReferenced))
        {
            var configMap = await ReadAsync(
                () => client.CoreV1.ReadNamespacedConfigMapAsync(name, @namespace, cancellationToken: cancellationToken),
                $"ConfigMap {name}", where, warnings).ConfigureAwait(false);
            if (configMap is not null)
            {
                configMap.Kind = "ConfigMap";
                AddObject(manifests, KubernetesJson.Serialize(configMap), $"configmap/{name}", warnings);
            }
        }

        if (options.IncludeSecrets)
        {
            foreach (var name in secrets.Take(MaxReferenced))
            {
                var secret = await ReadAsync(
                    () => client.CoreV1.ReadNamespacedSecretAsync(name, @namespace, cancellationToken: cancellationToken),
                    $"Secret {name}", where, warnings).ConfigureAwait(false);
                if (secret is not null)
                {
                    secret.Kind = "Secret";
                    AddObject(manifests, KubernetesJson.Serialize(secret), $"secret/{name}", warnings);
                }
            }
        }

        if (configMaps.Count > MaxReferenced || secrets.Count > MaxReferenced)
        {
            warnings.Add($"{workload} references more than {MaxReferenced} ConfigMaps or Secrets; only the first {MaxReferenced} were read.");
        }

        return new KubernetesClusterSnapshot(manifests, @namespace, context, read, options.Container, options.IncludeSecrets, warnings);
    }

    private static async Task<string> ReadWorkloadAsync(IKubernetes client, string @namespace, WorkloadReference workload, CancellationToken cancellationToken)
    {
        var (name, ns, ct) = (workload.Name, @namespace, cancellationToken);
        try
        {
            IKubernetesObject? read = workload.Kind switch
            {
                "Deployment" => await client.AppsV1.ReadNamespacedDeploymentAsync(name, ns, cancellationToken: ct).ConfigureAwait(false),
                "StatefulSet" => await client.AppsV1.ReadNamespacedStatefulSetAsync(name, ns, cancellationToken: ct).ConfigureAwait(false),
                "DaemonSet" => await client.AppsV1.ReadNamespacedDaemonSetAsync(name, ns, cancellationToken: ct).ConfigureAwait(false),
                "CronJob" => await client.BatchV1.ReadNamespacedCronJobAsync(name, ns, cancellationToken: ct).ConfigureAwait(false),
                "Job" => await client.BatchV1.ReadNamespacedJobAsync(name, ns, cancellationToken: ct).ConfigureAwait(false),
                _ => await client.CoreV1.ReadNamespacedPodAsync(name, ns, cancellationToken: ct).ConfigureAwait(false),
            };
            var obj = read ?? throw new InvalidOperationException($"The Kubernetes API returned no {workload} in namespace {ns}.");

            // The core parser goes by kind, which the API leaves out of some responses. It needs no apiVersion for
            // built-in kinds.
            obj.Kind = workload.Kind;
            return KubernetesJson.Serialize(obj);
        }
        catch (HttpOperationException ex)
        {
            throw new InvalidOperationException(ex.Response.StatusCode switch
            {
                HttpStatusCode.NotFound => $"{workload} was not found in namespace {ns}.",
                HttpStatusCode.Unauthorized => $"The Kubernetes API rejected the credential (401 Unauthorized) when reading {workload}. Sign in again or check the kubeconfig.",
                HttpStatusCode.Forbidden => $"The credential cannot read {workload} in namespace {ns} (403 Forbidden). It needs get on {workload.Kind.ToLowerInvariant()}s.",
                var status => $"The Kubernetes API answered {(int)status} when reading {workload} in namespace {ns}.",
            }, ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The Kubernetes API returned {workload} in a form that could not be read.", ex);
        }
    }

    // One ConfigMap or Secret: null when it does not exist (the resolver then says it was not loaded) or cannot be read.
    private static async Task<T?> ReadAsync<T>(Func<Task<T>> read, string what, string where, List<string> warnings)
        where T : class
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            warnings.Add($"{what} does not exist in {where}.");
            return null;
        }
        catch (HttpOperationException ex)
        {
            warnings.Add(ex.Response.StatusCode == HttpStatusCode.Forbidden
                ? $"The credential cannot read {what} in {where} (403 Forbidden), so its keys are not known."
                : $"The Kubernetes API answered {(int)ex.Response.StatusCode} when reading {what} in {where}, so its keys are not known.");
            return null;
        }
        catch (JsonException)
        {
            warnings.Add($"The Kubernetes API returned {what} in a form that could not be read, so its keys are not known.");
            return null;
        }
    }

    // Lists one kind of operator object. A 404 means the operator is not installed, which is normal, so it is quiet.
    private static async Task AddCustomObjectsAsync(
        IKubernetes client, KubernetesManifests manifests, List<string> warnings, string? @namespace, string group, string[] versions,
        string plural, string kind, CancellationToken cancellationToken)
    {
        var what = kind == "SecretProviderClass" ? "SecretProviderClasses" : kind + "s";
        foreach (var version in versions)
        {
            var (list, problem) = await ListAsync(client, @namespace, group, version, plural, what, cancellationToken).ConfigureAwait(false);
            if (problem is not null)
            {
                warnings.Add(problem);
                return;
            }

            if (list is null)
            {
                // Not installed, or not this version.
                continue;
            }

            var found = list.Value;

            if (found.ValueKind != JsonValueKind.Object || !found.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in items.EnumerateArray().Take(1000))
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = item.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
                    && meta.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                AddObject(manifests, WithKind(item, $"{group}/{version}", kind), $"{kind.ToLowerInvariant()}/{name ?? "(unnamed)"}", warnings);
            }

            return;
        }
    }

    // One list request: the list, nothing when the kind or version is not served (404), or a warning.
    private static async Task<(JsonElement? List, string? Problem)> ListAsync(
        IKubernetes client, string? @namespace, string group, string version, string plural, string what, CancellationToken cancellationToken)
    {
        var scope = @namespace is null ? "the cluster" : $"namespace {@namespace}";
        try
        {
            var list = @namespace is null
                ? await client.CustomObjects.ListClusterCustomObjectAsync<JsonElement>(group, version, plural, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await client.CustomObjects.ListNamespacedCustomObjectAsync<JsonElement>(group, version, @namespace, plural, cancellationToken: cancellationToken).ConfigureAwait(false);
            return (list, null);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, null);
        }
        catch (HttpOperationException ex)
        {
            return (null, ex.Response.StatusCode == HttpStatusCode.Forbidden
                ? $"The credential cannot list {what} in {scope} (403 Forbidden), so the Secrets they fill are not linked to their stores."
                : $"The Kubernetes API answered {(int)ex.Response.StatusCode} when listing {what} in {scope}.");
        }
        catch (JsonException)
        {
            return (null, $"The Kubernetes API returned {what} in a form that could not be read.");
        }
    }

    // List items usually carry apiVersion and kind, but nothing in the API promises it, and the core parser needs both.
    // The item is copied as it is, duplicate keys and all, so the parser judges it the same way it judges a file.
    private static string WithKind(JsonElement item, string apiVersion, string kind)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (!item.TryGetProperty("apiVersion", out _))
            {
                writer.WriteString("apiVersion", apiVersion);
            }

            if (!item.TryGetProperty("kind", out _))
            {
                writer.WriteString("kind", kind);
            }

            foreach (var property in item.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void AddObject(KubernetesManifests manifests, string json, string sourceName, List<string> warnings)
    {
        try
        {
            manifests.Add(json, sourceName);
        }
        catch (FormatException)
        {
            warnings.Add($"{sourceName} could not be read.");
        }
    }

    // The ConfigMaps and Secrets a workload's containers and volumes name.
    private static (List<string> ConfigMaps, List<string> Secrets) Referenced(Workload workload)
    {
        var configMaps = new List<string>();
        var secrets = new List<string>();
        foreach (var container in workload.Containers)
        {
            foreach (var from in container.EnvFrom)
            {
                (from.IsSecret ? secrets : configMaps).Add(from.Name);
            }

            foreach (var env in container.Env)
            {
                switch (env.Source)
                {
                    case EnvValueSource.FromConfigMap cm:
                        configMaps.Add(cm.Name);
                        break;
                    case EnvValueSource.FromSecret s:
                        secrets.Add(s.Name);
                        break;
                }
            }
        }

        foreach (var projection in workload.Volumes.SelectMany(v => v.Projections))
        {
            (projection.IsSecret ? secrets : configMaps).Add(projection.SourceName);
        }

        return (Clean(configMaps), Clean(secrets));

        static List<string> Clean(List<string> names) => names.Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    }
}
