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
