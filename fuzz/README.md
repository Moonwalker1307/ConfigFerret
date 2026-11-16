# Fuzzing

The parsers are fuzzed with [SharpFuzz](https://github.com/Metalnem/sharpfuzz) and libFuzzer, through the
[libfuzzer-dotnet](https://github.com/Metalnem/libfuzzer-dotnet) driver. The `Fuzz` workflow
(`.github/workflows/fuzz.yml`) runs every target for 20 minutes each night, and not on pull requests. Each night
starts from the corpus the previous night grew, kept in the Actions cache.

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
| `octopus` | `OctopusVariables.LoadAsync` against a scripted server, the same way, then the Octopus resolver with scope context picked by the first byte | anything other than `HttpRequestException` or the documented not-found `InvalidOperationException`, a sensitive variable with a value, a step value that no plain variable holds, or the API key or the layer's value in a message or note |
| `vault` | `VaultSecrets.LoadAsync` against a scripted server, the same way; once the script runs out, every folder holds another folder and a secret, so the request and depth caps are always hit. Then the Vault resolver with same-name and External Secrets layers | anything other than `HttpRequestException`, more requests or secrets than the caps, a request outside `/v1/`, data or a value read without `IncludeValues`, a step that is not sensitive or whose reference no entry has, or the token or the layer's value in a message or note |
| `consul` | `ConsulKeyValues.LoadAsync` against a scripted server (one `?recurse` response), then the Consul resolver | anything other than `HttpRequestException`, more keys than `MaxKeys`, an entry with no configuration key or not found by `Find`, JSON expanded with `ExpandJson` off, a step whose reference no entry has or whose sensitivity differs from the option, or the token or the layer's value in a message or note |
| `kubernetes-cluster` | `KubernetesClusterReader.LoadAsync` against a scripted API server (first byte picks the workload kind, `IncludeSecrets` and the container; then one response per request: the workload, the operator object lists, the ConfigMaps, the Secrets), then `ConfigTracer.ForContainer` over what it read | anything other than `InvalidOperationException`, a Secret read with `IncludeSecrets` off, the token in a message, an entry whose sensitivity does not match its reference, or a Secret value in the report |
| `azure-keyvault` | `AzureKeyVaultSecrets.LoadAsync` through the Azure SDK against a scripted server (first byte picks values, disabled secrets, a name filter and the store type; Key Vault's 401 challenge is answered first), then the Key Vault resolver and a traced report | anything other than `RequestFailedException`, a value or disabled secret that was not asked for, a step that is not sensitive, the token or the layer's value in a message or note, or a secret value left unredacted in the report |
| `azure-appconfig` | `AzureAppConfigurationSettings.LoadAsync` the same way (first byte picks labels, Key Vault references, feature flags, key filter, the trimmed prefixes `App:` and `FeatureManagement:`, matching and token or connection-string sign-in), then the App Configuration resolver and a trace over up to 10 values of each stored setting (the values a JSON key-value is flattened to) | anything other than `RequestFailedException`, a key-value or flattened value its keys do not find, a Key Vault reference with a value, a feature flag whose key is not exactly `FeatureManagement:<id>` (the `id` in the response, else the name in the stored key) with the trimmed prefix removed, or empty for a flag in the newer layout (with variants, an allocation or telemetry), or the token, access key or layer's value in a message or note |
| `desktop-tar` | `GitHubAccount.ExtractAsync` (desktop app): first byte 0 means the rest is the gzip, otherwise the rest is gzipped by the target so tar headers get fuzzed | anything the download's call site does not catch, a file outside the target, a link, or more than the size cap |
| `dotenv` | the desktop app's `DotEnv.Parse` | anything other than `FormatException`, or an empty name |
| `secret-audit` | `SecretAudit.LooksLikeCredential` and `IsPlaceholder` on each line and on the whole input | any exception, more than two seconds, or a placeholder rated as a credential |
| `scanner` | `RepositoryScanner.Scan` over a temp folder written from `path` line then content, files split by `%%` | any exception, or a result that is not inside the folder |
| `workspace` | `WorkspaceStore.Load` on workspace JSON, then save and load again | anything other than the exceptions the app catches (`JsonException`, `IOException`, `UnauthorizedAccessException`, `NotSupportedException`), a null member, or a save that does not load back the same |
| `code-reads` | the desktop app's `CodeReadScanner.FromSources` over C# sources split by `%%`, then `Check` on string literals from the input and fixed keys, and `StripComments` | any exception, more than two seconds, a not-read verdict without a reason, or `StripComments` output longer than its input |
| `helm-usage` | the desktop app's `HelmValueUsage.FindDead` on `templates %% values yaml` | anything other than `YamlException` from reading the values, more than two seconds, or a dead path that is not a leaf of the values |
| `aws-secrets` | `AwsSecrets.LoadAsync` through the real AWS SDK client against a scripted server (first byte picks the options, then one JSON response per request, split by `%%`, each optionally starting with `HTTP <status>`), then the Secrets Manager resolver over names, .NET keys, External Secrets references and provider keys | anything other than `AmazonServiceException` or `AmazonClientException`, a value read without `IncludeValues`, a step that is not sensitive, or the secret key or the layer's value in a message or note |
| `aws-parameters` | `AwsParameters.LoadAsync` the same way, then the Parameter Store resolver | the same, or a `SecureString` value kept without decryption, or a step whose sensitivity differs from its parameter's |
| `gcp-secrets` | `GcpSecrets.LoadAsync` against a scripted server the same way (secret list pages, version lists, version access), then the Secret Manager resolver over same-name, external secret, CSI resource name and provider layers | anything other than `HttpRequestException`, a request outside the API, more secrets than `MaxSecrets`, a value or versions that were not asked for, a step that is not sensitive or has no matching entry, or the token or the layer's value in a message or note |
| `azure-devops` | `AzureDevOpsVariableGroups.LoadAsync` against a scripted server, the same way, where a response may also set headers with `> Name: value` lines (for `X-MS-ContinuationToken`); then the same text as pipeline YAML, and the resolver, following pipeline variables that refer to others | anything other than `HttpRequestException`, more than 100 pages per group name, a request outside the variable groups API, a group kept twice, a secret or Key Vault value, a step whose reference no entry has, or the token or the layer's value in a message or note |
| `gitlab` | `GitLabVariables.LoadAsync` against a scripted server with headers (for `X-Next-Page`), then the same text as `.gitlab-ci.yml`, and the resolver with the environment picked by the first byte | anything other than `HttpRequestException`, a request outside `/api/v4/` or to `/admin/` without `IncludeInstance`, a hidden value or a masked one without `IncludeMaskedValues`, two variables from one owner or the wrong order in a chain, a scope that does not apply, or the token or the layer's value in a message or note |
| `azure-devops-trace` | up to three pipeline files split by `%%`, traced whole with `ConfigTracer.For(...).WithAzureDevOps(...).Trace()` the way an app is: every name the files set or link is an environment variable, and every name they refer to and do not set is in a variable group, half of them secret (the first byte picks matching by name, a group linked to Key Vault, and variables for the names the files set too) | any exception, a trace that takes more than five seconds, a setting with more origin steps than the tracer's bound or a chain deeper than `MaxDepth`, more steps in the trace than its bound, a setting that was cut off or whose value comes through a secret but is not redacted, or a sensitive step that shows its value |
| `gitlab-trace` | the same for CI files with `WithGitLab`, where the names are masked, file or plain project, group and instance variables, and the first byte also picks the environment and scopes | the same, or the value of a masked or file variable anywhere in the report, its warnings, notes or sources |
| `gitops` | `GitOpsApplication.ParseAll` on `GitOps manifests %% source manifests`, then `HelmChart.ForGitOps` and the Helm resolver for each Helm release, and the GitOps resolver for every kustomize patch, `${VAR}` placeholder and substitution variable | anything other than `FormatException` |
| `terraform` | `TerraformOutputs.Parse` and `ParseState` on `terraform output -json %% state file %% .tf file`, with output blocks read from the `.tf` text, then the Terraform resolver for every output | anything other than `FormatException`, an output that does not match its own name, a sensitive output shown by `ToString` or a step that is not sensitive, or the layer's value in a note |
| `unused` | `FindUnused` and `RecordReads` over `appsettings.json %% appsettings.Production.json %% KEY=VALUE lines %% Kubernetes manifests %% keys read` | a never-winning step that wins, a winner that does not win, a read key listed as unread, counts that do not add up, an unredacted sensitive value, or lists out of the report's order |
| `dotenv-layers` | `DotEnvLayers.Parse` over dotenv files split by `%%`, with shell variables, then the dotenv resolver for every key | anything other than `FormatException`, an entry outside its file or out of order, `Find` or `Variables` disagreeing with the entries, or a key the resolver does not link |
| `compose` | `ComposeProject.Parse` on `compose.yaml %% compose.override.yaml %% project env file`, then `ConfigTracer.ForCompose(...).Trace()` for the default pick and every service, rendered as text and JSON | anything other than `FormatException`, a key listed twice, not found by `Find` or without layers, or a secret or secret-named value that is not redacted |

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

When the nightly run fails, the workflow uploads the failing input (`crash-<hash>`, or `timeout-<hash>` for one
that took longer than 10 seconds) as the `fuzz-findings-<target>` artifact, then replays it in its log. The replay is
stopped after two minutes, so an input that hangs shows no stack trace there. After fixing it, add the input to
`tests/ConfigFerret.Tests/TestData/fuzz/` with a test in `FuzzRegressionTests.cs`.
