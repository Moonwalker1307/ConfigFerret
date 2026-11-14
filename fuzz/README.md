# Fuzzing

The parsers are fuzzed with [SharpFuzz](https://github.com/Metalnem/sharpfuzz) and libFuzzer, through the
[libfuzzer-dotnet](https://github.com/Metalnem/libfuzzer-dotnet) driver. The `Fuzz` workflow
(`.github/workflows/fuzz.yml`) runs every target for 20 minutes each night, and for one minute on pull requests
that touch `src/` or `fuzz/`. Each night starts from the corpus the previous night grew, kept in the Actions cache.

## Targets

| Target | What it feeds | Fails when |
| --- | --- | --- |
| `json` | `JsonLineLocator` (appsettings line numbers) | it throws on JSON the .NET JSON provider reads, or misses a key the provider has |
| `helm-values` | `HelmChart.FromText`, values files split by `%%` lines, then the Helm resolver | anything other than `FormatException` |
| `helm-set` | `HelmChart.Load` with one `--set` expression per line | any exception |
| `helm-template` | template scanning for `.Values` references | any exception |
| `kubernetes` | `KubernetesManifests.Parse`, then the env and volume resolver for every container | anything other than `FormatException` |
| `container` | `KubernetesManifests.Parse`, then `ConfigTracer.ForContainer(...).Trace()` for the default pick and every container, rendered as text and JSON | anything other than `FormatException`, a key listed twice or not found by `Find`, or a Secret value that is not redacted |
| `kustomize` | `Kustomization.Load` on `overlay/kustomization.yaml %% overlay/app.properties %% base/kustomization.yaml` | anything other than `FormatException`, a missing kustomization or a cycle |
| `env-names` | environment variable to configuration key matching: prefix, key and value lines, then `NAME=VALUE` lines | any exception, or a match that is not in the environment |
| `github` | `GitHubVariables.LoadAsync` against a scripted server (first byte picks the options, then one response per request, split by `%%`, each optionally starting with `HTTP <status>`), then the GitHub resolver with links read from the same text as workflows | anything other than `HttpRequestException`, a secret with a value, or the token or the layer's value in a message or note |
| `octopus` | `OctopusVariables.LoadAsync` against a scripted server, the same way, then the Octopus resolver with scope context picked by the first byte | anything other than `HttpRequestException` or the documented not-found `InvalidOperationException`, a sensitive value, or the API key or the layer's value in a message or note |
| `vault` | `VaultSecrets.LoadAsync` against a scripted server, the same way; once the script runs out, every folder holds another folder and a secret, so the request and depth caps are always hit. Then the Vault resolver with same-name and External Secrets layers | anything other than `HttpRequestException`, more requests or secrets than the caps, a request outside `/v1/`, data or a value read without `IncludeValues`, a step that is not sensitive or whose reference no entry has, or the token or the layer's value in a message or note |
| `consul` | `ConsulKeyValues.LoadAsync` against a scripted server (one `?recurse` response), then the Consul resolver | anything other than `HttpRequestException`, more keys than `MaxKeys`, an entry with no configuration key or not found by `Find`, JSON expanded with `ExpandJson` off, a step whose reference no entry has or whose sensitivity differs from the option, or the token or the layer's value in a message or note |
| `kubernetes-cluster` | `KubernetesClusterReader.LoadAsync` against a scripted API server (first byte picks the workload kind, `IncludeSecrets` and the container; then one response per request: the workload, the operator object lists, the ConfigMaps, the Secrets), then `ConfigTracer.ForContainer` over what it read | anything other than `InvalidOperationException`, a Secret read with `IncludeSecrets` off, the token in a message, an entry whose sensitivity does not match its reference, or a Secret value in the report |
| `azure-keyvault` | `AzureKeyVaultSecrets.LoadAsync` through the Azure SDK against a scripted server (first byte picks values, disabled secrets, a name filter and the store type; Key Vault's 401 challenge is answered first), then the Key Vault resolver and a traced report | anything other than `RequestFailedException`, a value or disabled secret that was not asked for, a step that is not sensitive, the token or the layer's value in a message or note, or a secret value left unredacted in the report |
| `azure-appconfig` | `AzureAppConfigurationSettings.LoadAsync` the same way (first byte picks labels, Key Vault references, feature flags, key filter, trimmed prefix, matching and token or connection-string sign-in), then the App Configuration resolver and a trace | anything other than `RequestFailedException`, a Key Vault reference with a value, a feature flag not under `FeatureManagement:`, or the token, access key or layer's value in a message or note |
| `desktop-tar` | `GitHubAccount.ExtractAsync` (desktop app): first byte 0 means the rest is the gzip, otherwise the rest is gzipped by the target so tar headers get fuzzed | anything the download's call site does not catch, a file outside the target, a link, or more than the size cap |
| `dotenv` | the desktop app's `DotEnv.Parse` | anything other than `FormatException`, or an empty name |
| `secret-audit` | `SecretAudit.LooksLikeCredential` and `IsPlaceholder` on each line and on the whole input | any exception, more than two seconds, or a placeholder rated as a credential |
| `scanner` | `RepositoryScanner.Scan` over a temp folder written from `path` line then content, files split by `%%` | any exception, or a result that is not inside the folder |
| `workspace` | `WorkspaceStore.Load` on workspace JSON, then save and load again | anything other than the exceptions the app catches (`JsonException`, `IOException`, `UnauthorizedAccessException`, `NotSupportedException`), a null member, or a save that does not load back the same |
| `unused` | `FindUnused` and `RecordReads` over `appsettings.json %% appsettings.Production.json %% KEY=VALUE lines %% Kubernetes manifests %% keys read` | a never-winning step that wins, a winner that does not win, a read key listed as unread, counts that do not add up, an unredacted sensitive value, or lists out of the report's order |

Seeds are in `corpus/<target>/` and libFuzzer dictionaries in `dictionaries/`.

## Run locally

Needs Linux, the .NET 10 SDK, and clang with the libFuzzer runtime (`apt-get install clang libclang-rt-dev`).

```
fuzz/run.sh kubernetes 300
```

The script builds the harness, instruments the ConfigFerret assemblies (the desktop app included) and `YamlDotNet.dll`, and runs one target for the
given number of seconds. It grows the corpus in `fuzz/work/<target>/corpus/` and writes crashes to
`fuzz/findings/<target>/`. Delete `fuzz/out/` to rebuild after changing the code.

## Reproduce a crash

The instrumented build only runs under libFuzzer, so replay with the normal build:

```
dotnet build fuzz/ConfigFerret.Fuzz -c Release -p:TargetFrameworks=net10.0
fuzz/ConfigFerret.Fuzz/bin/Release/net10.0/ConfigFerret.Fuzz kubernetes fuzz/findings/kubernetes/crash-<hash>
```

When the nightly run fails, the workflow replays the crash in its log and uploads it as the
`fuzz-findings-<target>` artifact. After fixing it, add the input to `tests/ConfigFerret.Tests/TestData/fuzz/`
with a test in `FuzzRegressionTests.cs`.
