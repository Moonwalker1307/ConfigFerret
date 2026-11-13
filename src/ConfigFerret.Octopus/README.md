# ConfigFerret.Octopus

Adds Octopus Deploy variables to [ConfigFerret](https://github.com/Moonwalker1307/ConfigFerret) traces, so a value in
appsettings.json, an environment variable, a Kubernetes env entry or a ConfigMap key is traced back to the Octopus
project or library variable set variable that set it, with the scope that made it apply.

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

## How layers are linked

- **JSON keys** (on by default, `MatchConfigurationKeys`): a key in a JSON file is linked to the Octopus variable with
  the same name, for example `Db:Timeout`, the way Octopus structured configuration variables replace it.
- **Same name** (on by default, `MatchByName`): an environment variable, env entry or ConfigMap/Secret key with the
  same name as an Octopus variable.
- **Explicit links** (`Map("DB__TIMEOUT", "Db:Timeout")`).

Only variables whose scope fits the deployment are listed: set `Environment`, `Machine`, `Roles`, `Channel`, `Step`
and `TenantTags` to describe it. A variable scoped on something you did not set is kept but marked as inferred, with a
note naming the scope. Variables are ordered by how specific their scope is (step, then target, then target tag, then
environment), and a project variable wins over a library variable set one of equal scope, so the last one is the
value Octopus uses. Where tenant tags and channels fall in that order is a best guess, and two equally scoped values
produce a warning.

## API keys and sensitive values

- The API key needs permission to view the project and its variables (`VariableView`). A key for a service account
  with read-only access is best.
- The key is sent only in the `X-Octopus-ApiKey` header, only over HTTPS (plain HTTP only to localhost or with
  `AllowUnencryptedHttp`), and redirects are not followed. It never appears in a report or an exception message.
- Octopus never returns sensitive values, so sensitive variables show up by name only and are marked sensitive. The
  usual ConfigFerret redaction then hides the value of every layer in that chain, and values of keys that look secret
  are hidden even when Octopus does not mark them sensitive.
- Values that reference other variables (`#{Name}`) are shown as written, not expanded.
