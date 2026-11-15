using ConfigFerret.HashiCorp;

namespace ConfigFerret.Fuzz;

/// <summary>
/// Fuzz targets for the HashiCorp package, fed the same way as the GitHub and Octopus ones: the first byte picks the
/// options, and the rest holds one response per request, split by <c>%%</c> lines, each optionally starting with
/// <c>HTTP &lt;status&gt;</c>. Once the script runs out, the Vault server answers every request with a folder that holds
/// another folder and a secret, so every run also hits the request and depth caps.
/// </summary>
internal static partial class Targets
{
    private const int FuzzMaxRequests = 200;
    private const int FuzzMaxKeys = 500;

    private static void Vault(ReadOnlySpan<byte> data)
    {
        var flags = data.Length > 0 ? data[0] : (byte)0;
        var responses = Responses(data.Length > 0 ? data[1..] : data);
        var options = new VaultOptions
        {
            Address = new Uri("https://vault.example.com:8200/"),
            Token = Token,
            IncludeValues = (flags & 1) != 0,
            Recursive = (flags & 2) == 0,
            KvVersion = ((flags >> 2) & 3) switch { 1 => 1, 2 => 2, _ => null },
            Namespace = (flags & 16) != 0 ? "admin" : null,
            MaxDepth = (flags & 128) != 0 ? 2 : 10,
            MaxRequests = FuzzMaxRequests,
            MaxSecrets = 100,
        };
        if ((flags & 32) != 0)
        {
            options.Paths.Add("orders");
            options.Paths.Add("orders/prod");
        }

        using var server = new ScriptedServer(responses, """{"data":{"keys":["a/","s"]}}""");
        using var client = new HttpClient(server, disposeHandler: false);
        VaultSecrets secrets;
        try
        {
            secrets = VaultSecrets.LoadAsync(options, client).GetAwaiter().GetResult();
        }
        catch (HttpRequestException ex)
        {
            CheckNoCredential(ex.Message, server, "Vault load error");
            return;
        }

        CheckNoCredential(string.Empty, server, "Vault request");
        if (server.Requests.Count > FuzzMaxRequests)
        {
            throw new InvariantException($"Vault sent {server.Requests.Count} requests, more than MaxRequests.");
        }

        foreach (var request in server.Requests)
        {
            var uri = new Uri(request);
            if (uri.Host != "vault.example.com" || !uri.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal))
            {
                throw new InvariantException($"Vault sent a request outside the API: {request}");
            }
        }

        foreach (var warning in secrets.Warnings)
        {
            CheckNoCredential(warning, server, "Vault warning");
        }

        if (secrets.Secrets.Count > options.MaxSecrets)
        {
            throw new InvariantException($"Vault kept {secrets.Secrets.Count} secrets, more than MaxSecrets.");
        }

        foreach (var secret in secrets.Secrets)
        {
            if (string.IsNullOrEmpty(secret.Path) || secrets.Find(secret.Path) != secret)
            {
                throw new InvariantException($"VaultSecrets.Find did not return '{secret.Path}'.");
            }

            if (!options.IncludeValues && secret.Data is not null)
            {
                throw new InvariantException($"Vault secret {secret.Path} has data although values were not asked for.");
            }
        }

        var traceOptions = new VaultTraceOptions { MatchByName = (flags & 64) == 0 };
        var resolver = new VaultResolver(secrets, traceOptions);
        var entries = resolver.Entries().ToList();
        var references = entries.Select(e => e.Reference).ToHashSet();
        if (entries.Any(e => !e.IsSensitive))
        {
            throw new InvariantException("A Vault entry is not sensitive.");
        }

