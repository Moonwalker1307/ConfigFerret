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
| `unused` | `FindUnused` and `RecordReads` over `appsettings.json %% appsettings.Production.json %% KEY=VALUE lines %% Kubernetes manifests %% keys read` | a never-winning step that wins, a winner that does not win, a read key listed as unread, counts that do not add up, an unredacted sensitive value, or lists out of the report's order |

Seeds are in `corpus/<target>/` and libFuzzer dictionaries in `dictionaries/`.

## Run locally

Needs Linux, the .NET 10 SDK, and clang with the libFuzzer runtime (`apt-get install clang libclang-rt-dev`).

```
fuzz/run.sh kubernetes 300
```

The script builds the harness, instruments the ConfigFerret assemblies and `YamlDotNet.dll`, and runs one target for the
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
