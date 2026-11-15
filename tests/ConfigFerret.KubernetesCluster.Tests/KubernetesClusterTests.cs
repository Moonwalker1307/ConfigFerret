using System.Net;
using ConfigFerret.Kubernetes;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.KubernetesCluster.Tests;

public class KubernetesClusterTests
{
    internal const string Token = "kubeconfig-token-5c1d";
    internal const string SecretValue = "s3cr3t-db-password";

    internal static readonly string Kubeconfig = Path.Join(AppContext.BaseDirectory, "TestData", "kubeconfig.yaml");

    internal const string Deployment = """
        {
          "apiVersion": "apps/v1",
          "kind": "Deployment",
          "metadata": { "name": "api", "namespace": "orders" },
          "spec": {
            "template": {
              "spec": {
                "containers": [
                  {
                    "name": "api",
                    "envFrom": [ { "configMapRef": { "name": "api-config" } }, { "secretRef": { "name": "env-secret" } } ],
                    "env": [
                      { "name": "Db__Password", "valueFrom": { "secretKeyRef": { "name": "api-secrets", "key": "password" } } },
                      { "name": "Stripe__ApiKey", "valueFrom": { "secretKeyRef": { "name": "stripe", "key": "api-key" } } },
                      { "name": "Feature", "valueFrom": { "configMapKeyRef": { "name": "missing", "key": "f" } } },
                      { "name": "Nameless", "valueFrom": { "configMapKeyRef": { "name": "", "key": "f" } } }
                    ],
                    "volumeMounts": [ { "name": "cfg", "mountPath": "/etc/cfg" } ]
                  }
                ],
                "volumes": [
                  { "name": "cfg", "configMap": { "name": "api-config" } },
                  { "name": "tls", "projected": { "sources": [ { "secret": { "name": "tls-secret" } } ] } }
                ]
              }
            }
          }
        }
        """;

    internal const string ConfigMap = """
        { "apiVersion": "v1", "kind": "ConfigMap", "metadata": { "name": "api-config" }, "data": { "Db__Timeout": "30", "Unused": "x" } }
        """;

    // stringData is never returned by the API; data is base64 of SecretValue.
    internal const string Secret = """
        { "apiVersion": "v1", "kind": "Secret", "metadata": { "name": "api-secrets" }, "data": { "password": "czNjcjN0LWRiLXBhc3N3b3Jk" } }
        """;

    internal const string ExternalSecrets = """
        {
          "apiVersion": "external-secrets.io/v1",
          "kind": "ExternalSecretList",
          "items": [
            {
              "apiVersion": "external-secrets.io/v1",
              "kind": "ExternalSecret",
              "metadata": { "name": "stripe" },
              "spec": {
                "secretStoreRef": { "kind": "ClusterSecretStore", "name": "aws" },
                "data": [ { "secretKey": "api-key", "remoteRef": { "key": "prod/stripe", "property": "key" } } ]
              }
            },
            {
              "metadata": { "name": "orphan" },
              "spec": { "data": [ { "secretKey": "k", "remoteRef": { "key": "r" } } ] }
            },
            "not an object"
          ]
        }
        """;

    internal const string ClusterSecretStores = """
        { "apiVersion": "external-secrets.io/v1", "kind": "ClusterSecretStoreList", "items": [ { "metadata": { "name": "aws" }, "spec": { "provider": { "aws": { "service": "SecretsManager" } } } } ] }
        """;

    internal static FakeApiServer Server() => new FakeApiServer()
        .Respond("/apis/apps/v1/namespaces/orders/deployments/api", Deployment)
        .Respond("/api/v1/namespaces/orders/configmaps/api-config", ConfigMap)
        .Respond("/api/v1/namespaces/orders/secrets/api-secrets", Secret)
        .Respond("/apis/external-secrets.io/v1/namespaces/orders/externalsecrets", ExternalSecrets)
        .Respond("/apis/external-secrets.io/v1/clustersecretstores", ClusterSecretStores);

    internal static KubernetesClusterOptions Options(string workload = "deployment/api", bool includeSecrets = false) => new()
    {
        Kubeconfig = Kubeconfig,
        Workload = workload,
        IncludeSecrets = includeSecrets,
    };

