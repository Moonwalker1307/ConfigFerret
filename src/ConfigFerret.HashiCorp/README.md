# ConfigFerret.HashiCorp

Adds HashiCorp Vault KV secrets and Consul KV keys to [ConfigFerret](https://github.com/Moonwalker1307/ConfigFerret)
traces, so a value read from an environment variable, a Kubernetes env entry, a ConfigMap or Secret key, or an External
Secrets Operator or Secrets Store CSI reference is traced back to the Vault secret or Consul key that set it.

```
dotnet add package ConfigFerret.HashiCorp
```

It talks to the Vault and Consul HTTP APIs directly and has no third-party dependencies.

## Vault

```csharp
using ConfigFerret;
using ConfigFerret.HashiCorp;

var vault = await VaultSecrets.LoadAsync(new VaultOptions
{
    Address = new Uri("https://vault.example.com:8200/"),
    Token = Environment.GetEnvironmentVariable("VAULT_TOKEN"),   // never hard-code it
    Mount = "secret",                                           // the default
    Paths = { "orders/prod" },                                  // leave empty for the whole mount
});

var report = ConfigTracer.For(builder.Configuration)
    .WithVault(vault, o => o.StripPrefixes.Add("ORDERS_"))
    .Trace();

Console.WriteLine(report.Explain("Db:Password"));
// set in environment variable ORDERS_Db__Password from HashiCorp Vault secret secret/orders/prod/Db
```

`LoadAsync` lists each path (`LIST`, sent as `GET ...?list=true`) and, when `Recursive` is on (the default), the
folders below it, down to `MaxDepth` levels. For KV version 2 it reads each secret's metadata, which gives its current
version, its update time and whether the current version is deleted. It does not read secret data unless you set
`IncludeValues = true`.

The KV version is detected from `sys/mounts/<mount>`, then `sys/internal/ui/mounts/<mount>`. When the token can read
neither, version 2 is assumed and the trace carries a warning; set `KvVersion` to skip detection. Set `Namespace` for
Vault Enterprise; it is sent as `X-Vault-Namespace`.

### Values are off by default

Vault KV holds secrets, so by default only names and metadata are read. Then:

- layers are linked to whole secrets, and an External Secrets `property` is shown but not checked,
- KV version 1 secrets have no metadata, and a configured path that is not a folder is taken to be a secret.

With `IncludeValues = true`, each key inside a secret is read, so a layer is linked to the key and a different value is
noted ("the Vault value differs ..."; neither value is printed). Every Vault step is marked sensitive, so its value is
redacted in reports unless revealing is turned on.

### How layers are linked

- **External Secrets Operator and Secrets Store CSI** references (`ExternalSecretRef`) whose provider is `vault` or not
  known. `RemoteKey` is the path inside the mount; a CSI `secretPath` such as `secret/data/orders/prod/db` works too.
  `Property` picks the key, and a pinned `Version` that is not the current one is noted. These links are certain. When
  the provider is `vault` and the secret was not loaded, the trace carries a warning.
- **Explicit links** (`Map("DB_PASSWORD", "orders/prod/db", "password")`, or `Links` from configuration as
  `orders/prod/db#password`). These are certain.
- **Same name** (on by default, `MatchByName`), marked as inferred. A layer name is turned into a configuration key
  (`Db__Timeout` becomes `Db:Timeout`), after removing any of `StripPrefixes`, and compared with the key each Vault
  entry would set, relative to the configured path it was found under, the way Vault configuration providers map them:

  | Under path `orders/prod` | Sets |
  | --- | --- |
  | key `Db__Timeout` or `Db:Timeout` in secret `orders/prod` | `Db:Timeout` |
  | key `Timeout` in secret `orders/prod/Db` | `Db:Timeout` |
  | secret `orders/prod/Db/Timeout` (keys not read) | `Db:Timeout` |
  | secret `orders/prod/Db` (keys not read) | may set `Db:Timeout`, noted as such |

  A .NET configuration provider whose name contains `Vault` (for example VaultSharp.Extensions.Configuration) is
  matched by its configuration key.

### Token permissions

A policy like this is enough without values:

```hcl
path "secret/metadata/orders/prod/*" { capabilities = ["list", "read"] }
path "sys/mounts/secret"             { capabilities = ["read"] }   # optional, for detection
```

Add `path "secret/data/orders/prod/*" { capabilities = ["read"] }` for `IncludeValues`. For KV version 1 use
`path "secret/orders/prod/*" { capabilities = ["list"] }`, plus `read` for values. A folder or secret the token cannot
read becomes a trace warning; other errors fail the load with an `HttpRequestException`.

## Consul

```csharp
var consul = await ConsulKeyValues.LoadAsync(new ConsulOptions
{
    Address = new Uri("https://consul.example.com:8501/"),       // default http://localhost:8500/
    Token = Environment.GetEnvironmentVariable("CONSUL_HTTP_TOKEN"),
    Prefix = "config/orders/",
    ExpandJson = true,                                           // optional
});

var report = ConfigTracer.For(builder.Configuration).WithConsul(consul).Trace();
// set in environment variable Db__Timeout from Consul key config/orders/Db/Timeout
```

It reads `GET /v1/kv/<prefix>?recurse=true` (with `&dc=` when `Datacenter` is set) and decodes each base64 value. Like
Winton.Extensions.Configuration.Consul, it strips the prefix and turns `/` into `:`, so `config/orders/Db/Timeout` sets
`Db:Timeout`. With `ExpandJson`, a value that is a JSON object or array is expanded like the .NET JSON provider reads
it: `config/orders/Db` holding `{"Timeout": 30}` sets `Db:Timeout`, and the step notes the path inside the JSON.

Consul values are not treated as secret, unless the last part of the key (with its JSON path) looks secret, such as
`config/orders/smtp-password`. Set `IsSensitive = true` to redact them all. Layers are linked by explicit
`Links` (a full key, or one relative to the prefix, optionally `#Json:Path`), and by same name like Vault (a .NET
provider whose name contains `Consul` is matched by its configuration key). The token needs
`key_prefix "config/orders/" { policy = "read" }`. Without ACLs, leave `Token` null (and set `AllowNoToken` for
dependency injection).

## Configure from appsettings and dependency injection

```json
// appsettings.json: no tokens here
{
  "ConfigFerret": {
    "Vault": {
      "Address": "https://vault.example.com:8200/",
      "Mount": "secret",
      "Paths": [ "orders/prod" ],
      "StripPrefixes": [ "ORDERS_" ],
      "Links": { "DB_PASSWORD": "orders/prod/db#password" }
    },
    "Consul": { "Address": "https://consul.example.com:8501/", "Prefix": "config/orders/" }
  }
}
```

```csharp
builder.Services.AddConfigFerretVault(builder.Configuration.GetSection("ConfigFerret:Vault"));
builder.Services.AddConfigFerretConsul(builder.Configuration.GetSection("ConfigFerret:Consul"));
var app = builder.Build();

var tracer = ConfigTracer.For(builder.Configuration);
await tracer.WithVaultAsync(app.Services);
await tracer.WithConsulAsync(app.Services);
```

Supply the tokens from somewhere that is not committed:

```
dotnet user-secrets set "ConfigFerret:Vault:Token" "<token>"   # development
export ConfigFerret__Vault__Token="$VAULT_TOKEN"               # CI, containers
```

Startup fails with a message naming the key when the address or token is missing (Consul accepts no token with
`AllowNoToken`), the address is not HTTPS (plain http is allowed on loopback, or with `AllowUnencryptedHttp`), the
token is in any `appsettings*.json` file, or a value such as `KvVersion` is not valid. Messages never include the token.

Without a host, the same checks run the first time the options are read, on the configuration passed in: the root, or a
section that `GetSection` made, which is checked through the root it reads from. The root of a registered
`IConfiguration` is checked as well when it is another one. If the check finds no root in either, as for a section type
of your own with no root registered, a token that is set fails with a message that says to register the root
(`services.AddSingleton<IConfiguration>(configuration)`). With or without a host, a token served through a source that
hides where its values come from, such as a chained configuration that is neither a root nor a section `GetSection`
made, or a provider that keeps a section, fails too, with a message that names that source.

`VaultSource` and `ConsulSource` are also `IConfigSource`s, and `VaultSourceProvider` (`hashicorp-vault`) and
`ConsulSourceProvider` (`consul-kv`) describe their settings to hosts such as the desktop app and the tool. Their
`Token` settings are read from `VAULT_TOKEN` and `CONSUL_HTTP_TOKEN`.

## Tokens and limits

- Tokens are sent only in the `X-Vault-Token` and `X-Consul-Token` headers, and redirects are not followed. They
  never appear in a report, a warning or an exception message.
- Vault stops after `MaxRequests` requests (default 5000) or `MaxSecrets` secrets (default 2000), and lists at most
  `MaxDepth` folder levels (default 10), with a warning, so a large mount or a misbehaving server cannot keep a trace
  going. Keys that are not a single path segment (`..`, `a/b`, longer than 1024 characters) are skipped. A secret's
  keys past the first 1000 are skipped.
- Consul stops after `MaxKeys` keys (default 10000, counted after JSON expansion). Values that are not base64 UTF-8 text
  are skipped with a warning.
- Only KV secrets engines are read. Dynamic secrets (database, AWS and so on) are not.
