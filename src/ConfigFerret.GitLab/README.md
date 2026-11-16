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
  `export NAME=$VAR` or `NAME=$VAR`, links `NAME` to `VAR`. `${VAR}` works too. The report names a file by its path
  from the current directory (its full path when it is outside it), so CI files of one name in different folders are
  told apart.
- **Same key** (on by default, `MatchByName`). GitLab keys are case-sensitive, so the match is too. These links are
  marked as inferred.

CI files also add their own variables, global and per job (`NAME: value`, or `NAME:` with `value:`). A variable whose
whole value is `$OTHER` is traced on to `OTHER`, leaving out the CI file variables that are themselves `OTHER: $OTHER`,
since they cannot supply a value. A variable that refers to itself (`VERSION: $VERSION` in a job) is traced on to the
other `VERSION` variables, once: the ones in CI files are marked as inferred, since which one GitLab expands the
reference to is not certain, and project, group and instance variables are listed as usual.

The steps follow GitLab's precedence, lowest first: CI file global variables, CI file job variables, instance
variables, group variables from the outermost group in, then project variables, so the last one wins. For each owner,
the variable whose environment scope fits `Environment` best is used: the exact name, then a wildcard such as
`review/*` (a longer pattern beats a shorter one), then `*`. With no `Environment`, only `*` variables apply, as for a
job with no environment. A key that is set only for scopes that do not apply becomes a trace warning. When a CI file
line or an explicit link ties a layer to such a key and it is masked, the layer is still marked sensitive.

A job gets the global variables and its own, never another job's, and a CI file link counts for the job it is in.
What is set outside jobs, in hidden jobs (`.name`) and in `default:` counts for every job, since `extends:` is not
read. Variables from `workflow:` `rules:` apply only when their rule matches, which the trace cannot tell, so they and
the global variables they would replace are inferred alternatives, never certain. `Environment` also picks the jobs: a
job whose own `environment:` names another environment (`environment: staging`, `environment:` with `name: staging`,
or `environment: { name: staging }`) is left out. A hidden job never is, since a job that extends it can name another
environment. A job with no environment of its own, one that gets it through `extends:`, one whose environment name
has a variable in it (`review/$CI_COMMIT_REF_SLUG`), and one whose `environment:` is an alias, a tag such as
`!reference` or an anchor, which are not read, count for every environment. A variable followed from a job is looked
up in that job.

Once a name has a CI file link, every job that counts and may set it is followed: the links in jobs, in hidden jobs and
outside jobs, and the jobs that set the name in `variables:` without a link for it. When they come to different values,
every one of them is listed as an inferred step and a warning names the jobs; the same goes for jobs that set a name to
different values in `variables:`. A value that is a reference is compared by what it comes to in its own job (the same
CI file value or the same GitLab variable), whatever the variable's name. One the trace cannot compare, such as a masked
value, a value written in a variable named like a secret, `db-$SUFFIX` or a reference with several definitions, keeps
them alternatives, and the warning then says the values cannot be compared rather than that they differ. None of the
alternatives is then compared with the value seen here, and the one that comes to that value goes last (the tracer marks
the last step effective), also when it is reached through a whole-value reference such as `ALIAS: $TIMEOUT`. When any of
them may hold a secret, all of them are marked sensitive.

One trace step looks at no more than 1,000 CI file variables, the most upstream steps the tracer keeps for a setting.
It counts only the variables the job sees, and looks at them in the order they win, as the tracer keeps the end of a
chain: the job's own (every job's that counts, when the trace cannot tell which job), then the hidden jobs', then the
global ones, each from the end of the file. So what it keeps includes the one that wins. Past that, the rest is left
out, every step listed is marked inferred and sensitive, and a warning says that not all of them were followed.

## Tokens and secrets

- Use a personal, group or project access token with the `read_api` scope from a user with the **Maintainer** role:
  GitLab shows CI/CD variables only to maintainers. A job's `CI_JOB_TOKEN` cannot read variables.
- The token is sent only in the `PRIVATE-TOKEN` header, only over HTTPS, and redirects are not followed. It never
  appears in a report or an exception message. White space around it is left out. A token with a control character or
  a character outside ASCII in it (a zero-width space or byte order mark from a paste) cannot be sent in a header, so
  loading refuses it with an `ArgumentException` before any request.
- When GitLab refuses a request, the error gives the reason GitLab sends (for an OAuth-style answer, its `error` and
  `error_description`), cut short and without control characters, and leaves it out when it repeats the token or
  four of its characters in a row. Without a token GitLab answers a private project as if it did not exist, so the
  error says that no token was sent.
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
  evaluated. So a job's own variables win over a hidden job's, but two hidden jobs that set a name differently are
  alternatives, even when each job extends only one of them.
- Variables in a job's own `rules:` are read as the job's variables, as if their rule always matched.
- Pipeline, trigger, schedule and manual variables are not read, though they win over everything else.
- At most 100 pages per list and 20 nested groups are read.