    internal static Task<KubernetesClusterSnapshot> Load(KubernetesClusterOptions options, FakeApiServer server, Func<string, string?>? env = null) =>
        KubernetesClusterReader.LoadAsync(options, [server], env ?? (_ => null), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Reads_the_workload_its_ConfigMaps_and_operator_objects_but_not_Secrets_by_default()
    {
        var server = Server();
        var cluster = await Load(Options(), server);

        Assert.Equal("orders", cluster.Namespace);
        Assert.Equal("prod", cluster.Context);
        Assert.Equal("Deployment/api", cluster.Workload.DisplayName);
        Assert.False(cluster.IncludesSecrets);
        Assert.Equal("Kubernetes Deployment/api (prod, orders)", cluster.DisplayName);
        Assert.Equal(["api-config"], cluster.Manifests.ConfigMaps.Keys);
        Assert.Empty(cluster.Manifests.Secrets);
        Assert.Equal(["stripe", "orphan"], cluster.Manifests.ExternalSecrets.Select(e => e.Name));
        Assert.Equal("aws", Assert.Single(cluster.Manifests.SecretStores).Provider);
        Assert.DoesNotContain(server.Paths, p => p.Contains("/secrets/", StringComparison.Ordinal));
        Assert.All(server.Authorizations, a => Assert.Equal("Bearer " + Token, a));
        Assert.All(server.Hosts, h => Assert.Equal("fake-cluster.example", h));
        Assert.Equal(
            [
                "/apis/apps/v1/namespaces/orders/deployments/api",
                "/apis/external-secrets.io/v1/namespaces/orders/externalsecrets",
                "/apis/external-secrets.io/v1/namespaces/orders/secretstores",
                "/apis/external-secrets.io/v1beta1/namespaces/orders/secretstores",
                "/apis/external-secrets.io/v1/clustersecretstores",
                "/apis/secrets-store.csi.x-k8s.io/v1/namespaces/orders/secretproviderclasses",
                "/apis/secrets-store.csi.x-k8s.io/v1alpha1/namespaces/orders/secretproviderclasses",
                "/api/v1/namespaces/orders/configmaps/api-config",
                "/api/v1/namespaces/orders/configmaps/missing",
            ],
            server.Paths);
        Assert.Equal(["ConfigMap missing does not exist in namespace orders."], cluster.Warnings);
    }

    [Fact]
    public async Task Secrets_without_IncludeSecrets_are_sensitive_with_unknown_values()
    {
        var cluster = await Load(Options(), Server());
        var report = ConfigTracer.ForContainer(cluster.Manifests, cluster.Workload.Name).Trace();

        var password = report.Find("Db__Password")!;
        Assert.Null(password.Value);
        Assert.True(password.EffectiveStep!.IsSensitive);
        Assert.Equal(new SecretKeyRef("api-secrets", "password"), password.EffectiveStep.Reference);

        // The ExternalSecret's target is known without reading Secrets, and links to its store.
        var stripe = report.Find("Stripe__ApiKey")!.EffectiveStep!;
        Assert.Equal(new ExternalSecretRef("aws", "aws", "prod/stripe", "key"), Assert.Single(stripe.Origins).Reference);
        Assert.Equal("30", report.Find("Db__Timeout")!.Value);
    }

    [Fact]
    public async Task IncludeSecrets_reads_values_that_stay_redacted_unless_revealed()
    {
        var server = Server();
        var cluster = await Load(Options(includeSecrets: true), server);

        Assert.Contains("/api/v1/namespaces/orders/secrets/api-secrets", server.Paths);
        Assert.Equal(
            ["env-secret", "api-secrets", "stripe", "tls-secret"],
            server.Paths.Where(p => p.Contains("/secrets/", StringComparison.Ordinal)).Select(p => p.Split('/')[^1]));
        Assert.Equal("secret/api-secrets", cluster.Manifests.Secrets["api-secrets"].SourceName);
        Assert.Equal("configmap/api-config", cluster.Manifests.ConfigMaps["api-config"].SourceName);
        Assert.Equal("deployment/api", cluster.Workload.SourceName);
        Assert.Contains("Secret stripe does not exist in namespace orders.", cluster.Warnings);
        Assert.True(cluster.IncludesSecrets);

        var report = ConfigTracer.ForContainer(cluster.Manifests).Trace();
        Assert.DoesNotContain(SecretValue, report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain(SecretValue, report.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain(SecretValue, string.Join("\n", cluster.Warnings), StringComparison.Ordinal);
        Assert.DoesNotContain(SecretValue, string.Join("\n", cluster.Entries.Select(e => e + e.Detail)), StringComparison.Ordinal);
        Assert.Equal("***", report.Find("Db__Password")!.Value);

        var revealed = ConfigTracer.ForContainer(cluster.Manifests).WithOptions(o => o.RevealSensitiveValues = true).Trace();
        Assert.Equal(SecretValue, revealed.Find("Db__Password")!.Value);
    }

    [Fact]
    public async Task Entries_list_every_key_with_location_and_FindUnused_finds_the_unread_ones()
    {
        var cluster = await Load(Options(includeSecrets: true), Server());

        Assert.Equal(
            [
                "Db__Timeout|ConfigMap api-config|False",
                "Unused|ConfigMap api-config|False",
                "password|Secret api-secrets|True",
                "api-key|Secret stripe, written by ExternalSecret stripe|True",
                "k|Secret orphan, written by ExternalSecret orphan|True",
            ],
            cluster.Entries.Select(e => $"{e.Name}|{e.Detail}|{e.IsSensitive}"));
        Assert.All(cluster.Entries, e => Assert.Equal("prod/orders", e.Location));

        var loaded = cluster.ToLoadedSource();
        Assert.Equal(cluster.DisplayName, loaded.DisplayName);
        // The container uses every key but the one the orphan ExternalSecret writes.
        var report = ConfigTracer.ForContainer(cluster.Manifests).Trace();
        Assert.Equal(["k"], loaded.FindUnused(report).Select(e => e.Name));
    }

    [Fact]
    public async Task WithKubernetesCluster_links_the_apps_configuration()
    {
        var cluster = await Load(Options(), Server());
        var env = new Dictionary<string, string?> { ["Db__Timeout"] = "30" };
        var report = ConfigTracer.For(new ConfigurationBuilder().AddInMemoryCollection([new("Db:Timeout", "30")]).Build())
            .WithOptions(o => o.Environment = env)
            .WithKubernetesCluster(cluster)
            .Trace();

        Assert.NotNull(report.Find("Db:Timeout"));
        Assert.Equal(["ConfigMap missing does not exist in namespace orders."], report.Warnings);
        Assert.Throws<ArgumentNullException>(() => ConfigTracer.For(new ConfigurationBuilder().Build()).WithKubernetesCluster(null!));
        Assert.Throws<ArgumentNullException>(() => KubernetesClusterTracerExtensions.WithKubernetesCluster(null!, cluster));
    }

    [Theory]
    [InlineData("statefulset/api", "/apis/apps/v1/namespaces/orders/statefulsets/api", "StatefulSet")]
    [InlineData("ds/api", "/apis/apps/v1/namespaces/orders/daemonsets/api", "DaemonSet")]
    [InlineData("cronjob/api", "/apis/batch/v1/namespaces/orders/cronjobs/api", "CronJob")]
    [InlineData("jobs/api", "/apis/batch/v1/namespaces/orders/jobs/api", "Job")]
    [InlineData("api", "/api/v1/namespaces/orders/pods/api", "Pod")]
    [InlineData("deployment.apps/api", "/apis/apps/v1/namespaces/orders/deployments/api", "Deployment")]
    public async Task Reads_each_workload_kind(string workload, string path, string kind)
    {
        // The API leaves kind and apiVersion out here; the loader fills them in.
        var spec = """{"containers":[{"name":"c","env":[{"name":"A","value":"1"}]}]}""";
        var body = kind switch
        {
            "Pod" => """{"metadata":{"name":"api"},"spec":""" + spec + "}",
            "CronJob" => """{"metadata":{"name":"api"},"spec":{"schedule":"* * * * *","jobTemplate":{"spec":{"template":{"spec":""" + spec + "}}}}}",
            "StatefulSet" or "DaemonSet" or "Deployment" => """{"metadata":{"name":"api"},"spec":{"selector":{},"template":{"spec":""" + spec + "}}}",
            _ => """{"metadata":{"name":"api"},"spec":{"template":{"spec":""" + spec + "}}}",
        };
        var server = new FakeApiServer().Respond(path, body);
        var cluster = await Load(Options(workload), server);

        Assert.Equal(kind, cluster.Workload.Kind);
        Assert.Equal("1", ConfigTracer.ForContainer(cluster.Manifests).Trace().Find("A")!.Value);
        Assert.Equal(path, server.Paths[0]);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "Deployment/api was not found in namespace orders.")]
    [InlineData(HttpStatusCode.Forbidden, "The credential cannot read Deployment/api in namespace orders (403 Forbidden). It needs get on deployments.")]
    [InlineData(HttpStatusCode.Unauthorized, "The Kubernetes API rejected the credential (401 Unauthorized) when reading Deployment/api. Sign in again or check the kubeconfig.")]
    [InlineData(HttpStatusCode.InternalServerError, "The Kubernetes API answered 500 when reading Deployment/api in namespace orders.")]
    public async Task Workload_errors_say_what_failed_without_the_credential(HttpStatusCode status, string message)
    {
        var server = new FakeApiServer().Fail("/apis/apps/v1/namespaces/orders/deployments/api", status);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Load(Options(), server));

        Assert.Equal(message, ex.Message);
        Assert.DoesNotContain(Token, ex.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    public async Task Unreadable_workload_json_is_an_InvalidOperationException(string body)
    {
        var server = new FakeApiServer().Respond("/apis/apps/v1/namespaces/orders/deployments/api", body);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Load(Options(), server));

        Assert.Equal("The Kubernetes API returned Deployment/api in a form that could not be read.", ex.Message);
    }

    [Fact]
    public async Task A_workload_without_a_pod_template_is_an_error()
    {
        var server = new FakeApiServer().Respond("/apis/apps/v1/namespaces/orders/deployments/api", """{"metadata":{"name":"api"},"spec":{}}""");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Load(Options(), server));

        Assert.Equal("Deployment/api in namespace orders has no pod template.", ex.Message);
    }

    [Fact]
    public async Task Object_errors_become_warnings()
    {
        var server = Server()
            .Fail("/api/v1/namespaces/orders/configmaps/api-config", HttpStatusCode.Forbidden)
            .Fail("/api/v1/namespaces/orders/configmaps/missing", HttpStatusCode.InternalServerError)
            .Respond("/api/v1/namespaces/orders/secrets/api-secrets", "{bad")
            .Fail("/apis/external-secrets.io/v1/namespaces/orders/externalsecrets", HttpStatusCode.Forbidden)
            .Fail("/apis/external-secrets.io/v1/clustersecretstores", HttpStatusCode.ServiceUnavailable)
            .Respond("/apis/secrets-store.csi.x-k8s.io/v1/namespaces/orders/secretproviderclasses", "{bad");
        var cluster = await Load(Options(includeSecrets: true), server);

        Assert.Equal(
            [
                "The credential cannot list ExternalSecrets in namespace orders (403 Forbidden), so the Secrets they fill are not linked to their stores.",
                "The Kubernetes API answered 503 when listing ClusterSecretStores in the cluster.",
                "The Kubernetes API returned SecretProviderClasses in a form that could not be read.",
                "The credential cannot read ConfigMap api-config in namespace orders (403 Forbidden), so its keys are not known.",
                "The Kubernetes API answered 500 when reading ConfigMap missing in namespace orders, so its keys are not known.",
                "Secret env-secret does not exist in namespace orders.",
                "The Kubernetes API returned Secret api-secrets in a form that could not be read, so its keys are not known.",
                "Secret stripe does not exist in namespace orders.",
                "Secret tls-secret does not exist in namespace orders.",
            ],
            cluster.Warnings);
        Assert.DoesNotContain(server.Paths, p => p.Contains("v1beta1/namespaces/orders/externalsecrets", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Falls_back_to_older_operator_api_versions_and_fills_in_missing_kinds()
    {
        var server = new FakeApiServer()
            .Respond("/apis/apps/v1/namespaces/orders/deployments/api", Deployment)
            .Respond("/apis/external-secrets.io/v1beta1/namespaces/orders/externalsecrets", """
                {"items":[{"metadata":{"name":"stripe"},"spec":{"secretStoreRef":{"name":"kv"},"data":[{"secretKey":"api-key","remoteRef":{"key":"stripe"}}]}},{"metadata":{},"spec":{}},{"metadata":{"name":7}}]}
                """)
            .Respond("/apis/external-secrets.io/v1beta1/namespaces/orders/secretstores", """
                {"items":[{"metadata":{"name":"kv"},"spec":{"provider":{"azurekv":{}}}}]}
                """)
            .Respond("/apis/secrets-store.csi.x-k8s.io/v1alpha1/namespaces/orders/secretproviderclasses", """
                {"items":[{"metadata":{"name":"spc"},"spec":{"provider":"azure","secretObjects":[{"secretName":"synced","data":[{"objectName":"o","key":"K"}]}]}}]}
                """)
            .Respond("/apis/external-secrets.io/v1/clustersecretstores", """{"items":"nope"}""");
        var cluster = await Load(Options(), server);

        Assert.Equal(["stripe", "(unnamed)", "7"], cluster.Manifests.ExternalSecrets.Select(e => e.Name));
        Assert.Equal("azurekv", Assert.Single(cluster.Manifests.SecretStores).Provider);
        Assert.Equal("azurekv", cluster.Manifests.SecretProviderClasses["spc"].Provider);
        Assert.Contains(cluster.Entries, e => e.Detail == "Secret synced, synced by SecretProviderClass spc" && e.IsSensitive);
        Assert.DoesNotContain(cluster.Warnings, w => !w.StartsWith("ConfigMap", StringComparison.Ordinal));
    }

    // Found by fuzzing: a list item with a key twice made System.Text.Json.Nodes throw ArgumentException.
    [Fact]
    public async Task A_list_item_with_duplicate_keys_becomes_a_warning()
    {
        var server = new FakeApiServer()
            .Respond("/apis/apps/v1/namespaces/orders/deployments/api", Deployment)
            .Respond("/apis/external-secrets.io/v1/namespaces/orders/externalsecrets", """
                {"items":[{"metadata":{"name":"dup"},"spec":{},"spec":{}},{"metadata":{"name":"ok"},"spec":{"data":[{"secretKey":"a","remoteRef":{"key":"b"}}]}}]}
                """);
        var cluster = await Load(Options(), server);

        Assert.Contains("externalsecret/dup could not be read.", cluster.Warnings);
        Assert.Equal(["ok"], cluster.Manifests.ExternalSecrets.Select(e => e.Name));
    }

    [Fact]
    public async Task Many_referenced_objects_are_capped_with_a_warning()
    {
        var env = string.Join(",", Enumerable.Range(0, 205).Select(i => "{\"name\":\"V" + i + "\",\"valueFrom\":{\"configMapKeyRef\":{\"name\":\"cm" + i + "\",\"key\":\"k\"}}}"));
        var server = new FakeApiServer().Respond(
            "/apis/apps/v1/namespaces/orders/deployments/api",
            """{"metadata":{"name":"api"},"spec":{"template":{"spec":{"containers":[{"name":"c","env":[""" + env + "]}]}}}}");
        var cluster = await Load(Options(), server);

        Assert.Equal(200, server.Paths.Count(p => p.Contains("/configmaps/", StringComparison.Ordinal)));
        Assert.Contains("Deployment/api references more than 200 ConfigMaps or Secrets; only the first 200 were read.", cluster.Warnings);
    }

    [Theory]
    [InlineData("configMapKeyRef")]
    [InlineData("secretKeyRef")]
    public async Task Exactly_200_referenced_objects_need_no_warning(string reference)
    {
        var env = string.Join(",", Enumerable.Range(0, 200).Select(i => "{\"name\":\"V" + i + "\",\"valueFrom\":{\"" + reference + "\":{\"name\":\"o" + i + "\",\"key\":\"k\"}}}"));
        var server = new FakeApiServer().Respond(
            "/apis/apps/v1/namespaces/orders/deployments/api",
            """{"metadata":{"name":"api"},"spec":{"template":{"spec":{"containers":[{"name":"c","env":[""" + env + "]}]}}}}");
        var cluster = await Load(Options(includeSecrets: true), server);

        Assert.DoesNotContain(cluster.Warnings, w => w.Contains("more than 200", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Namespace_option_wins_over_the_context()
    {
        var server = new FakeApiServer().Respond("/api/v1/namespaces/other/pods/p", """{"metadata":{"name":"p"},"spec":{"containers":[{"name":"c"}]}}""");
        var options = Options("pod/p");
        options.Namespace = "other";
        var cluster = await Load(options, server);

        Assert.Equal("other", cluster.Namespace);
    }

    [Fact]
    public async Task A_context_without_a_namespace_uses_default()
    {
        var server = new FakeApiServer().Respond("/api/v1/namespaces/default/pods/p", """{"metadata":{"name":"p"},"spec":{"containers":[{"name":"c"}]}}""");
        var options = Options("p");
        options.Context = "no-namespace";
        var cluster = await Load(options, server);

        Assert.Equal("default", cluster.Namespace);
        Assert.Equal("no-namespace", cluster.Context);
    }

    [Fact]
    public async Task LoadAsync_rejects_bad_options()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => KubernetesClusterReader.LoadAsync(null!, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => KubernetesClusterReader.LoadAsync(new KubernetesClusterOptions(), TestContext.Current.CancellationToken));
    }
}
