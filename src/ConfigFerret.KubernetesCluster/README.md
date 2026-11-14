# ConfigFerret.KubernetesCluster

Reads a workload from a live Kubernetes cluster for [ConfigFerret](https://github.com/Moonwalker1307/ConfigFerret)
traces: the Deployment, StatefulSet, DaemonSet, CronJob, Job or Pod, the ConfigMaps it uses, its Secrets when you opt
in, and the External Secrets Operator and Secrets Store CSI objects in its namespace. It returns them as the core
`KubernetesManifests`, so the usual Kubernetes linking applies: a value read from an environment variable or a mounted
file is traced to the env entry, ConfigMap key, Secret key, `ExternalSecret` or `SecretProviderClass` behind it.

```
dotnet add package ConfigFerret.KubernetesCluster
```

```csharp
using ConfigFerret;
using ConfigFerret.KubernetesCluster;

var cluster = await KubernetesClusterReader.LoadAsync(new KubernetesClusterOptions
{
    Context = "prod",                    // optional: the kubeconfig's current context
    Namespace = "orders",                // optional: the context's namespace
    Workload = "deployment/orders-api",  // kind/name, or a pod name
});

// Link the app's own configuration to the cluster.
var report = ConfigTracer.For(builder.Configuration).WithKubernetesCluster(cluster).Trace();
Console.WriteLine(report.Explain("Db:Timeout"));
// set in environment variable Db__Timeout from ConfigMap orders-config

// Or trace the container from outside, like a cluster browser.
var container = ConfigTracer.ForContainer(cluster.Manifests, cluster.Workload.Name).Trace();
```

`KubernetesClusterSource` implements `IConfigSource`, and `KubernetesClusterSourceProvider` (id `kubernetes-cluster`)
describes it to hosts such as the desktop app and the command-line tool. `cluster.ToLoadedSource().FindUnused(report)`
lists the ConfigMap and Secret keys nothing seems to use.

## Configure from appsettings and dependency injection

```json
{
  "ConfigFerret": {
    "KubernetesCluster": {
      "Context": "prod",
      "Namespace": "orders",
      "Workload": "deployment/orders-api",
      "Container": "api",
      "IncludeSecrets": false
    }
  }
}
```

```csharp
builder.Services.AddConfigFerretKubernetesCluster(builder.Configuration.GetSection("ConfigFerret:KubernetesCluster"));
var app = builder.Build();

var tracer = ConfigTracer.For(builder.Configuration);
await tracer.WithKubernetesClusterAsync(app.Services);
Console.WriteLine(tracer.Trace().Explain("Db:Timeout"));
```

Every property of `KubernetesClusterOptions` binds from the section (`Kubeconfig`, `InCluster`,
`AllowUnencryptedHttp` too). When the host starts, startup fails with a message naming the key when `Workload` is
missing or not `kind/name`, `Kubeconfig` names a file that does not exist, or both `Kubeconfig` and `InCluster` are set.

## Signing in

The official [KubernetesClient](https://www.nuget.org/packages/KubernetesClient) reads the kubeconfig, so whatever
`kubectl` uses works: a token, a client certificate, or an exec credential plugin (`kubelogin`, `aws eks get-token`,
`gke-gcloud-auth-plugin`). The kubeconfig is found in this order:

1. `Kubeconfig`, when set.
2. The files in `KUBECONFIG`, merged like `kubectl` does (the first file that defines a name wins).
3. `~/.kube/config`.
4. The pod's service account, when the process runs in a cluster (or when `InCluster` is set).

The credential stays in the kubeconfig or the service account, never in appsettings or a report. A plain-http API
server that is not loopback is refused unless `AllowUnencryptedHttp` is set.

## What it reads

| Object | Request | When |
| --- | --- | --- |
| The workload | `get` on `deployments`, `statefulsets`, `daemonsets`, `cronjobs`, `jobs` or `pods` | always; it must exist |
| ConfigMaps | `get` on each ConfigMap the workload names in `env`, `envFrom` and volumes | always |
| Secrets | `get` on each Secret the workload names | only with `IncludeSecrets` |
| `ExternalSecret`, `SecretStore` | `list` in the namespace (`external-secrets.io/v1`, then `v1beta1`) | always |
| `ClusterSecretStore` | `list` across the cluster | always |
| `SecretProviderClass` | `list` in the namespace (`secrets-store.csi.x-k8s.io/v1`, then `v1alpha1`) | always |

A Role like this covers it (add `secrets` only if you turn on `IncludeSecrets`, and a ClusterRole for
`clustersecretstores`):

```yaml
rules:
  - apiGroups: ["", "apps", "batch"]
    resources: [pods, deployments, statefulsets, daemonsets, cronjobs, jobs, configmaps]
    verbs: [get]
  - apiGroups: [external-secrets.io, secrets-store.csi.x-k8s.io]
    resources: [externalsecrets, secretstores, secretproviderclasses]
    verbs: [list]
```

An operator that is not installed (404) is skipped quietly. An object the credential cannot read (403), or a
ConfigMap or Secret that does not exist, becomes a trace warning instead of failing the load.

## Secrets

- Secrets are not read unless `IncludeSecrets` is on. A `secretKeyRef` then shows as a sensitive layer with an unknown
  value. The Secret an `ExternalSecret` writes, or a `SecretProviderClass` syncs, is still known by its keys.
- Values that are read are always marked sensitive, so reports show `***` unless `RevealSensitiveValues` is on.
  Warnings and entries never hold a value.

## Limits

- One workload per load. Pods that a Deployment owns are not listed; pass `pod/<name>` to read one directly.
- At most 200 ConfigMaps and 200 Secrets per workload are read.
- `ClusterExternalSecret`, `PushSecret` and generators are not read.
