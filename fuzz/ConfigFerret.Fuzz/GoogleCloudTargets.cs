using ConfigFerret.GoogleCloud;

namespace ConfigFerret.Fuzz;

/// <summary>
/// The fuzz target for the Google Cloud package, fed the same way as the GitHub and Octopus ones: the first byte picks the
/// options, and the rest holds one response per request, split by <c>%%</c> lines, each optionally starting with
/// <c>HTTP &lt;status&gt;</c>. Once the script runs out, the server answers every request with a body that is a secret
/// list, a version list and an access response at once, with a page token it repeats, so every run also ends paging.
/// </summary>
internal static partial class Targets
{
    private const int FuzzMaxSecrets = 100;

    private const string GcpFallback =
        """{"secrets":[{"name":"projects/1/secrets/S"}],"versions":[{"name":"projects/1/secrets/S/versions/1","state":"ENABLED"}],"payload":{"data":"eyJhIjoxfQ=="},"nextPageToken":"n"}""";

    private static void GcpSecrets(ReadOnlySpan<byte> data)
    {
        var flags = data.Length > 0 ? data[0] : (byte)0;
        var responses = Responses(data.Length > 0 ? data[1..] : data);
        var options = new GcpSecretManagerOptions
        {
            ProjectId = "contoso-prod",
            AccessToken = Token,
            IncludeVersions = (flags & 1) != 0,
            IncludeValues = (flags & 2) != 0,
            IncludeDisabled = (flags & 4) != 0,
            Location = (flags & 8) != 0 ? "europe-west1" : null,
            Prefix = (flags & 16) != 0 ? "Db" : null,
            Filter = (flags & 32) != 0 ? "labels.app=orders" : null,
            MaxSecrets = FuzzMaxSecrets,
        };
        var host = options.Location is null ? "secretmanager.googleapis.com" : "secretmanager.europe-west1.rep.googleapis.com";

        using var server = new ScriptedServer(responses, GcpFallback);
        using var client = new HttpClient(server, disposeHandler: false);
        GoogleCloud.GcpSecrets secrets;
        try
        {
            secrets = GoogleCloud.GcpSecrets.LoadAsync(options, client).GetAwaiter().GetResult();
        }
        catch (HttpRequestException ex)
        {
            CheckNoCredential(ex.Message, server, "Google Cloud load error");
            return;
        }

        CheckNoCredential(string.Empty, server, "Google Cloud request");
        foreach (var request in server.Requests)
        {
            var uri = new Uri(request);
            if (uri.Host != host || !uri.AbsolutePath.StartsWith("/v1/projects/", StringComparison.Ordinal))
            {
                throw new InvariantException($"Google Cloud sent a request outside the API: {request}");
            }
        }

        foreach (var warning in secrets.Warnings)
        {
            CheckNoCredential(warning, server, "Google Cloud warning");
        }

        if (secrets.Secrets.Count > FuzzMaxSecrets)
        {
            throw new InvariantException($"Google Cloud kept {secrets.Secrets.Count} secrets, more than MaxSecrets.");
        }

        foreach (var secret in secrets.Secrets)
        {
            if (string.IsNullOrEmpty(secret.Id) || secrets.Find(secret.Id) != secret)
            {
                throw new InvariantException($"GcpSecrets.Find did not return '{secret.Id}'.");
            }

            if (options.Prefix is { } prefix && !secret.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvariantException($"Google Cloud secret {secret.Id} does not start with the prefix.");
            }

            if (!options.IncludeValues && (secret.Value is not null || secret.ValueVersion is not null || secret.IsBinary))
            {
                throw new InvariantException($"Google Cloud secret {secret.Id} has a value although values were not asked for.");
            }

            if (!options.IncludeValues && !options.IncludeVersions && secret.Versions is not null)
            {
                throw new InvariantException($"Google Cloud secret {secret.Id} has versions although they were not asked for.");
            }

            if (!options.IncludeDisabled && secret.Versions is not null && secret.LatestEnabledVersion is null)
            {
                throw new InvariantException($"Google Cloud secret {secret.Id} has no enabled version although IncludeDisabled is off.");
            }

            if (secret.Value is { Length: >= 16 } value && !secret.Name.Contains(value, StringComparison.Ordinal) && secret.ToString().Contains(value, StringComparison.Ordinal))
            {
                throw new InvariantException($"GcpSecret.ToString shows the value of {secret.Id}.");
            }
        }

        var traceOptions = new GcpSecretManagerTraceOptions
        {
            MatchByName = (flags & 64) == 0,
            Separator = (flags & 128) != 0 ? "--" : null,
        };
        var resolver = new GcpSecretManagerResolver(secrets, traceOptions);
        var entries = resolver.Entries().ToList();
        var references = entries.Select(e => e.Reference).ToHashSet();
        if (entries.Count != secrets.Secrets.Count || entries.Any(e => !e.IsSensitive))
        {
            throw new InvariantException("Google Cloud entries do not match the secrets, or one is not sensitive.");
        }

        var diagnostics = new TraceDiagnostics();
        foreach (var secret in secrets.Secrets.Take(50))
        {
            var layers = NamedLayers(secret.Id)
                .Append(Step(LayerKind.KubernetesSecret, new ExternalSecretRef("gcpsm", "store", secret.Id, "a", "1"), null))
                .Append(Step(LayerKind.KubernetesSecret, new ExternalSecretRef(null, null, secret.Name + "/versions/latest"), null))
                .Append(new ProvenanceStep { Kind = LayerKind.DotNetProvider, Source = "Google Secret Manager", ConfigurationKey = secret.Id.Replace("__", ":", StringComparison.Ordinal), Value = LayerValue });
            foreach (var layer in layers)
            {
                foreach (var step in resolver.Resolve(layer, diagnostics))
                {
                    if (step.Kind != LayerKind.GcpSecretManager || step.Reference is not GcpSecretRef reference || !step.IsSensitive)
                    {
                        throw new InvariantException($"Google Cloud step {step.Source} has kind {step.Kind}, reference {step.Reference} or is not sensitive.");
                    }

                    if (!references.Contains(reference))
                    {
                        throw new InvariantException($"Google Cloud step {step.Source} has reference {reference}, which no entry has.");
                    }

                    if (!options.IncludeValues && step.Value is not null)
                    {
                        throw new InvariantException($"Google Cloud step {step.Source} has a value although values were not read.");
                    }

                    CheckNote(step, secret.Id);
                }
            }
        }

        foreach (var warning in diagnostics.Warnings)
        {
            CheckNoCredential(warning, server, "Google Cloud trace warning");
        }
    }
}
