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
| `configferret container [names]` | What one Kubernetes container or Docker Compose service gets, seen from outside the app: environment variables and mounted ConfigMap, Secret and compose secret files |

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

A Docker Compose service, with the override file. `${VAR}` is filled in from the project's `.env` (or each
`--compose-env-file`), never from your shell:

```
configferret container --compose-file compose.yaml --compose-file compose.override.yaml --compose-service api
```

Files that the compose files name (`env_file`, `extends`, `include`, config and secret files) are read only from the
project directory, after following symbolic links (a link to another drive or share counts as outside, unless the
project directory really lies there); the others are listed as warnings. Pass
`--compose-allow-files-outside-project` to read them from anywhere, as docker compose does.

`-m -` reads manifests from standard input, for example `kubectl get pod,configmap -o yaml | configferret container -m - -w api-7d9f8c-x2kqp`.

Possibly unused configuration, failing a CI step when there is any:

```
configferret unused --app src/Orders.Api -m rendered.yaml --chart charts/orders -f values-prod.yaml --reads reads.txt --exit-code
```

`--reads` takes the keys the app was seen reading (save `ConfigReadRecorder.ReadKeys` from the app), one per line
or as a JSON array. Repeat it to combine several runs.

## How the app's configuration is built

`trace` and `unused` read the app folder (`--app`, default the current folder) in the order
`WebApplication.CreateBuilder` does:

1. Environment variables starting with `ASPNETCORE_`, then those starting with `DOTNET_`, with the prefix removed, so
   `ASPNETCORE_URLS` sets the key `URLS`.
2. `appsettings.json`, then `appsettings.{Environment}.json`. The environment is `--environment`, else
   `--environment` in the app's command line, else `DOTNET_ENVIRONMENT`, else `ASPNETCORE_ENVIRONMENT` from the
   app's variables, else `Production`. A worker app built with `Host.CreateApplicationBuilder` ignores
   `ASPNETCORE_ENVIRONMENT`, and an app built with `Host.CreateDefaultBuilder(...).ConfigureWebHostDefaults(...)`
   lets `ASPNETCORE_ENVIRONMENT` win over both others (and `ASPNETCORE_` variables over `DOTNET_` ones); pass
   `--environment` for them. A name with a slash or a character a file name can't have is refused, so the settings
   file always comes from the app folder.
3. `{ApplicationName}.settings.json`, then `{ApplicationName}.settings.{Environment}.json`, which .NET 10 and later
   load. The name is the `AssemblyName` in the folder's project file, else the project file's name. This needs
   exactly one `*.csproj` in the folder; otherwise these files are not looked for, and with several the tool says
   so on standard error. A name built with `$(...)` falls back to the project file's name, and a name set in code
   is missed: pass such a file with `--config` and `--config-before-env`. A project file that links out of the app
   folder, is over 64 MB, holds a DTD, is not valid XML or can't be read is not read at all, for its
   `UserSecretsId` either, and the tool says so on standard error.
4. User secrets, in Development only, using the `UserSecretsId` in the folder's project file (or `--user-secrets-id`).
   `--no-user-secrets` turns this off.
5. Environment variables: the `AddEnvironmentVariables` call without a prefix that the host makes, then one call per
   `--env-prefix`, in order. The tool puts the `--env-prefix` calls below the app's command line, but an app that
   calls `AddEnvironmentVariables(prefix)` itself reads them above it: with
   `--env-prefix ORDERS_ --env ORDERS_Db__Timeout=42 -- --Db:Timeout=7` the tool says 7 where the app reads 42.
6. The app's command line: everything after `--`.
7. Each `--config` file, in order. A file the app adds with `AddJsonFile` comes after everything the host set up.
   `--config-before-env` loads them right after the files in steps 2 and 3 instead, below user secrets, the variables
   and the command line. Use it for an app that adds the variables again after its file, such as
   `AddJsonFile("extra.json").AddEnvironmentVariables()`, and for an `{ApplicationName}.settings.json` the tool does
   not find by itself.

`--no-default-env` leaves out the environment variables in steps 1 and 5 that the host adds by itself, keeping only
the `--env-prefix` calls, for an app that builds its configuration without the host defaults. To leave out only the
`ASPNETCORE_` and `DOTNET_` variables, pass `--no-default-env --env-prefix ""`.

The tool reads at most 64 MB from a file it finds by itself: the settings files in steps 2 and 3 and the files in a
`--manifests` folder. Such a settings file may be a symbolic link, but only to a file in the app folder. To read a
link that leads out of it, pass the file it leads to with `--config`: the link is then passed over, and the file is
read once, where the `--config` files load. On Windows a settings file that links to another drive or a share is
refused even then, because the tool does not follow a link to another root. Files you name on the command line are
read as they are.

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
| 2 | Bad arguments, or an input that could not be read: a missing file, a settings file that does not parse, a request that failed or timed out. |
| 130 | You cancelled the run with Ctrl+C. Nothing is printed. |

See the [ConfigFerret readme](https://github.com/Moonwalker1307/ConfigFerret#readme) for what each layer shows and the limits.
