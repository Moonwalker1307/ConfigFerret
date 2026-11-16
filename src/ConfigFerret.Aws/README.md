# ConfigFerret.Aws

Adds AWS Secrets Manager secrets and AWS Systems Manager Parameter Store parameters to
[ConfigFerret](https://github.com/Moonwalker1307/ConfigFerret) traces, so a value read from an environment variable, a
Kubernetes Secret filled by External Secrets Operator, or the AWS configuration providers is traced back to the secret
(or JSON property) or parameter that set it.

```
dotnet add package ConfigFerret.Aws
```

```csharp
using ConfigFerret;
using ConfigFerret.Aws;

// Signs in with the default AWS credential chain: environment, profile, SSO, container or instance role.
var secrets = await AwsSecrets.LoadAsync(new AwsSecretsManagerOptions
{
    Region = "eu-west-1",
    NamePrefix = "prod/orders/",   // optional: list only these
    IncludeValues = true,          // optional: read values, to see JSON properties and compare them
});
var parameters = await AwsParameters.LoadAsync(new AwsParameterStoreOptions
{
    Region = "eu-west-1",
    Path = "/orders/prod/",        // the path you pass to AddSystemsManager
});

var report = ConfigTracer.For(builder.Configuration)
    .WithAwsSecretsManager(secrets)
    .WithAwsParameterStore(parameters)
    .Trace();

Console.WriteLine(report.Explain("Db:Timeout"));
// ... from AWS Parameter Store parameter /orders/prod/Db/Timeout
```

Both sources also implement `ConfigFerret.Sources.IConfigSource`, and `AwsSecretsManagerProvider`
(`aws-secrets-manager`) and `AwsParameterStoreProvider` (`aws-parameter-store`) describe their settings to hosts such
as the desktop app and the command-line tool.

## Configure from appsettings and dependency injection

```json
// appsettings.json: no access keys here
{
  "ConfigFerret": {
    "AwsSecretsManager": {
      "Region": "eu-west-1",
      "NamePrefix": "prod/orders/",
      "Links": { "PG_PASS": "prod/orders/db#password" }
    },
    "AwsParameterStore": {
      "Region": "eu-west-1",
      "Path": "/orders/prod/"
    }
  }
}
```

```csharp
builder.Services.AddConfigFerretAwsSecretsManager(builder.Configuration.GetSection("ConfigFerret:AwsSecretsManager"));
builder.Services.AddConfigFerretAwsParameterStore(builder.Configuration.GetSection("ConfigFerret:AwsParameterStore"));
var app = builder.Build();

var tracer = ConfigTracer.For(builder.Configuration);
await tracer.WithAwsSecretsManagerAsync(app.Services);
await tracer.WithAwsParameterStoreAsync(app.Services);
Console.WriteLine(tracer.Trace().Explain("Db:Password"));
```

Every property of the options and trace options binds from the section. Sign-in, in order:

1. `Credentials`, an `AWSCredentials` you set from code (not bound from configuration).
2. `AccessKeyId` and `SecretAccessKey` (and `SessionToken`). Keep them out of appsettings files: use user secrets
   (`dotnet user-secrets set "ConfigFerret:AwsSecretsManager:SecretAccessKey" "..."`) or an environment variable
   (`ConfigFerret__AwsSecretsManager__SecretAccessKey`). Prefer a profile or a role.
3. `Profile`, a named profile from the shared config and credentials files. Its region is used when `Region` is not set.
4. Otherwise the default AWS credential chain, including `AWS_ACCESS_KEY_ID`, `AWS_PROFILE`, SSO, web identity and
   container or instance roles. The region then comes from `AWS_REGION` or the default profile.

`ServiceUrl` points at another endpoint, such as a VPC endpoint or LocalStack (`http://localhost:4566`). It must use
https, except on loopback.

When the host starts, the options are checked, and startup fails with a message naming the key when:

- `AccessKeyId` is set without `SecretAccessKey`, or the other way round, or `SessionToken` without both,
- `AccessKeyId`, `SecretAccessKey` or `SessionToken` is in any `appsettings*.json` file, even one another source
  overrides, since that file is usually committed,
- `ServiceUrl` is not https (or loopback http), or holds credentials,
- Parameter Store `Path` is missing or does not start with `/`.

The messages never include a credential.

## How layers are linked

Secrets Manager:

- **External Secrets Operator** (`ExternalSecretRef`, from the core's Kubernetes reader): a reference whose provider is
  `aws` or unknown, with a remote key that is a secret name, full ARN or partial ARN, links to that secret, or to the
  JSON `property` it picks (`a.b` and `a:b` both work). These links are certain.
- **Explicit links** (`Links`, or `Map("PG_PASS", "prod/orders/db", "password")`): from an environment variable,
  env entry, ConfigMap/Secret key or .NET key to a secret, with `#property` for one JSON property.
- **Same name** (`MatchByName`, on by default): an environment variable, env entry or ConfigMap/Secret key named like a
  secret, or whose .NET key matches the secret's .NET key: the prefix (`KeyPrefix`, or else `NamePrefix`) is removed and
  `/` and `__` become `:`, so `Db__Password` matches `prod/orders/Db/Password`, or property `Password` of
  `prod/orders/Db`. Marked as inferred.
- **Configuration providers** (`MatchConfigurationKeys`, on by default): a key set by Kralizek's `AddSecretsManager`
  or Amazon's `AddSystemsManager` links to the secret or JSON property that maps to it the same way. Marked as inferred.

Parameter Store:

- **External Secrets Operator**: a reference with provider `aws` or unknown and a key starting with `/` (or an ssm ARN)
  links to that parameter. Certain.
- **Explicit links** (`Links`, or `Map("TIMEOUT", "/orders/prod/Db/Timeout")`).
- **Same name** (`MatchByName`): an exact parameter name, or the .NET key: `Db__Timeout` matches
  `/orders/prod/Db/Timeout` under `/orders/prod/`. Marked as inferred.
- **`AddSystemsManager`** (`MatchConfigurationKeys`): its keys map the way `Amazon.Extensions.Configuration.SystemsManager`
  maps them: the path is removed, leading `/` trimmed and the other `/` become `:`. Each item of a `StringList` is
  also linked as `key:0`, `key:1` and so on. Marked as inferred.

When a value is known on both sides and differs, the step's note says so without printing either value.

## Permissions and secrets

- Secrets Manager needs `secretsmanager:ListSecrets`. With `IncludeValues`, also `secretsmanager:BatchGetSecretValue`
  and `secretsmanager:GetSecretValue` on the secrets (and `kms:Decrypt` for customer managed keys). Without
  `BatchGetSecretValue`, values are read one by one with `GetSecretValue`. A value that cannot be read becomes a warning.
- Parameter Store needs `ssm:GetParametersByPath` on the path, and `kms:Decrypt` to decrypt `SecureString` values.
- By default only names and metadata are read. Values are read only with `IncludeValues`, and secret values and
  `SecureString` values are always marked sensitive, so reports redact them and every layer in that chain. `String` and
  `StringList` values are read and are not secret, unless the last part of the name looks secret, such as
  `/orders/prod/smtp-password`.
- Requests are signed by the AWS SDK and never follow redirects. Credentials never appear in a report, a note or a
  warning.
- Responses are checked before the SDK reads them, because the SDK's JSON readers never finish on some malformed
  responses (a list sent as a plain value). Such a response fails with error code `InvalidResponseException`. Clients
  you pass to `LoadAsync` yourself are not checked.

## Limits

- Name and key matches are guesses, so they are marked `[inferred]`.
- Only the current version (`AWSCURRENT`) of a secret is read. A reference pinned to another version says so in its note.
- JSON secrets are flattened the way .NET configuration flattens JSON (`a:b`, `a:0`). When the whole JSON secret is
  linked, its properties still count as unused for `FindUnused`.
- Binary secrets show up by name only.
- Listing stops after 1000 pages of secrets or 5000 pages of parameters, with a warning.
