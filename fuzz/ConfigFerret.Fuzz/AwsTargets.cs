using Amazon.Runtime;
using ConfigFerret.Aws;

namespace ConfigFerret.Fuzz;

/// <summary>
/// Fuzz targets for the AWS package. The input is AWS: its first byte picks the options, and the rest holds one JSON
/// response per request, in the order the loader sends them, split by <c>%%</c> lines, each optionally starting with
/// <c>HTTP &lt;status&gt;</c>. The real AWS SDK clients parse them. Loading may fail only with <see cref="AmazonServiceException"/> or <see cref="AmazonClientException"/>,
/// and nothing may put the secret access key into a message, or a secret value into a step that is not sensitive.
/// </summary>
internal static partial class Targets
{
    private static void AwsSecretsTarget(ReadOnlySpan<byte> data)
    {
        var flags = data.Length > 0 ? data[0] : (byte)0;
        var responses = Responses(data.Length > 0 ? data[1..] : data);
        var options = new AwsSecretsManagerOptions
        {
            IncludeValues = (flags & 1) != 0,
            NamePrefix = (flags & 2) != 0 ? "prod/" : null,
            Region = "eu-west-1",
            AccessKeyId = "AKIDFUZZ",
            SecretAccessKey = Token,
        };

        using var server = new ScriptedServer(responses, """{"SecretList":[]}""");
        using var client = AwsClients.CreateSecretsManager(options, server, NoRetries);
        AwsSecrets secrets;
        try
        {
            secrets = AwsSecrets.LoadAsync(options, client).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is AmazonClientException or AmazonServiceException)
        {
            CheckNoCredential(ex.ToString(), server, "AWS Secrets Manager load error");
            return;
        }

        foreach (var warning in secrets.Warnings)
        {
            CheckNoCredential(warning, server, "AWS Secrets Manager warning");
        }

        foreach (var secret in secrets.Entries)
        {
            if (string.IsNullOrEmpty(secret.Name) || secrets.Find(secret.Name) != secret)
            {
                throw new InvariantException($"AwsSecrets.Find did not return {secret.Name}.");
            }

            if (!options.IncludeValues && (secret.Value is not null || secret.ValueRead))
            {
                throw new InvariantException($"AWS secret {secret.Name} has a value although values were not asked for.");
            }
        }

        var traceOptions = new AwsSecretsTraceOptions
        {
            KeyPrefix = (flags & 4) != 0 ? "prod/orders/" : null,
            MatchByName = (flags & 8) == 0,
            MatchConfigurationKeys = (flags & 16) == 0,
        };
        var loaded = secrets.ToLoadedSource("fuzz", traceOptions);
        if (loaded.Entries.Any(e => !e.IsSensitive || e.Reference is not AwsSecretRef))
        {
            throw new InvariantException("An AWS secret entry is not sensitive or has the wrong reference.");
        }

        var resolver = loaded.Resolver;
        var diagnostics = new TraceDiagnostics();
        foreach (var secret in secrets.Entries.Take(50))
        {
            var properties = secret.Properties?.Keys.Take(20).ToList() ?? [];
            var key = AwsSecretsResolver.ToKey(secret.Name, traceOptions.KeyPrefix ?? secrets.NamePrefix) ?? secret.Name;
            var layers = NamedLayers(secret.Name)
                .Concat(NamedLayers(key.Replace(":", "__", StringComparison.Ordinal)))
                .Append(ExternalLayer(secret.Name, "aws", null))
                .Append(ExternalLayer(secret.Arn ?? secret.Name, null, properties.FirstOrDefault() ?? "missing"))
                .Append(ProviderLayer(key, "Kralizek.Extensions.Configuration.Internal.SecretsManagerConfigurationProvider"))
                .Concat(properties.Select(p => ExternalLayer(secret.Name, "aws", p.Replace(':', '.'))))
                .Concat(properties.Select(p => ProviderLayer($"{key}:{p}", "SecretsManagerConfigurationProvider")));
            foreach (var layer in layers)
            {
                foreach (var step in resolver.Resolve(layer, diagnostics))
                {
                    if (step.Kind != LayerKind.AwsSecretsManager || step.Reference is not AwsSecretRef || !step.IsSensitive)
                    {
                        throw new InvariantException($"AWS secret step for {secret.Name} has kind {step.Kind}, reference {step.Reference}, sensitive {step.IsSensitive}.");
                    }

                    if (step.Value is not null && !options.IncludeValues)
                    {
                        throw new InvariantException($"AWS secret step for {secret.Name} has a value although values were not read.");
                    }

                    CheckNote(step, secret.Name);
                }
            }
        }

        foreach (var warning in diagnostics.Warnings)
        {
            CheckNoCredential(warning, server, "AWS Secrets Manager trace warning");
        }
    }

