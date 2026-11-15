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
// set in environment variable DB_TIMEOUT from Azure DevOps variable group api-production variable Db.Timeout
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
  `env:` block, links `NAME` to `Var`. `$[variables.Var]` and `${{ variables.Var }}` work too.
- **Same name** (on by default, `MatchByName`): a layer named like the variable, or named the way Azure Pipelines
  exposes it to scripts: upper case with `.` turned into `_`, so `Db.Timeout` matches `DB_TIMEOUT`. These links are
  marked as inferred.

Pipeline files also add their own variables: `- name: N` with `value: V`, and `N: V` in mapping form, at the pipeline,
stage or job level. A variable whose whole value is `$(Other)` is traced on to `Other`.

When several groups or pipeline variables set the same name, they are listed lowest priority first, in the order the
pipeline's `variables:` lists them (`- group: X` lines and variables), so the last one wins, the same as Azure Pipelines.
Groups that no pipeline file read links come first and say so. Without pipeline files, groups are in `GroupNames` order
(or by id when every group is read).

## Tokens and secrets

- The token needs the **Variable Groups (Read)** scope. In a pipeline, `System.AccessToken` works when the build
  service account can read the groups.
- The token is sent only in the `Authorization` header, only over HTTPS, and redirects are not followed. A token that
  Azure DevOps does not accept fails with a clear message (Azure DevOps answers it with a 203 sign-in page).
- Azure DevOps never returns secret values, so secret variables show up by name only and are marked sensitive.
- Groups linked to an Azure Key Vault list the vault's secret names; each is marked sensitive, its location is the vault
  name, and the note says which vault the value comes from.
- Secret variables reach scripts only when mapped in an `env:` block, which the note on a same-name match says.

## Limits

- Pipeline YAML is read line by line, not with a full YAML parser: templates (`- template:`), template expressions and
  runtime conditions are not evaluated, and every `variables:` block in a file counts.
- Variable group permissions per pipeline and library security are not checked.
- At most 100 pages of 100 groups are read per group name.
