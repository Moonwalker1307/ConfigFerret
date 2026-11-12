using System.Net;
using ConfigFerret.Kubernetes;

namespace ConfigFerret.Tests;

public class KubernetesTests
{
    private static ProvenanceStep FileStep(string path) => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = path,
        Reference = new FileRef(path),
    };

    private static ProvenanceStep EnvStep(string name) => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = "environment variable " + name,
        Reference = new EnvironmentVariableRef(name),
    };

    [Fact]
    public void Links_a_mounted_file_to_its_configmap()
    {
        var resolver = new KubernetesResolver(KubernetesManifests.LoadFile(TestPaths.Data("rendered.yaml")), "api", "api");

        var step = Assert.Single(resolver.Resolve(FileStep("/app/appsettings.Production.json"), new TraceDiagnostics()));

        Assert.Equal("ConfigMap api-files", step.Source);
        Assert.Equal("key appsettings.Production.json", step.Location);
        Assert.Equal(new ConfigMapKeyRef("api-files", "appsettings.Production.json"), step.Reference);
    }

    [Fact]
    public void Env_entries_override_envFrom_and_later_envFrom_overrides_earlier()
    {
        var manifests = KubernetesManifests.Parse("""
            apiVersion: v1
            kind: ConfigMap
            metadata: { name: one }
            data: { APP_X: "1" }
            ---
            apiVersion: v1
            kind: ConfigMap
            metadata: { name: two }
            data: { X: "2" }
            ---
            apiVersion: v1
            kind: Pod
            metadata: { name: p }
            spec:
              containers:
                - name: c
                  envFrom:
                    - configMapRef: { name: one }
                    - configMapRef: { name: two }
                      prefix: APP_
                    - configMapRef: { name: missing }
                  env:
                    - name: APP_X
                      value: "3"
                    - name: POD_IP
                      valueFrom:
                        fieldRef: { fieldPath: status.podIP }
            """);
        var resolver = new KubernetesResolver(manifests, null, null);

        var chain = resolver.Resolve(EnvStep("APP_X"), new TraceDiagnostics());
        var field = Assert.Single(resolver.Resolve(EnvStep("POD_IP"), new TraceDiagnostics()));
        var guessed = Assert.Single(resolver.Resolve(EnvStep("ONLY_IN_MISSING"), new TraceDiagnostics()));

        Assert.Equal(["ConfigMap one", "ConfigMap two", "Pod/p env APP_X"], chain.Select(s => s.Source));
        Assert.Equal(["1", "2", "3"], chain.Select(s => s.Value));
        Assert.Contains("with prefix 'APP_'", chain[1].Note, StringComparison.Ordinal);
        Assert.Equal("downward API status.podIP", field.Source);
        Assert.True(guessed.IsInferred);
        Assert.Equal("ConfigMap missing", guessed.Source);
    }

    [Fact]
    public void Reads_cronjobs_lists_and_projected_volumes()
    {
        var manifests = KubernetesManifests.Parse("""
            {"kind":"List","items":[
              {"kind":"CronJob","metadata":{"name":"nightly"},"spec":{"jobTemplate":{"spec":{"template":{"spec":{
                "containers":[{"name":"job","volumeMounts":[{"name":"cfg","mountPath":"/etc/cfg"}]}],
                "volumes":[{"name":"cfg","projected":{"sources":[
                  {"secret":{"name":"creds","items":[{"key":"pw","path":"Db__Password"}]}}]}}]}}}}}}
            ]}
            """);
        var resolver = new KubernetesResolver(manifests, null, null);

        var step = Assert.Single(resolver.Resolve(FileStep("/etc/cfg/Db__Password"), new TraceDiagnostics()));

        Assert.Equal("CronJob/nightly", manifests.Workloads[0].DisplayName);
        Assert.Equal("Secret creds", step.Source);
        Assert.Equal("key pw", step.Location);
        Assert.True(step.IsSensitive);
    }

    [Fact]
    public async Task Loads_a_pod_and_its_configmaps_from_the_api()
    {
        using var handler = new FakeApi();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://k8s.test/") };

        var manifests = await KubernetesInCluster.LoadPodAsync(client, "prod", "api-7d9f");

        Assert.Equal("Pod/api-7d9f", manifests.Workloads[0].DisplayName);
        Assert.Equal("42", manifests.ConfigMaps["api-config"].Data["Db__Timeout"].Value);
        Assert.DoesNotContain(handler.Requests, r => r.Contains("secrets", StringComparison.Ordinal));
        Assert.Contains("/api/v1/namespaces/prod/configmaps/forbidden", handler.Requests);
    }

    [Fact]
    public async Task Fetches_each_configmap_the_pod_references_once_and_never_secrets()
    {
        using var handler = new FakeApi();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://k8s.test/") };

        var manifests = await KubernetesInCluster.LoadPodAsync(client, "prod", "worker");

        Assert.Equal("pod worker", manifests.Workloads[0].SourceName);
        Assert.Equal("configmap api-config", manifests.ConfigMaps["api-config"].SourceName);
        Assert.Equal(
            [
                "/api/v1/namespaces/prod/pods/worker",
                "/api/v1/namespaces/prod/configmaps/api-config",
                "/api/v1/namespaces/prod/configmaps/env-key",
                "/api/v1/namespaces/prod/configmaps/projected",
            ],
            handler.Requests);
    }

    [Fact]
    public async Task LoadPodAsync_rejects_a_null_client() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() => KubernetesInCluster.LoadPodAsync(null!, "ns", "pod"));

    [Fact]
    public async Task LoadPodAsync_rejects_a_null_namespace_or_pod_name()
    {
        using var client = new HttpClient();
        await Assert.ThrowsAsync<ArgumentNullException>(() => KubernetesInCluster.LoadPodAsync(client, null!, "pod"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => KubernetesInCluster.LoadPodAsync(client, "ns", null!));
    }

    private sealed class FakeApi : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            var body = path switch
            {
                "/api/v1/namespaces/prod/pods/api-7d9f" => """
                    {"kind":"Pod","metadata":{"name":"api-7d9f"},"spec":{"containers":[{"name":"api",
                      "envFrom":[{"configMapRef":{"name":"api-config"}},{"configMapRef":{"name":"forbidden"}},{"secretRef":{"name":"s"}}]}]}}
                    """,
                "/api/v1/namespaces/prod/pods/worker" => """
                    {"kind":"Pod","metadata":{"name":"worker"},"spec":{
                      "containers":[{"name":"w",
                        "envFrom":[{"configMapRef":{"name":"api-config"}},{"secretRef":{"name":"s"}}],
                        "env":[
                          {"name":"A","valueFrom":{"configMapKeyRef":{"name":"env-key","key":"a"}}},
                          {"name":"B","valueFrom":{"configMapKeyRef":{"name":"api-config","key":"b"}}},
                          {"name":"C","valueFrom":{"secretKeyRef":{"name":"env-secret","key":"c"}}}]}],
                      "volumes":[{"name":"v","projected":{"sources":[
                        {"configMap":{"name":"projected"}},{"secret":{"name":"projected-secret"}}]}}]}}
                    """,
                "/api/v1/namespaces/prod/configmaps/api-config" => """{"kind":"ConfigMap","metadata":{"name":"api-config"},"data":{"Db__Timeout":"42"}}""",
                _ => null,
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
