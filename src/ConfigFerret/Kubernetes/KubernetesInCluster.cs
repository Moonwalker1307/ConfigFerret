using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using ConfigFerret.Internal;

namespace ConfigFerret.Kubernetes;

/// <summary>
/// Reads the pod this process runs in, and the ConfigMaps it references, from the Kubernetes API using the pod's
/// service account. The service account needs <c>get</c> on <c>pods</c>, and on <c>configmaps</c> to show values.
/// Secrets are never read.
/// </summary>
public static class KubernetesInCluster
{
    private const string ServiceAccountPath = "/var/run/secrets/kubernetes.io/serviceaccount";

    /// <summary>True when the process seems to run inside a Kubernetes pod.</summary>
    public static bool IsInCluster =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST"))
        && File.Exists(Path.Combine(ServiceAccountPath, "token"));

    /// <summary>Reads the current pod and the ConfigMaps it references.</summary>
    /// <param name="podName">The pod name. Defaults to <c>POD_NAME</c>, then <c>HOSTNAME</c>.</param>
    /// <param name="namespace">The namespace. Defaults to <c>POD_NAMESPACE</c>, then the service account's namespace.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public static async Task<KubernetesManifests> LoadCurrentPodAsync(string? podName = null, string? @namespace = null, CancellationToken cancellationToken = default)
    {
        var target = await ResolveTargetAsync(ServiceAccountPath, Environment.GetEnvironmentVariable, podName, @namespace, cancellationToken).ConfigureAwait(false);

        var caPath = Path.Combine(ServiceAccountPath, "ca.crt");
#pragma warning disable CA2000 // The handler is owned and disposed by the HttpClient.
        var handler = new HttpClientHandler();
#pragma warning restore CA2000
        if (File.Exists(caPath))
        {
            var ca = X509CertificateLoader.LoadCertificateFromFile(caPath);
            handler.ServerCertificateCustomValidationCallback = (_, cert, chain, errors) =>
            {
                if (errors == System.Net.Security.SslPolicyErrors.None)
                {
                    return true;
                }

                if (cert is null || chain is null || (errors & ~System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors) != 0)
                {
                    return false;
                }

                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(ca);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(cert);
            };
        }

        using var client = new HttpClient(handler) { BaseAddress = target.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", target.Token);
        return await LoadPodAsync(client, target.Namespace, target.PodName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Works out the API address, token, namespace and pod name from the environment and service account.</summary>
    internal static async Task<InClusterTarget> ResolveTargetAsync(
        string serviceAccountPath, Func<string, string?> getEnvironmentVariable, string? podName, string? @namespace, CancellationToken cancellationToken)
    {
        var host = getEnvironmentVariable("KUBERNETES_SERVICE_HOST")
            ?? throw new InvalidOperationException("KUBERNETES_SERVICE_HOST is not set; this process does not seem to run in a pod.");
        var port = getEnvironmentVariable("KUBERNETES_SERVICE_PORT") ?? "443";
        var token = (await File.ReadAllTextAsync(Path.Combine(serviceAccountPath, "token"), cancellationToken).ConfigureAwait(false)).Trim();
        @namespace ??= getEnvironmentVariable("POD_NAMESPACE")
            ?? (await File.ReadAllTextAsync(Path.Combine(serviceAccountPath, "namespace"), cancellationToken).ConfigureAwait(false)).Trim();
        podName ??= getEnvironmentVariable("POD_NAME") ?? getEnvironmentVariable("HOSTNAME") ?? Environment.MachineName;

        var hostPart = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        return new InClusterTarget(new Uri($"https://{hostPart}:{port}/"), token, @namespace, podName);
    }

    /// <summary>
    /// Reads a pod and the ConfigMaps it references through <paramref name="client"/>, whose base address and
    /// authentication point at a Kubernetes API server. ConfigMaps that cannot be read are skipped.
    /// </summary>
    public static async Task<KubernetesManifests> LoadPodAsync(HttpClient client, string @namespace, string podName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var podJson = await client.GetStringAsync(
            new Uri($"api/v1/namespaces/{Uri.EscapeDataString(@namespace)}/pods/{Uri.EscapeDataString(podName)}", UriKind.Relative),
            cancellationToken).ConfigureAwait(false);
        var manifests = KubernetesManifests.Parse(podJson, $"pod {podName}");

        var configMaps = manifests.Workloads
            .SelectMany(w => w.Containers.SelectMany(c =>
                c.EnvFrom.Where(e => !e.IsSecret).Select(e => e.Name)
                    .Concat(c.Env.Select(e => e.Source).OfType<EnvValueSource.FromConfigMap>().Select(e => e.Name)))
                .Concat(w.Volumes.SelectMany(v => v.Projections).Where(p => !p.IsSecret).Select(p => p.SourceName)))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var name in configMaps)
        {
            using var response = await client.GetAsync(
                new Uri($"api/v1/namespaces/{Uri.EscapeDataString(@namespace)}/configmaps/{Uri.EscapeDataString(name)}", UriKind.Relative),
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            foreach (var doc in Yaml.LoadDocuments(json))
            {
                manifests.AddObject(doc, $"configmap {name}");
            }
        }

        return manifests;
    }
}

/// <summary>Where and as whom <see cref="KubernetesInCluster"/> reads the current pod.</summary>
internal sealed record InClusterTarget(Uri BaseAddress, string Token, string Namespace, string PodName);
