# ConfigFerret.GitLab

Adds GitLab CI/CD variables to [ConfigFerret](https://github.com/Moonwalker1307/ConfigFerret) traces, so a value read
from an environment variable, a Kubernetes env entry or a ConfigMap key is traced back to the project, group or
instance variable (or `.gitlab-ci.yml` variable) that set it, with the environment scope that applies.

```
dotnet add package ConfigFerret.GitLab
```

```csharp
using ConfigFerret;
using ConfigFerret.GitLab;

var gitlab = await GitLabVariables.LoadAsync(new GitLabOptions
{
    Project = "contoso/platform/api",                            // or the numeric id
    Token = Environment.GetEnvironmentVariable("GITLAB_TOKEN"),  // never hard-code it
});

var report = ConfigTracer.For(builder.Configuration)
    .WithGitLab(gitlab, o =>
    {
        o.Environment = "production";                            // picks environment-scoped variables
        o.AddCiFile(".gitlab-ci.yml");
    })
    .Trace();

Console.WriteLine(report.Explain("Db:Timeout"));
// set in environment variable Db__Timeout from GitLab project variable DB_TIMEOUT
// (when a script line in .gitlab-ci.yml says `export Db__Timeout=$DB_TIMEOUT` before the app starts)
```

## Configure from appsettings and dependency injection

Put everything except the token in configuration, and register the source:

```json
// appsettings.json: no token here
{
  "ConfigFerret": {
    "GitLab": {
      "BaseAddress": "https://gitlab.example.com/",
      "Project": "contoso/platform/api",
      "Environment": "production",
      "CiFiles": [ ".gitlab-ci.yml" ],
      "Links": { "Db__Timeout": "DB_TIMEOUT" }
    }
  }
}
```

```csharp
builder.Services.AddConfigFerretGitLab(builder.Configuration.GetSection("ConfigFerret:GitLab"));
var app = builder.Build();

var tracer = ConfigTracer.For(builder.Configuration);
await tracer.WithGitLabAsync(app.Services);
Console.WriteLine(tracer.Trace().Explain("Db:Timeout"));
```

Supply the token from somewhere that is not committed:

```
dotnet user-secrets set "ConfigFerret:GitLab:Token" "<token>"   # development
export ConfigFerret__GitLab__Token="<token>"                    # CI, containers
```

Every property of `GitLabOptions` and `GitLabTraceOptions` binds from the section (`IncludeGroups`, `IncludeInstance`,
`IncludeMaskedValues`, `Environment`, `CiFiles`, `MatchByName`, `Links` and so on). A CI file in `CiFiles` that cannot
be read becomes a trace warning.

When the host starts, the options are checked, and startup fails with a message naming the key when:

- `Project` or `Token` is missing,
- `BaseAddress` is not HTTPS (or holds credentials),
- the token is in any `appsettings*.json` file, even one that another source overrides.

The messages never include the token.

## What it reads

- The project's variables (`/projects/:id/variables`).
- The variables of every group the project is in, outer group first (`/groups/:id/variables`). Turn this off with
  `IncludeGroups = false`.
- Instance variables (`/admin/ci/variables`), only with `IncludeInstance = true`, since they need an administrator.

Every list is read in pages of 100. Group or instance variables the token cannot read become a trace warning; project
variables that cannot be read fail the load.

## How layers are linked

- **Explicit links** (`Map("Db__Timeout", "DB_TIMEOUT")`).
- **CI files** (`AddCiFile`): every `NAME: $VAR` line outside a `variables:` block, and every script line
  `export NAME=$VAR` or `NAME=$VAR`, links `NAME` to `VAR`. `${VAR}` works too.
- **Same key** (on by default, `MatchByName`). GitLab keys are case-sensitive, so the match is too. These links are
  marked as inferred.

CI files also add their own variables, global and per job (`NAME: value`, or `NAME:` with `value:`). A variable whose
whole value is `$OTHER` is traced on to `OTHER`.

The steps follow GitLab's precedence, lowest first: CI file global variables, CI file job variables, instance
variables, group variables from the outermost group in, then project variables, so the last one wins. For each owner,
the variable whose environment scope fits `Environment` best is used: the exact name, then a wildcard such as
`review/*` (a longer pattern beats a shorter one), then `*`. With no `Environment`, only `*` variables apply, as for a
job with no environment. A key that is set only for scopes that do not apply becomes a trace warning. When a CI file
line or an explicit link ties a layer to such a key and it is masked, the layer is still marked sensitive.

## Tokens and secrets

- Use a personal, group or project access token with the `read_api` scope from a user with the **Maintainer** role:
  GitLab shows CI/CD variables only to maintainers. A job's `CI_JOB_TOKEN` cannot read variables.
- The token is sent only in the `PRIVATE-TOKEN` header, only over HTTPS, and redirects are not followed. It never
  appears in a report or an exception message.
- Masked and hidden variables are marked sensitive. GitLab never returns hidden values. It does return masked values,
  but ConfigFerret drops them unless `IncludeMaskedValues` is on; then they are only compared to say whether they
  differ, and are redacted like any other secret.
- Variables whose key looks like a secret's (`SMTP_PASSWORD`, by `TraceOptions.DefaultSensitiveKeyPattern`, whatever
  `SensitiveKeyPattern` is set to) are marked sensitive even when they are not masked, both in the settings and in CI
  files, so every layer they set is redacted.
- File variables (`variable_type: file`) are noted and marked sensitive, since GitLab cannot mask a value with several
  lines: the job gets the path of a temporary file, not the value, so the value is not compared. A layer that holds
  the path is then redacted too. Protected variables are noted.

## Limits

- CI YAML is read line by line, not with a full YAML parser: `include:`, `extends:`, anchors and `rules:` are not
  evaluated.
- Pipeline, trigger, schedule and manual variables are not read, though they win over everything else.
- At most 100 pages per list and 20 nested groups are read.
