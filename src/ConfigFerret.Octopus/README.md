# ConfigFerret.Octopus

Adds Octopus Deploy variables to [ConfigFerret](https://github.com/Moonwalker1307/ConfigFerret) traces, so a value in
appsettings.json, an environment variable, a Kubernetes env entry or a ConfigMap key is traced back to the Octopus
project or library variable set variable that set it, with the scope that made it apply and, when the deployment
process is read, the step that replaced it.

```
dotnet add package ConfigFerret.Octopus
```

```csharp
using ConfigFerret;
using ConfigFerret.Octopus;

var octopus = await OctopusVariables.LoadAsync(new OctopusVariablesOptions
{
    ServerUri = new Uri("https://contoso.octopus.app/"),
    ApiKey = Environment.GetEnvironmentVariable("OCTOPUS_API_KEY")!,  // never hard-code it
    Project = "billing-api",                                        // name, slug or Projects-N
    Space = "Default",                                              // optional, name or Spaces-N
});

var report = ConfigTracer.For(builder.Configuration)
    .WithOctopus(octopus, o =>
    {
        o.Environment = "Production";
        o.Roles.Add("web");
    })
    .Trace();

Console.WriteLine(report.Explain("Db:Timeout"));
// set in appsettings.json from Octopus variable Db:Timeout
```

## Configure from appsettings and dependency injection

Put everything except the API key in configuration, and register the source:

```json
// appsettings.json: no API key here
{
  "ConfigFerret": {
    "Octopus": {
      "ServerUri": "https://contoso.octopus.app/",
      "Space": "Default",
      "Project": "billing-api",
      "Environment": "Production",
      "Roles": [ "web" ],
      "Links": { "DB__TIMEOUT": "Db:Timeout" }
    }
  }
}
```

```csharp
builder.Services.AddConfigFerretOctopus(builder.Configuration.GetSection("ConfigFerret:Octopus"));
var app = builder.Build();

var tracer = ConfigTracer.For(builder.Configuration);
await tracer.WithOctopusAsync(app.Services);
Console.WriteLine(tracer.Trace().Explain("Db:Timeout"));
```

Supply the API key from somewhere that is not committed:

```
dotnet user-secrets set "ConfigFerret:Octopus:ApiKey" "API-..."    # development
export ConfigFerret__Octopus__ApiKey="API-..."                     # CI, containers
```

or from a key vault configuration provider.

Every property of `OctopusVariablesOptions` and `OctopusTraceOptions` binds from the section (`Machine`, `Channel`,
`Step`, `TenantTags`, `IncludeLibraryVariableSets`, `ReadDeploymentProcess`, `Release`, `GitRef`,
`MatchConfigurationKeys`, `MatchSubstitutions`, `MatchByName` and so on).

When the host starts, the options are checked, and startup fails with a message naming the key when:

- `ServerUri`, `Project` or `ApiKey` is missing,
- `ServerUri` is not HTTPS (plain HTTP only to localhost or with `AllowUnencryptedHttp`) or holds credentials,
- the API key is in any `appsettings*.json` file, even one that another source overrides, since that file is usually
  committed. User secrets (`secrets.json`), environment variables and key vaults are fine.

The messages never include the API key.

Without a host, the same checks run the first time the options are read, on the configuration passed in: the root, or a
section that `GetSection` made, which is checked through the root it reads from. The root of a registered
`IConfiguration` is checked as well when it is another one. If the check finds no root in either, as for a section type
of your own with no root registered, an API key that is set fails with a message that says to register the root
(`services.AddSingleton<IConfiguration>(configuration)`). With or without a host, an API key served through a source
that hides where its values come from, such as a chained configuration that is neither a root nor a section `GetSection`
made, or a provider that keeps a section, fails too, with a message that names that source.

## How layers are linked

- **JSON and YAML keys** (on by default, `MatchConfigurationKeys`): a key in a JSON or YAML file is linked to the
  Octopus variable with the same name, for example `Db:Timeout`, the way Octopus structured configuration variables
  replace it.
- **Substitutions** (on by default, `MatchSubstitutions`): a value that holds `#{Name}`, as written in a file that
  Substitute Variables in Templates processes, is linked to the Octopus variable `Name`. Escaped `##{...}`, `#{if}`
  and other statements, and `Octopus.*` system variables are left out. When a value holds several, each is linked,
  and the last step is marked sensitive if any of them is.
- **Same name** (on by default, `MatchByName`): an environment variable, env entry or ConfigMap/Secret key with the
  same name as an Octopus variable.
- **Explicit links** (`Map("DB__TIMEOUT", "Db:Timeout")`).

Only variables whose scope fits the deployment are listed: set `Environment`, `Machine`, `Roles`, `Channel`, `Step`
and `TenantTags` to describe it. A variable scoped on something you did not set is kept but marked as inferred, with a
note naming the scope. The trace is taken to be of a deployment, so a loaded variable scoped only to runbooks is left
out, and one scoped to the deployment process applies; when a value left out that way is sensitive, the last value is
marked sensitive, since the configuration may have come from a runbook. Variables are ordered by how specific their
scope is (step, then target, then target tag, then tenant tag, then environment, then channel, then process), the
order of the [scope specificity](https://octopus.com/docs/projects/variables/getting-started) list in the Octopus
documentation, and a project variable wins over a library variable set one of equal scope, so the last one is the
value Octopus uses. How several scopes on one value combine is not documented; here the most specific one decides. Two
equally scoped values produce a warning.

## Deployment process steps

Set `ReadDeploymentProcess = true` to also read the project's deployment process, or `Release = "1.4.0"` to read the
variables and the process from the snapshot that release was created with, which is what its deployments used. For a
config-as-code project the process is read at `GitRef` (a branch such as `main` or a reference such as
`refs/tags/v1.4.0`), or at the project's default branch. The API key then also needs `ProcessView`; without it the
trace warns and links by name only.

With the process, a link says which step made it:

- A key is `replaced by structured configuration variables in step Deploy web (target appsettings.json)` when a step
  that runs in the deployment has the feature on and a target file pattern that names the file. That link is certain,
  not inferred. When no step targets the file, the note says so and the link stays inferred.
- `#{Name}` in a file is `substituted into this file in step Deploy web (target *.config)` when a step runs Substitute
  Variables in Templates on it.
- When exactly one step changes the file and `Step` is not set, variables scoped to that step apply.
- A step that comes from a step template is named with its template, and a template parameter set on that step is
  added after the variables with the same name, since that step uses it in their place. Sensitive parameters are
  shown by name only.
- A step that runs .NET configuration variables or XML configuration transforms on `.config` files produces a
  warning, since those files are not traced.

A step counts when it is enabled and fits the deployment you describe: its environments, excluded environments,
channels, target tags and, when `Step` is set, its name. Target patterns match the end of the file's path, with `*`,
`?` and `**`; a pattern built from variables (`#{...}`) may match any file, so its links stay inferred.

The CLI reads the process with `--octopus-process`, `--octopus-release` and `--octopus-git-ref`.

## API keys and sensitive values

- The API key needs permission to view the project and its variables (`VariableView`). A key for a service account
  with read-only access is best.
- The key is sent only in the `X-Octopus-ApiKey` header, only over HTTPS (plain HTTP only to localhost or with
  `AllowUnencryptedHttp`), and redirects are not followed. It never appears in a report or an exception message.
- When Octopus refuses a request, the error gives the reason Octopus sends, cut short and without control characters,
  and leaves it out when it repeats the API key or four of its characters in a row; the `API-` every key starts
  with does not count.
- Octopus never returns sensitive values, so sensitive variables show up by name only and are marked sensitive. The
  usual ConfigFerret redaction then hides the value of every layer in that chain, and values of keys that look secret
  are hidden even when Octopus does not mark them sensitive. When the scope does not settle which value applies (two
  equally scoped values, or a more specific one scoped on something that was not given) and one that may apply is
  sensitive, the last value is marked sensitive too.
- Octopus variable values that reference other variables (`#{Name}`) are shown as written, not expanded.

## Projects that keep variables in Git

For a version-controlled project whose variables were moved to Git, Octopus keeps only the sensitive ones in its
database. Only the variables stored in Octopus itself are read, not the `variables.ocl` file in the repository, and the
trace says so in a warning.
