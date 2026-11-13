<p align="center">
  <img src="https://raw.githubusercontent.com/Moonwalker1307/ConfigFerret/main/assets/logo.png" alt="ConfigFerret" width="600">
</p>

# ConfigFerret

**Why is this value 42?** ConfigFerret traces every effective .NET configuration value back through all the
layers that set it: appsettings files, environment variables, the command line, Kubernetes ConfigMaps and
Secrets, Helm values files and Kustomize overlays.

```
Db:Timeout = "42"
  set in appsettings.json, overridden by appsettings.Production.json, overridden again by environment variable DB__Timeout from ConfigMap api-config, rendered by Helm template templates/configmap.yaml from .Values.db.timeout, set in values-prod.yaml over values.yaml (chart default)
  [overridden] appsettings.json (line 4) = "30"
  [overridden] appsettings.Production.json (line 3) = "60"
  [effective]  environment variable DB__Timeout = "42"
    <- [effective]  ConfigMap api-config (key DB__Timeout, rendered.yaml:7) = "42"
                    envFrom on Deployment/api, container api
      <- [effective]  Helm template templates/configmap.yaml (line 6) = "42" [inferred]
                      renders .Values.db.timeout
        <- [overridden] values.yaml (chart default) (line 3, db.timeout) = "30"
        <- [effective]  values-prod.yaml (line 2, db.timeout) = "42"
```

Each layer has its own partial debug view (`GetDebugView()`, `helm get values`, `kubectl describe`).
ConfigFerret joins them into one history per key.

## Install

```
dotnet package add ConfigFerret
```

Targets `net10.0` and `net11.0`.

Two optional packages read variables from deployment tools over their REST APIs. They are separate so the core
package stays offline and never handles credentials unless you add one:

| Package | Adds |
| --- | --- |
| `ConfigFerret.GitHub` | GitHub Actions organization, repository and environment variables, and secret names ([readme](src/ConfigFerret.GitHub/README.md)) |
| `ConfigFerret.Octopus` | Octopus Deploy project and library variable set variables, matched by scope, read with an API key ([readme](src/ConfigFerret.Octopus/README.md)) |

## Use

### Just .NET configuration

```csharp
using ConfigFerret;

var builder = WebApplication.CreateBuilder(args);
Console.WriteLine(builder.Configuration.Explain("Db:Timeout"));

// Or every setting, as text or JSON:
var report = builder.Configuration.TraceProvenance();
Console.WriteLine(report.ToText());
File.WriteAllText("config-provenance.json", report.ToJson());
```

### With Kubernetes, Helm and Kustomize

```csharp
using ConfigFerret;
using ConfigFerret.Helm;
using ConfigFerret.Kubernetes;
using ConfigFerret.Kustomize;

var report = ConfigTracer.For(builder.Configuration)
    // Rendered manifests: `helm template`, `kustomize build` or `kubectl get deploy/api -o yaml` plus its ConfigMaps.
    .WithKubernetes(KubernetesManifests.LoadFiles("rendered.yaml"), workload: "api", container: "api")
    // The chart and the values passed to helm install/upgrade, in the same order.
    .WithHelm(HelmChart.Load("charts/api", valuesFiles: ["values-prod.yaml"], setValues: ["db.timeout=42"]))
    // Or the overlay you deploy with kustomize.
    .WithKustomize(Kustomization.Load("deploy/overlays/prod"))
    .Trace();

Console.WriteLine(report.Explain("Db:Timeout"));
```

### With GitHub Actions or Octopus Deploy variables

```csharp
using ConfigFerret;
using ConfigFerret.GitHub;   // dotnet package add ConfigFerret.GitHub
using ConfigFerret.Octopus;  // dotnet package add ConfigFerret.Octopus

var github = await GitHubVariables.LoadAsync(new GitHubVariablesOptions
{
    Owner = "contoso", Repository = "api", Environment = "production",
    Token = Environment.GetEnvironmentVariable("GITHUB_TOKEN"),
});
var octopus = await OctopusVariables.LoadAsync(new OctopusVariablesOptions
{
    ServerUri = new Uri("https://contoso.octopus.app/"),
    ApiKey = Environment.GetEnvironmentVariable("OCTOPUS_API_KEY")!,
    Project = "api",
});

var report = ConfigTracer.For(builder.Configuration)
    .WithGitHub(github, o => o.AddWorkflow(".github/workflows/deploy.yml"))
    .WithOctopus(octopus, o => o.Environment = "Production")
    .Trace();
```

Both packages can also be set up from configuration with dependency injection:

```csharp
builder.Services.AddConfigFerretGitHub(builder.Configuration.GetSection("ConfigFerret:GitHub"));
builder.Services.AddConfigFerretOctopus(builder.Configuration.GetSection("ConfigFerret:Octopus"));
var app = builder.Build();

var tracer = ConfigTracer.For(builder.Configuration);
await tracer.WithGitHubAsync(app.Services);
await tracer.WithOctopusAsync(app.Services);
var report = tracer.Trace();
```

