# ConfigFerret.GitHub

Adds GitHub Actions variables to [ConfigFerret](https://github.com/Moonwalker1307/ConfigFerret) traces, so a value
read from an environment variable, a Kubernetes env entry or a ConfigMap key is traced back to the organization,
repository or environment variable (or secret) that set it.

```
dotnet add package ConfigFerret.GitHub
```

```csharp
using ConfigFerret;
using ConfigFerret.GitHub;

var github = await GitHubVariables.LoadAsync(new GitHubVariablesOptions
{
    Owner = "contoso",
    Repository = "api",
    Environment = "production",                                  // optional
    Token = Environment.GetEnvironmentVariable("GITHUB_TOKEN"),  // never hard-code it
});

var report = ConfigTracer.For(builder.Configuration)
    .WithGitHub(github, o => o.AddWorkflow(".github/workflows/deploy.yml"))
    .Trace();

Console.WriteLine(report.Explain("Db:Timeout"));
// set in environment variable Db__Timeout from GitHub environment variable DB_TIMEOUT
```

## Configure from appsettings and dependency injection

Put everything except the token in configuration, and register the source:

```json
// appsettings.json: no token here
{
  "ConfigFerret": {
    "GitHub": {
      "Owner": "contoso",
      "Repository": "api",
      "Environment": "production",
      "Workflows": [ ".github/workflows/deploy.yml" ],
      "Links": { "Db__Timeout": "DB_TIMEOUT", "Db__Password": "secrets.DB_PASSWORD" }
    }
  }
}
```

```csharp
builder.Services.AddConfigFerretGitHub(builder.Configuration.GetSection("ConfigFerret:GitHub"));
var app = builder.Build();

var tracer = ConfigTracer.For(builder.Configuration);
await tracer.WithGitHubAsync(app.Services);
Console.WriteLine(tracer.Trace().Explain("Db:Timeout"));
```

Supply the token from somewhere that is not committed:

```
dotnet user-secrets set "ConfigFerret:GitHub:Token" "<token>"      # development
export ConfigFerret__GitHub__Token="<token>"                       # CI, containers
```

or from a key vault configuration provider. In a GitHub Actions workflow, map the built-in token:
`env: ConfigFerret__GitHub__Token: ${{ secrets.GITHUB_TOKEN }}`.

Every property of `GitHubVariablesOptions` and `GitHubTraceOptions` binds from the section (`ApiBaseAddress`,
`IncludeOrganization`, `IncludeSecrets`, `MatchByName` and so on). In `Links`, prefix a value with `secrets.` to link to
a secret. A workflow in `Workflows` that cannot be read becomes a trace warning.

When the host starts, the options are checked, and startup fails with a message naming the key when:

- `Owner`, `Repository` or `Token` is missing,
- `ApiBaseAddress` is not HTTPS (or holds credentials),
- the token is in any `appsettings*.json` file, even one that another source overrides, since that file is usually
  committed. User secrets (`secrets.json`), environment variables and key vaults are fine.

The messages never include the token. Without a host, the same checks run the first time the options are read.

## How layers are linked

- **Workflow files** (`AddWorkflow`): every `NAME: ${{ vars.X }}` or `NAME: ${{ secrets.X }}` line links `NAME` to `X`.
- **Explicit links** (`Map("Db__Timeout", "DB_TIMEOUT")`).
- **Same name** (on by default, `MatchByName`): an environment variable, env entry or ConfigMap/Secret key with the
  same name as a GitHub variable (or, failing that, a secret). These links are marked as inferred.

Organization, repository and environment entries are listed lowest priority first, the same order GitHub applies them,
so the environment one wins.

## Tokens and secrets

- The token needs read access to Actions variables (fine-grained: "Variables: read", plus "Secrets: read" for secret
  names and "Environments: read" for environments). In a workflow, `GITHUB_TOKEN` with `actions: read` works.
- The token is sent only in the `Authorization` header, only over HTTPS, and redirects are not followed. It never
  appears in a report or an exception message.
- GitHub never returns secret values, so secrets show up by name only and are marked sensitive. The usual ConfigFerret
  redaction then hides the value of every layer in that chain.
- Organization entries need an organization-owned repository; when they cannot be read, the trace carries a warning
  instead of failing.

For GitHub Enterprise Server, set `ApiBaseAddress` to `https://HOST/api/v3/`.
