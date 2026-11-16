# ConfigFerret.Azure

Adds Azure Key Vault secrets and Azure App Configuration key-values to
[ConfigFerret](https://github.com/Moonwalker1307/ConfigFerret) traces. A value read from an environment variable, a
Kubernetes env entry or Secret key, an External Secrets reference, or one of the Azure configuration providers is traced
back to the secret or key-value that set it.

```
dotnet add package ConfigFerret.Azure
```

```csharp
using ConfigFerret;
using ConfigFerret.Azure;

// Both sign in with DefaultAzureCredential unless you pass a TokenCredential.
var vault = await AzureKeyVaultSecrets.LoadAsync(new AzureKeyVaultOptions
{
    VaultUri = new("https://contoso-prod.vault.azure.net/"),
});
var store = await AzureAppConfigurationSettings.LoadAsync(new AzureAppConfigurationOptions
{
    Endpoint = new("https://contoso.azconfig.io"),
    Labels = { "base", "production" },
});

var report = ConfigTracer.For(builder.Configuration)
    .WithAzureAppConfiguration(store)
    .WithAzureKeyVault(vault)
    .Trace();

Console.WriteLine(report.Explain("Db:Password"));
```

To sign in another way, pass any `TokenCredential`:
`AzureKeyVaultSecrets.LoadAsync(options, new ManagedIdentityCredential(...))`. To use a proxy or a test transport, pass
`SecretClientOptions` or `ConfigurationClientOptions` too (for example `Transport = new HttpClientTransport(httpClient)`).

Both snapshots also work as generic sources: `vault.ToLoadedSource()` returns a `LoadedConfigSource`, and
`AzureKeyVaultSourceProvider` (`azure-key-vault`) and `AzureAppConfigurationSourceProvider` (`azure-app-configuration`)
describe the settings for hosts such as the desktop app.

## Configure from appsettings and dependency injection

Put the addresses and filters in configuration, and register the sources:

```json
// appsettings.json: no client secret or connection string here
{
  "ConfigFerret": {
    "AzureKeyVault": {
      "VaultUri": "https://contoso-prod.vault.azure.net/",
      "SecretNames": [ "Orders--*" ],
      "Links": { "DB_PASS": "orders-db-password" }
    },
    "AzureAppConfiguration": {
      "Endpoint": "https://contoso.azconfig.io",
      "KeyFilter": "Orders:*",
      "Labels": [ "base", "production" ],
      "TrimKeyPrefixes": [ "Orders:" ]
    }
  }
}
```

```csharp
builder.Services.AddConfigFerretAzureKeyVault(builder.Configuration.GetSection("ConfigFerret:AzureKeyVault"));
builder.Services.AddConfigFerretAzureAppConfiguration(builder.Configuration.GetSection("ConfigFerret:AzureAppConfiguration"));
var app = builder.Build();

var tracer = ConfigTracer.For(builder.Configuration);
await tracer.WithAzureAppConfigurationAsync(app.Services);
await tracer.WithAzureKeyVaultAsync(app.Services);
Console.WriteLine(tracer.Trace().Explain("Db:Password"));
```

The sources sign in with a `TokenCredential` registered in the container if there is one. Otherwise they use
`TenantId`, `ClientId` and `ClientSecret` from the section as a service principal, or, without a client secret,
`DefaultAzureCredential` (with `ClientId` as the managed or workload identity, when set). App Configuration can take a
`ConnectionString` with an access key instead of `Endpoint`.

Supply the client secret or connection string from somewhere that is not committed:

```
dotnet user-secrets set "ConfigFerret:AzureKeyVault:ClientSecret" "<secret>"                         # development
export ConfigFerret__AzureAppConfiguration__ConnectionString="Endpoint=...;Id=...;Secret=..."        # CI, containers
```

When the host starts, the options are checked, and startup fails with a message naming the key when:

- `VaultUri` is missing, or `Endpoint` and `ConnectionString` are both missing or both set,
- an address is not HTTPS (or holds credentials), or the connection string has no HTTPS `Endpoint=`,
- `ClientSecret` is set without `TenantId` and `ClientId`,
- the client secret or connection string is in any `appsettings*.json` file, even one that another source overrides.

The messages never include the secret.

## How layers are linked

Key Vault (`LayerKind.AzureKeyVault`):

- **External Secrets** (certain): an `ExternalSecretRef` whose provider is `azurekv` or unknown links to the secret named
  by its remote key (`secret/<name>` works too). A pinned version that is not the current one is noted.
- **App Configuration Key Vault references** (certain): a reference to `https://<vault>.vault.azure.net/secrets/<name>`
  links to that secret when both sources are added.
- **Explicit links** (certain): `Map("DB_PASS", "orders-db-password")` or `Links`. A link to a secret that is not in
  the snapshot adds a trace warning, and the value is still hidden.
- **Same name** (inferred, `MatchByName`): an environment variable, env entry or ConfigMap/Secret key with the secret's
  name, or with the same configuration key the Key Vault provider maps it to: `Db__Password`, `Db:Password` and
  `Db--Password` all match.
- **Key Vault configuration provider** (inferred, `MatchConfigurationProvider`): a value set by `AddAzureKeyVault` in
  the app links to the secret for its key. When no loaded secret maps to the key, for example because `SecretNames`
  left it out, or when `MatchConfigurationProvider` is off, the value is still hidden.

A linked secret that is disabled adds a trace warning that it cannot be read. One that is expired or not yet valid adds
a trace warning with the date: for secrets those dates are informational, so Key Vault still returns the secret and the
Key Vault configuration provider still loads it.

App Configuration (`LayerKind.AzureAppConfiguration`):

- **Same key** (inferred, `MatchByName`): `Db__Timeout` and `Db:Timeout` match the key-value `Db:Timeout`. App
  Configuration keys already use `:`.
- **App Configuration provider** (inferred, `MatchConfigurationProvider`): a value set by `AddAzureAppConfiguration`
  links to the key-values for its key.
- **Explicit links** (certain): `Map("TIMEOUT", "Orders:Db:Timeout")` or `Links`.

Each label in `Labels` is read in order, and a key with several labels lists them lowest priority first, so the last
label wins, as with the provider's `Select` calls. Feature flags (`.appconfig.featureflag/<name>`) map to
`FeatureManagement:<name>`. A Key Vault reference (content type
`application/vnd.microsoft.appconfig.keyvaultref+json`) is shown as sensitive, with the secret's URI in the note; the
secret is never read through App Configuration.

## Permissions

- Key Vault: listing needs the "List" secret permission (RBAC: Key Vault Secrets User covers both list and get; for
  names only, a custom role with `Microsoft.KeyVault/vaults/secrets/readMetadata/action`). `IncludeValues` also needs
  "Get". A value that cannot be read becomes a warning.
- App Configuration: App Configuration Data Reader, or a read-only access key in the connection string.

## Secrets

- Secret values are read only with `IncludeValues`, are always marked sensitive, and are redacted unless the report
  reveals sensitive values. A fetched value is compared with the value the app sees only to say that it differs.
- Tokens, client secrets and access keys stay in the Azure SDK's request headers and never appear in a report, warning
  or exception message from ConfigFerret.

## Limits

- Listing secrets does not return their version, so the version is known only with `IncludeValues`.
- A custom `KeyVaultSecretManager` that renames secrets, or an App Configuration key filter in the app that differs from
  `KeyFilter`, is not seen; use `Links` for those.
- Feature flags with filters are linked by name only; their filter settings are not traced.
- App Configuration snapshots are not read.
- Listing stops after 400 pages per vault or selector, with a warning.
