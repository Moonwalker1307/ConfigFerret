# ConfigFerret.AzureDevOps

Adds Azure Pipelines variable groups to [ConfigFerret](https://github.com/Moonwalker1307/ConfigFerret) traces, so a
value read from an environment variable, a Kubernetes env entry or a ConfigMap key is traced back to the variable group
variable (or pipeline YAML variable) that set it.

```
dotnet add package ConfigFerret.AzureDevOps
```

```csharp
using ConfigFerret;
using ConfigFerret.AzureDevOps;

var groups = await AzureDevOpsVariableGroups.LoadAsync(new AzureDevOpsOptions
{
    OrganizationUrl = new Uri("https://dev.azure.com/contoso/"),
    Project = "api",
    GroupNames = { "shared", "api-production" },                                   // leave empty for every group
    PersonalAccessToken = Environment.GetEnvironmentVariable("AZURE_DEVOPS_EXT_PAT"), // never hard-code it
});

var report = ConfigTracer.For(builder.Configuration)
    .WithAzureDevOps(groups, o => o.AddPipeline("azure-pipelines.yml"))
    .Trace();

Console.WriteLine(report.Explain("Db:Timeout"));
// set in environment variable Db__Timeout from Azure DevOps variable group api-production variable Db.Timeout
// (when azure-pipelines.yml has `env: Db__Timeout: $(Db.Timeout)` on the step that starts the app)
```

## Configure from appsettings and dependency injection

Put everything except the token in configuration, and register the source:

```json
// appsettings.json: no token here
{
  "ConfigFerret": {
    "AzureDevOps": {
      "OrganizationUrl": "https://dev.azure.com/contoso/",
      "Project": "api",
      "GroupNames": [ "shared", "api-production" ],
      "Pipelines": [ "azure-pipelines.yml" ],
      "Links": { "Db__Password": "api-production/Db.Password" }
    }
  }
}
```

```csharp
builder.Services.AddConfigFerretAzureDevOps(builder.Configuration.GetSection("ConfigFerret:AzureDevOps"));
var app = builder.Build();

var tracer = ConfigTracer.For(builder.Configuration);
await tracer.WithAzureDevOpsAsync(app.Services);
Console.WriteLine(tracer.Trace().Explain("Db:Timeout"));
```

Supply the token from somewhere that is not committed:

```
dotnet user-secrets set "ConfigFerret:AzureDevOps:PersonalAccessToken" "<pat>"   # development
export ConfigFerret__AzureDevOps__PersonalAccessToken="<pat>"                    # CI, containers
```

In a pipeline, map the job's token: `env: ConfigFerret__AzureDevOps__PersonalAccessToken: $(System.AccessToken)`. To use
a Microsoft Entra ID token instead, set `AccessToken`; it is sent as a bearer token. Set one of the two, not both.

Every property of `AzureDevOpsOptions` and `AzureDevOpsTraceOptions` binds from the section (`GroupNames`, `Pipelines`,
`MatchByName`, `Links` and so on). A pipeline file in `Pipelines` that cannot be read becomes a trace warning.

When the host starts, the options are checked, and startup fails with a message naming the key when:

- `OrganizationUrl` or `Project` is missing,
- neither `PersonalAccessToken` nor `AccessToken` is set, or both are,
- `OrganizationUrl` is not HTTPS (or holds credentials),
- a token is in any `appsettings*.json` file, even one that another source overrides.

The messages never include the token.

## How layers are linked

- **Explicit links** (`Map("Db__Password", "Db.Password", "api-production")`, or `Links` with `Group/Variable` or just
  `Variable`).
- **Pipeline files** (`AddPipeline`): every `NAME: $(Var)` line outside a `variables:` block, for example in a step's
  `env:` block, links `NAME` to `Var`. `$[variables.Var]` and `${{ variables.Var }}` work too. The report names a file
  by its path from the current directory (its full path when it is outside it), so pipeline files of one name in
  different folders are told apart. Variable names may hold `-`, as Key Vault secret names such as
  `sql-connection-string` do, and inside `$(...)` and `variables['...']` spaces too.
- **Same name** (on by default, `MatchByName`): a layer named like the variable, or named the way Azure Pipelines
  exposes it to scripts: upper case with `.` turned into `_`, so `Db.Timeout` matches `DB_TIMEOUT`. These links are
  marked as inferred.

Pipeline files also add their own variables: `- name: N` with `value: V`, and `N: V` in mapping form, at the pipeline,
stage or job level. A variable whose whole value is `$(Other)` is traced on to `Other`, leaving out the pipeline
variables that are themselves `Other: $(Other)`, since they cannot supply a value. A variable that refers to itself
(`Version: $(Version)` in a stage) is traced on to the other `Version` variables, once: the ones in pipeline files are
marked as inferred, since which one Azure Pipelines expands the reference to is not certain, and variable group
variables are listed as usual.

A job gets the pipeline's variables and groups, its stage's and its own (`- stage:`, `- job:` and `- deployment:`
items), never another stage's or job's: a `- group:` line, a variable and an `env:` link count for the stage or job
they are in, and a variable followed from a stage or job is looked up there. A group that no pipeline file links counts
everywhere. Steps for a stage or job variable name it, as in `Azure Pipelines variable Db.Name (stage Staging)`.

Once a name has a pipeline file link, every stage or job that may give scripts that name is followed: the links in
stages, jobs and at the top level, and the stages and jobs without a link for it where a pipeline variable, or a group
variable that is not secret, has that name. When they come to different values, every one of them is listed as an
inferred step and a warning names them; the same goes for stages or jobs that set a name to different values in
`variables:`, where a stage's variable counts for the stage's jobs that do not set it again, so a stage and one of its
jobs can disagree too. A value that is a reference is compared by what it comes to in its own stage or job, and group
variables are compared by value. A value the trace cannot compare, such as a secret, a value with a reference inside it
or a reference that stages or jobs below may set again, keeps them alternatives, and the warning then says the values
cannot be compared rather than that they differ. None of the alternatives is compared with the value seen here, and
the one that comes to that value goes last (the tracer marks the last step effective), also when it is reached through
a whole-value reference such as `Alias: $(Timeout)`. When any of them may hold a secret, all of them are marked
sensitive. There is no option to say which stage or job the trace is about.

One trace step looks at no more than 1,000 pipeline variables and group variables, the most upstream steps the tracer
keeps for a setting. It counts only what the stage or job sees, and keeps the ones that win, as the tracer keeps the
end of a chain: pipeline variables from the narrowest scope and the end of the file, and of the groups that apply, the
linked ones before those no file links.
Past that, the rest is left out, every step listed is marked inferred and sensitive, and a warning says that not all
of them were followed.

When several groups or pipeline variables set the same name, they are listed lowest priority first, so the last one
wins, as in Azure Pipelines: a job's own win over its stage's, and a stage's over the pipeline's, wherever their
`variables:` block is written; within one of them, the order its `variables:` lists them (`- group: X` lines and
variables) decides.
Groups that no pipeline file read links come first and say so. Without pipeline files, groups are in `GroupNames` order
(or by id when every group is read).

## Tokens and secrets

- The token needs the **Variable Groups (Read)** scope. In a pipeline, `System.AccessToken` works when the build
  service account can read the groups.
- The token is sent only in the `Authorization` header, only over HTTPS, and redirects are not followed. A token that
  Azure DevOps does not accept fails with a clear message (Azure DevOps answers it with a 203 sign-in page).
- When Azure DevOps refuses a request, the error gives the reason Azure DevOps sends, cut short and without control
  characters, and leaves it out when it repeats the token or four of its characters in a row.
- Azure DevOps never returns secret values, so secret variables show up by name only and are marked sensitive.
- Variables whose name looks like a secret's (`Smtp.Password`, by `TraceOptions.DefaultSensitiveKeyPattern`, whatever
  `SensitiveKeyPattern` is set to) are marked sensitive even when they are not secret, both in variable groups and in
  pipeline YAML, so every layer they set is redacted.
- Groups linked to an Azure Key Vault list the vault's secret names; each is marked sensitive, its location is the vault
  name, and the note says which vault the value comes from.
- Secret variables reach scripts only when mapped in an `env:` block, which the note on a same-name match says.

## Limits

- Pipeline YAML is read line by line, not with a full YAML parser: templates (`- template:`), template expressions and
  runtime conditions are not evaluated, so every `variables:` block counts for its stage or job, whatever its
  condition, and a job's `environment:` is not used to pick it.
- Variable group permissions per pipeline and library security are not checked.
- At most 100 pages of 100 groups are read per group name.