Put the owner, repository, server address, project and environment in appsettings.json. Put the token and API key in
user secrets (`dotnet user-secrets set "ConfigFerret:Octopus:ApiKey" "API-..."`), an environment variable
(`ConfigFerret__Octopus__ApiKey`) or a key vault, never in a committed appsettings file: startup fails if either is in
an `appsettings*.json` file. See each package's readme for the settings.

Tokens and API keys go only in request headers, only over HTTPS, and never into a report. Secret values cannot be read
from either API, so secrets and sensitive variables appear by name and are redacted like any other secret.

### Inside a running pod

```csharp
if (KubernetesInCluster.IsInCluster)
{
    var pod = await KubernetesInCluster.LoadCurrentPodAsync();
    var report = ConfigTracer.For(builder.Configuration).WithKubernetes(pod).Trace();
}
```

This reads the pod (`POD_NAME` or `HOSTNAME`) and the ConfigMaps it references with the pod's service account.
Grant it `get` on `pods` and `configmaps` in its namespace. Secrets are never read through the API.

### From outside the app: a container in the cluster

A tool that only sees the cluster, such as a cluster browser, has the manifests but not the app's own
configuration. `ForContainer` traces what one container gets: every environment variable, then every file a
ConfigMap or Secret volume mounts, each followed back to the ConfigMap, Secret or env entry and from there to Helm
or Kustomize.

```csharp
using ConfigFerret;
using ConfigFerret.Helm;
using ConfigFerret.Kubernetes;

var manifests = KubernetesManifests.Parse(podYaml, "pod api-7d9f");
manifests.Add(configMapYaml, "configmap api-config");   // each ConfigMap and Secret the pod uses

// What the Helm release holds: the chart's values, the release's own values and the templates.
var chart = HelmChart.FromText(
    [("values.yaml (chart default)", chartValuesYaml), ("release values", releaseValuesYaml)],
    chartTemplates);                                     // (path, text) pairs

var report = ConfigTracer.ForContainer(manifests, workload: "api-7d9f", container: "api")
    .WithHelm(chart)
    .Trace();

Console.WriteLine(report.Explain("DB__Timeout"));
Console.WriteLine(report.Explain("/app/appsettings.Production.json"));
```

- Keys are the environment variable names, matched case-sensitively, and the files' absolute paths. A file's value
  is its full content; text output shortens it to one line.
- A variable that Kubernetes sets at run time (`fieldRef`) shows `(set at run time)`. One whose ConfigMap or Secret
  is not in the manifests shows `(unknown)`, and its value is `null` in JSON. An `envFrom` source or a volume without
  `items` that is not in the manifests gives a warning, since the names it adds can't be listed.
- Helm stores only the merged values a release was installed with, so a trace built from a release shows one
  "release values" layer rather than the separate `-f` files and `--set` flags.
- Nothing is read from the caller's own process environment, so the tool's own variables never reach the report.
  Leave Secrets out of the manifests if the tool shouldn't read them: a `secretKeyRef` to one then shows `(unknown)`.
- `WithKubernetes` throws on a container trace, since the manifests are already passed to `ForContainer`.

### Find possibly unused configuration

```csharp
var builder = WebApplication.CreateBuilder(args);
// After the last configuration source is added:
var reads = builder.Configuration.RecordReads();

// ... later, once the app has run through the code paths you care about:
var unused = builder.Configuration.TraceProvenance().FindUnused(reads);
Console.WriteLine(unused.ToText());
```

`FindUnused` reports two things:

- **Values that never win.** An appsettings key, env entry, ConfigMap key, Helm value or Kustomize literal that a
  later layer always overrides or removes in this trace, with the layer that beats it. Overrides that set the same
  value as the winner are called out, since they change nothing here. This needs no recorder: call `FindUnused()`.
- **Keys the app did not read.** Configured keys that `RecordReads()` never saw the app read, with every layer
  that sets them, so you can see where each one lives. Reads through `configuration["X"]`, `GetValue`, `GetSection`
  and options binding are all recorded, since the recorder sits under every provider. Plain process environment
  variables such as `PATH` are left out.

```
Values that never win (2)
  Db:Port
    appsettings.json (line 4) = "5432"
      overridden by environment variable ORDERS_Db__Port
      sets the same value as the layer that wins, so it changes nothing here
  Db:Timeout
    values.yaml (chart default) (line 8, db.timeout) = "30"
      overridden by values-eu.yaml (line 2, db.timeout)
      upstream of environment variable ORDERS_Db__Timeout <- ConfigMap orders-config <- Helm template templates/configmap.yaml

Keys the app did not read (1, from 9 recorded reads)
  Pod:Name = "orders-7c9d8f6b5-x2kqp"
    set in environment variable ORDERS_Pod__Name from downward API metadata.name
    ...
```

Everything it finds is **possibly** unused, never proven safe to delete. A value that never wins in production can
still be the default in development, and a key can be read on a code path that did not run while reads were
recorded. To cover more paths, save `reads.ReadKeys` from several runs or instances and pass them all to
`FindUnused(IEnumerable<string>)`. Code that walks every key, such as `AsEnumerable()` or `GetDebugView()`, marks
every key as read. ConfigFerret's own tracing is not recorded.

## What it traces