    private static void AwsParametersTarget(ReadOnlySpan<byte> data)
    {
        var flags = data.Length > 0 ? data[0] : (byte)0;
        var responses = Responses(data.Length > 0 ? data[1..] : data);
        var options = new AwsParameterStoreOptions
        {
            Path = (flags & 1) != 0 ? "/orders/prod/" : "/orders/prod",
            Recursive = (flags & 2) == 0,
            IncludeValues = (flags & 4) != 0,
            Region = "eu-west-1",
            AccessKeyId = "AKIDFUZZ",
            SecretAccessKey = Token,
        };

        using var server = new ScriptedServer(responses, """{"Parameters":[]}""");
        using var client = AwsClients.CreateParameterStore(options, server, NoRetries);
        AwsParameters parameters;
        try
        {
            parameters = AwsParameters.LoadAsync(options, client).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is AmazonClientException or AmazonServiceException)
        {
            CheckNoCredential(ex.ToString(), server, "AWS Parameter Store load error");
            return;
        }

        foreach (var parameter in parameters.Entries)
        {
            if (string.IsNullOrEmpty(parameter.Name) || parameters.Find(parameter.Name) != parameter)
            {
                throw new InvariantException($"AwsParameters.Find did not return {parameter.Name}.");
            }

            if (parameter.IsSensitive && !options.IncludeValues && parameter.Value is not null)
            {
                throw new InvariantException($"AWS parameter {parameter.Name} kept a value that was not decrypted.");
            }
        }

        var traceOptions = new AwsParametersTraceOptions { MatchByName = (flags & 8) == 0, MatchConfigurationKeys = (flags & 16) == 0 };
        var loaded = parameters.ToLoadedSource("fuzz", traceOptions);
        var sensitive = parameters.Entries.Where(p => p.IsSensitive).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        if (loaded.Entries.Any(e => e.IsSensitive != sensitive.Contains(e.Name)))
        {
            throw new InvariantException("An AWS parameter entry has the wrong sensitivity.");
        }

        var resolver = loaded.Resolver;
        var diagnostics = new TraceDiagnostics();
        foreach (var parameter in parameters.Entries.Take(50))
        {
            var key = parameters.ToConfigurationKey(parameter);
            var layers = NamedLayers(parameter.Name)
                .Concat(NamedLayers(key.Replace(":", "__", StringComparison.Ordinal)))
                .Append(ExternalLayer(parameter.Name, "aws", null))
                .Append(ExternalLayer(parameter.Arn ?? parameter.Name, null, "a.b"))
                .Append(ProviderLayer(key, "Amazon.Extensions.Configuration.SystemsManager.SystemsManagerConfigurationProvider"))
                .Append(ProviderLayer(key + ":0", "SystemsManagerConfigurationProvider"));
            foreach (var layer in layers)
            {
                foreach (var step in resolver.Resolve(layer, diagnostics))
                {
                    if (step.Kind != LayerKind.AwsParameterStore || step.Reference is not AwsParameterRef reference)
                    {
                        throw new InvariantException($"AWS parameter step for {parameter.Name} has kind {step.Kind} and reference {step.Reference}.");
                    }

                    if (sensitive.Contains(reference.Name) != step.IsSensitive)
                    {
                        throw new InvariantException($"AWS parameter step for {reference.Name} has the wrong sensitivity.");
                    }

                    CheckNote(step, parameter.Name);
                }
            }
        }
    }

    // No retries, so error responses do not wait for a backoff. Clients are built the way ConfigFerret builds its own,
    // so responses go through its response guard.
    private static void NoRetries(ClientConfig config) => config.MaxErrorRetry = 0;

    private static ProvenanceStep ExternalLayer(string key, string? provider, string? property) =>
        Step(LayerKind.KubernetesSecret, new ExternalSecretRef(provider, "fuzz-store", key, property), null);

    private static ProvenanceStep ProviderLayer(string key, string source) => new()
    {
        Kind = LayerKind.DotNetProvider,
        Source = source,
        Value = LayerValue,
        ConfigurationKey = key,
    };
}
