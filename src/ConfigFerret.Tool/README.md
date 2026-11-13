# ConfigFerret.Tool

The `configferret` command traces .NET configuration values back through every layer that sets them, from the
command line and without writing code: appsettings files, environment variables, the app's command line, Kubernetes
ConfigMaps and Secrets, Helm values, Kustomize overlays, and GitHub Actions and Octopus Deploy variables. It is built
on the [ConfigFerret](https://www.nuget.org/packages/ConfigFerret) library.

```
dotnet tool install -g ConfigFerret.Tool
```

It needs the .NET 10 or 11 runtime. Secret values are shown as `***` unless you pass `--reveal-secrets`.

## Commands

| Command | Traces |
| --- | --- |
| `configferret trace [keys]` | An app's configuration, built from its folder the way `WebApplication.CreateBuilder` builds it, and optionally the manifests, chart or overlay it is deployed with |
| `configferret unused` | The same inputs, listing values a later layer always overrides and, with `--reads`, keys the app never read |
| `configferret container [names]` | What one Kubernetes container gets, seen from outside the app: environment variables and mounted ConfigMap and Secret files |

Run `configferret <command> --help` for every option.

## Examples

Why is `Db:Timeout` 42 in production? Trace the app folder with the manifests `helm template` renders and the values
you install with:

```
helm template orders charts/orders -f values-prod.yaml > rendered.yaml
configferret trace Db:Timeout --app src/Orders.Api -m rendered.yaml -w orders \
  --chart charts/orders -f values-prod.yaml --env-prefix ORDERS_
```

The same for a Kustomize overlay, as JSON:

```
kustomize build deploy/overlays/prod > rendered.yaml
configferret trace --app src/Billing.Api -m rendered.yaml -k deploy/overlays/prod --format json --output provenance.json
```

On your own machine, in Development, with user secrets and a command-line argument:

```
configferret trace Paging:PageSize -e Development --env-file local.properties -- --Paging:PageSize=50
```

A running pod, from the cluster, with the release's values:

```
# The pod and each ConfigMap (and, if the tool may read them, Secret) it uses, as one List.
kubectl get pod/api-7d9f8c-x2kqp configmap/api-config -o yaml > pod.yaml
helm get values api -o yaml > release-values.yaml
configferret container -m pod.yaml -c api --chart charts/api -f release-values.yaml
```

`-m -` reads manifests from standard input, for example `kubectl get pod,configmap -o yaml | configferret container -m - -w api-7d9f8c-x2kqp`.

Possibly unused configuration, failing a CI step when there is any:

```
configferret unused --app src/Orders.Api -m rendered.yaml --chart charts/orders -f values-prod.yaml --reads reads.txt --exit-code
```

`--reads` takes the keys the app was seen reading (save `ConfigReadRecorder.ReadKeys` from the app), one per line
or as a JSON array. Repeat it to combine several runs.

## How the app's configuration is built

`trace` and `unused` read the app folder (`--app`, default the current folder) in the order the .NET host does:

1. `appsettings.json`, then `appsettings.{Environment}.json`. The environment is `--environment`, else
   `ASPNETCORE_ENVIRONMENT` or `DOTNET_ENVIRONMENT` from the app's variables, else `Production`.
2. Each `--config` file, in order.
3. User secrets, in Development only, using the `UserSecretsId` in the folder's project file (or `--user-secrets-id`).
   `--no-user-secrets` turns this off.
4. Environment variables, through one `AddEnvironmentVariables` call per `--env-prefix` (one without a prefix by default).
5. The app's command line: everything after `--`.

The app's environment variables are what the container gets from `--manifests`, then `--env-file`, then `--env`,
each winning over the one before. The tool's own environment is never read into a trace, so your shell's variables
and tokens can't leak in. A variable Kubernetes fills in at run time, such as a `fieldRef`, has no value in the
manifests; the tool names it and you can pass it with `--env`.

## GitHub Actions and Octopus Deploy

| Option | Credential, read from the environment only |
| --- | --- |
| `--github-repo owner/name`, `--github-environment`, `--github-workflow` | `GITHUB_TOKEN` or `GH_TOKEN` |
| `--octopus-project`, `--octopus-server` (or `OCTOPUS_URL`), `--octopus-environment` and the other scopes | `OCTOPUS_API_KEY` |

Tokens and API keys are never accepted as options, so they stay out of shell history, go only in request headers,
only over HTTPS, and never into a report.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | The trace ran. |
| 1 | A key you named is not set, or `unused --exit-code` found something. |
| 2 | Bad arguments, or an input that could not be read. |

See the [ConfigFerret readme](https://github.com/Moonwalker1307/ConfigFerret#readme) for what each layer shows and the limits.
