# ConfigFerret.GoogleCloud

Adds Google Cloud Secret Manager to [ConfigFerret](https://github.com/Moonwalker1307/ConfigFerret) traces, so a value
read from an environment variable, a Kubernetes env entry, a Secret key filled by External Secrets Operator or the
Secrets Store CSI driver, or a Secret Manager configuration provider is traced back to the secret that set it.

```
dotnet add package ConfigFerret.GoogleCloud
```

```csharp
using ConfigFerret;
using ConfigFerret.GoogleCloud;

var gcp = await GcpSecrets.LoadAsync(new GcpSecretManagerOptions
{
    ProjectId = "contoso-prod",
    Prefix = "orders-",          // optional: only secrets whose id starts with this
    IncludeVersions = true,      // optional: read each secret's version states
});

var report = ConfigTracer.For(builder.Configuration)
    .WithGcpSecretManager(gcp)
    .Trace();

Console.WriteLine(report.Explain("Db:Password"));
// set in environment variable Db__Password from Google Cloud Secret Manager secret orders-Db__Password (contoso-prod)
```

By default only names and metadata are read: labels, annotations, create time and replication. Set
`IncludeVersions` to read each secret's versions and their state (one request per secret), and `IncludeValues` to read
the latest enabled version's value. Values are always marked sensitive, so reports redact them.

## Sign in

The source signs in with the first of these that is set:

1. `AccessToken`: an OAuth access token, for example from `gcloud auth print-access-token`.
2. `Credential`: an `ICredential` you already have (from code only).
3. `CredentialsJson`: the content of a service account JSON key.
4. `CredentialsFile`: the path of a service account JSON key file.
5. Application Default Credentials: the `GOOGLE_APPLICATION_CREDENTIALS` file, `gcloud auth application-default login`,
   or the metadata server on Google Cloud (GKE Workload Identity, Cloud Run, Compute Engine).

`CredentialsJson` and `CredentialsFile` accept only `service_account` keys, because other credential configurations
can make the auth library run a command or call any URL. Use Application Default Credentials for other types.

The token is requested with the `cloud-platform` scope and sent only in the `Authorization` header, only over HTTPS,
and redirects are not followed. It never appears in a report or an exception message, and neither does the key.
When Secret Manager refuses a request, the error gives the reason Google Cloud sends, cut short and without control
characters, and leaves it out when it repeats the token or four of its characters in a row; the `ya29.` an access
token starts with does not count.

## Configure from appsettings and dependency injection

Put everything except the credential in configuration, and register the source:

```json
// appsettings.json: no access token or key content here
{
  "ConfigFerret": {
    "GcpSecretManager": {
      "ProjectId": "contoso-prod",
      "Location": "europe-west1",
      "Prefix": "orders-",
      "Filter": "labels.app=orders",
      "IncludeVersions": true,
      "StripPrefixes": [ "ORDERS_" ],
      "Links": { "Db__Password": "orders-db#password" }
    }
  }
}
```

```csharp
builder.Services.AddConfigFerretGcpSecretManager(builder.Configuration.GetSection("ConfigFerret:GcpSecretManager"));
var app = builder.Build();

var tracer = ConfigTracer.For(builder.Configuration);
await tracer.WithGcpSecretManagerAsync(app.Services);
Console.WriteLine(tracer.Trace().Explain("Db:Password"));
```

With no credential in configuration, Application Default Credentials are used. To use a token or key content instead,
supply it from somewhere that is not committed:

```
dotnet user-secrets set "ConfigFerret:GcpSecretManager:AccessToken" "$(gcloud auth print-access-token)"   # development
export ConfigFerret__GcpSecretManager__CredentialsJson="$(cat key.json)"                                    # CI, containers
```

A key file path (`CredentialsFile`) is not a secret and can go in appsettings.json.

When the host starts, the options are checked, and startup fails with a message naming the key when:

- `ProjectId` is missing,
- `Location` holds anything but lower-case letters, digits and dashes,
- `ApiBaseAddress` is not HTTPS (or holds credentials),
- `CredentialsFile` names a file that does not exist,
- `AccessToken` or `CredentialsJson` is in any `appsettings*.json` file, even one that another source overrides, since
  that file is usually committed. User secrets (`secrets.json`), environment variables and key vaults are fine.

Without a host, the same checks run the first time the options are read, on the configuration passed in: the root, or a
section that `GetSection` made, which is checked through the root it reads from. The root of a registered
`IConfiguration` is checked as well when it is another one. If the check finds no root in either, as for a section type
of your own with no root registered, a credential that is set fails with a message that says to register the root
(`services.AddSingleton<IConfiguration>(configuration)`). With or without a host, a credential served through a source
that hides where its values come from, such as a chained configuration that is neither a root nor a section `GetSection`
made, or a provider that keeps a section, fails too, with a message that names that source.

## How layers are linked

- **External secrets** (certain): an External Secrets Operator `ExternalSecret` whose store is `gcpsm` (or not
  loaded) is linked to the secret its `remoteRef.key` names, with `version` and `property` when given. A Secrets Store
  CSI `SecretProviderClass` with `provider: gcp` is linked by its `resourceName`,
  `projects/<project>/secrets/<secret>/versions/<version>`. The project may be the id or the number, and a regional
  name (`projects/<p>/locations/<l>/secrets/<s>`) matches only secrets read from that location.
- **Explicit links** (certain): `Map("DB_PASSWORD", "orders-db", "password")`, or `Links` in configuration.
- **Same name** (inferred, on by default, `MatchByName`): an environment variable, env entry or ConfigMap/Secret key
  whose configuration key matches the secret id the way .NET reads environment variables: `Db__Timeout` and
  `Db:Timeout` both match secret `Db__Timeout`. `Prefix` is stripped from secret ids first (`StripSecretPrefix`), and
  `StripPrefixes` from layer names. Set `Separator` to `--` for secrets named like Azure Key Vault ones (`Db--Timeout`).
  A configuration provider that reads Secret Manager directly is matched by its configuration key, when its name holds
  `Secret Manager`, `SecretManager` (as a type name does), `Google` or `GCP`.

A `property` is read from a JSON object value, by name or as a dotted path (`db.password`), when values are read.
When a reference pins a version, the note says whether that version is disabled or destroyed, or older than the latest
enabled one. Everything that is read as `latest` (a reference with no version or with `latest`, a match by name, an
explicit link, and a provider matched by its configuration key) gets a note and a trace warning when the newest version
is disabled or destroyed, because `latest` is the most recently created version and reading it fails. The value of an
older version is never compared as the latest: only the newest version's value counts, and not even that one when the
version is disabled or destroyed. When a value is read and differs from the layer's value, the note says so without
showing either.

## Permissions

- `roles/secretmanager.viewer` (`secretmanager.secrets.list`, `secretmanager.versions.list`) for names, metadata and
  version states.
- `roles/secretmanager.secretAccessor` (`secretmanager.versions.access`) as well, only for `IncludeValues`.

A secret whose versions or value cannot be read is kept with a trace warning instead of failing the load.

## Limits

- Secrets are read from one project, and from one location for regional secrets.
- `Prefix` is applied after listing. Use `Filter` to narrow the list on the server.
- Only the first 25 versions of each secret are read, plus the newest enabled one when it is older.
- A secret whose id breaks Secret Manager's rules (1 to 255 letters, digits, `_` and `-`), or whose project is neither a
  project number nor a project id (6 to 30 lower-case letters, digits and hyphens, or the old `example.com:my-project`
  form), or whose location is not lower-case letters, digits and dashes, and a version whose id is not a number, are
  left out of what the server sent. One warning for the listing gives how many secrets, never their names.
- Secrets with no enabled version are skipped when versions are read, unless `IncludeDisabled` is on.
- Only the latest enabled version's value is read. A reference pinned to another version is linked, but its value is
  not compared. Binary payloads are not compared either.
- Loading stops after `MaxSecrets` secrets (default 1000), with a warning. Secrets that are left out as described above
  count too.