        var diagnostics = new TraceDiagnostics();
        foreach (var secret in secrets.Secrets.Take(50))
        {
            var keys = secret.Data?.Keys.Take(10).ToList() ?? [];
            var names = keys.Select(k => (secret.RelativePath.Length == 0 ? k : secret.RelativePath.Replace('/', ':') + ":" + k).Replace(":", "__", StringComparison.Ordinal))
                .Append(secret.RelativePath.Replace("/", "__", StringComparison.Ordinal));
            var layers = names.Where(n => n.Length > 0).SelectMany(NamedLayers)
                .Append(Step(LayerKind.KubernetesSecret, new ExternalSecretRef("vault", "store", secret.Path, keys.FirstOrDefault()), null))
                .Append(Step(LayerKind.KubernetesSecret, new ExternalSecretRef(null, null, "secret/data/" + secret.Path), null));
            foreach (var layer in layers)
            {
                foreach (var step in resolver.Resolve(layer, diagnostics))
                {
                    if (step.Kind != LayerKind.HashiCorpVault || step.Reference is not VaultSecretRef reference || !step.IsSensitive)
                    {
                        throw new InvariantException($"Vault step {step.Source} has kind {step.Kind}, reference {step.Reference} or is not sensitive.");
                    }

                    if (!references.Contains(reference))
                    {
                        throw new InvariantException($"Vault step {step.Source} has reference {reference}, which no entry has.");
                    }

                    if (!options.IncludeValues && step.Value is not null)
                    {
                        throw new InvariantException($"Vault step {step.Source} has a value although values were not read.");
                    }

                    CheckNote(step, secret.Path);
                }
            }
        }

        foreach (var warning in diagnostics.Warnings)
        {
            CheckNoCredential(warning, server, "Vault trace warning");
        }
    }

    private static void Consul(ReadOnlySpan<byte> data)
    {
        var flags = data.Length > 0 ? data[0] : (byte)0;
        var responses = Responses(data.Length > 0 ? data[1..] : data);
        var options = new ConsulOptions
        {
            Address = new Uri("https://consul.example.com:8501/"),
            Token = Token,
            ExpandJson = (flags & 1) != 0,
            IsSensitive = (flags & 2) != 0,
            Datacenter = (flags & 4) != 0 ? "dc1" : null,
            Prefix = (flags & 8) != 0 ? string.Empty : "config/orders/",
            MaxKeys = FuzzMaxKeys,
        };

        using var server = new ScriptedServer(responses, "[]");
        using var client = new HttpClient(server, disposeHandler: false);
        ConsulKeyValues values;
        try
        {
            values = ConsulKeyValues.LoadAsync(options, client).GetAwaiter().GetResult();
        }
        catch (HttpRequestException ex)
        {
            CheckNoCredential(ex.Message, server, "Consul load error");
            return;
        }

        CheckNoCredential(string.Empty, server, "Consul request");
        foreach (var warning in values.Warnings)
        {
            CheckNoCredential(warning, server, "Consul warning");
        }

        if (values.Entries.Count > FuzzMaxKeys)
        {
            throw new InvariantException($"Consul kept {values.Entries.Count} keys, more than MaxKeys.");
        }

        foreach (var entry in values.Entries)
        {
            if (string.IsNullOrEmpty(entry.Key) || string.IsNullOrEmpty(entry.ConfigurationKey))
            {
                throw new InvariantException("ConsulKeyValues kept an entry with no key or no configuration key.");
            }

            if (!values.Find(entry.ConfigurationKey).Contains(entry))
            {
                throw new InvariantException($"ConsulKeyValues.Find did not return {entry.ConfigurationKey}.");
            }

            if (!options.ExpandJson && entry.JsonPath is not null)
            {
                throw new InvariantException($"Consul entry {entry.Key} was expanded although ExpandJson is off.");
            }
        }

        var resolver = new ConsulResolver(values, new ConsulTraceOptions { MatchByName = (flags & 16) == 0 });
        var references = resolver.Entries().Select(e => e.Reference).ToHashSet();
        var diagnostics = new TraceDiagnostics();
        foreach (var name in values.Entries.Select(e => e.ConfigurationKey.Replace(":", "__", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Take(100))
        {
            foreach (var step in NamedLayers(name).SelectMany(layer => resolver.Resolve(layer, diagnostics)))
            {
                if (step.Kind != LayerKind.Consul || step.Reference is not ConsulKeyRef reference || !references.Contains(reference))
                {
                    throw new InvariantException($"Consul step {step.Source} has kind {step.Kind} or reference {step.Reference}, which no entry has.");
                }

                if (step.IsSensitive != options.IsSensitive)
                {
                    throw new InvariantException($"Consul step {step.Source} is sensitive: {step.IsSensitive}.");
                }

                CheckNote(step, name);
            }
        }
    }
}