| Layer | What ConfigFerret shows |
| --- | --- |
| JSON files (`appsettings*.json`) | File and line number |
| User secrets | Marked sensitive |
| Environment variables | The exact variable name, including prefixes and `__`, and `*CONNSTR_` connection strings |
| Command line | The argument |
| Key-per-file (mounted secrets) | The file, marked sensitive |
| Kubernetes `env` | Literal value, `configMapKeyRef`, `secretKeyRef`, `fieldRef`, with workload and container |
| Kubernetes `envFrom` | Which ConfigMap or Secret, with prefix, and the precedence between `envFrom` and `env` |
| Kubernetes volumes | Which ConfigMap or Secret key a mounted file comes from (`configMap`, `secret`, `projected`, `subPath`, `items`) |
| Helm | The template line that renders the key, and every values file and `--set` that set or removed the value |
| Kustomize | Every `configMapGenerator` and `secretGenerator` across bases and overlays (`create`, `merge`, `replace`, `literals`, `envs`, `files`, `namePrefix`, `nameSuffix`, hash suffixes) |
| GitHub Actions (`ConfigFerret.GitHub`) | The organization, repository or environment variable or secret, from a workflow `env:` line, an explicit link or the same name |
| Octopus Deploy (`ConfigFerret.Octopus`) | The project or library variable set variable whose scope fits the deployment, ordered by scope specificity |

Workloads: Deployment, StatefulSet, DaemonSet, ReplicaSet, Job, CronJob, Pod and `List` documents.

## Secrets

Values are redacted as `***` when they come from a Secret, user secrets, key-per-file or a `*CONNSTR_` environment
variable, or when the key looks secret (`password`, `pass`, `token`, `apikey`, `key`, `signingkey`, `connectionstring`, `sas`, `dsn` and so on). Turn this off
only on your own machine:

```csharp
ConfigTracer.For(config).WithOptions(o => o.RevealSensitiveValues = true);
```

## Limits

- Helm links are found by scanning templates for `.Values.x.y` on lines that set an env `value:` or a data key,
  so they are marked `[inferred]`. Values used through `with`, `range`, `include` or helpers are not linked yet.
  Subchart values are not traced yet.
- Helm links match env entries and data keys by name, so an env entry that overrides a templated ConfigMap key of
  the same name is linked to that ConfigMap's template too. The note then says the values and the manifest disagree.
- Kustomize patches that edit env entries or ConfigMap data are not traced yet.
- Like kustomize, a generator's `envs` files must be in or below the folder of the kustomization that names them.
  Pass `allowFilesOutsideFolder: true` to `Kustomization.Load` to lift this, like `--load-restrictor LoadRestrictionsNone`.
- GitHub and Octopus links by name or JSON key are guesses, so they are marked `[inferred]`; a workflow `env:` line or
  `Map` makes a certain link. When a name is on both an environment variable and the Kubernetes env entry behind it,
  the GitHub or Octopus layer shows under both. Octopus `#{...}` references are not expanded.
- YAML whose aliases expand to more than 10 million characters is rejected.
- Unread keys come from recorded reads only; the code is not scanned for keys it could read.
- Custom configuration providers show their `ToString()`. Add an `IOriginResolver` with `WithResolver` to trace
  further upstream from them.

## Build

```
dotnet build
dotnet test
dotnet pack src/ConfigFerret -c Release
dotnet pack src/ConfigFerret.GitHub -c Release
dotnet pack src/ConfigFerret.Octopus -c Release
```

Building `net11.0` needs the .NET 11 SDK. With only the .NET 10 SDK, pass `-p:TargetFrameworks=net10.0`.

`GoldenTests` traces three realistic setups in `tests/ConfigFerret.Tests/TestData/golden/` (a Helm release, a
Kustomize overlay and a developer machine) and compares the full text and JSON output with the approved snapshots
in `tests/ConfigFerret.Tests/Snapshots/`, using [Verify](https://github.com/VerifyTests/Verify). When you change
the output on purpose, the failing test writes a `.received.` file next to the snapshot: check the diff, then
rename it to `.verified.` (or accept it in your diff tool) and commit it.

CI builds and tests both on Linux for pull requests, and on Windows, Linux and macOS after a merge to `main`, then packs the three NuGet packages, all with the same version. Pushing a `v*` tag publishes
them to nuget.org with [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing), so no API key
is stored. It needs a trusted publishing policy on nuget.org for workflow `ci.yml` in this repository, owned by
the nuget.org account `Moonwalker1307` (set the `NUGET_USER` repository variable to use another account).

### Mutation testing

[Stryker.NET](https://stryker-mutator.io/docs/stryker-net/introduction/) checks how many code changes the tests
catch. It runs on `net10.0`, and needs the `TargetFrameworks` variable so the build skips `net11.0`:

```
dotnet tool restore
cd tests/ConfigFerret.Tests
TargetFrameworks=net10.0 dotnet stryker
```

The HTML report is written to `tests/ConfigFerret.Tests/StrykerOutput/<run>/reports/`.

The parsers are fuzzed every night on Linux with SharpFuzz and libFuzzer. See [fuzz/README.md](fuzz/README.md).

## License

MIT
