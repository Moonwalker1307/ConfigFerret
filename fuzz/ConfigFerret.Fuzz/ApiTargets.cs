using System.Net;
using System.Text;
using ConfigFerret.GitHub;
using ConfigFerret.Octopus;

namespace ConfigFerret.Fuzz;

/// <summary>
/// Fuzz targets for the GitHub and Octopus packages. The input is the server: its first byte picks the options, and the
/// rest holds one response per request, in the order the loader sends them, split by <c>%%</c> lines. A response may
/// start with a line such as <c>HTTP 404</c> to set its status. Loading may fail only with the exceptions the loaders
/// document, and nothing may ever put the credential into a message the library writes.
/// </summary>
internal static partial class Targets
{
    private const string Token = "fuzz-credential-7f3a";

    // A value no fuzzed input is likely to hold, so it shows up if a resolver copies the layer's value into a note.
    private const string LayerValue = "layer-value-q8w2";

    private static void GitHubApi(ReadOnlySpan<byte> data)
    {
        var flags = data.Length > 0 ? data[0] : (byte)0;
        var responses = Responses(data.Length > 0 ? data[1..] : data);
        var options = new GitHubVariablesOptions
        {
            Owner = "contoso",
            Repository = "api",
            Token = Token,
            IncludeOrganization = (flags & 1) != 0,
            IncludeSecrets = (flags & 2) != 0,
            Environment = (flags & 4) != 0 ? "production" : null,
        };

        using var server = new ScriptedServer(responses, """{"total_count":0,"variables":[],"secrets":[]}""");
        using var client = new HttpClient(server, disposeHandler: false);
        GitHubVariables variables;
        try
        {
            variables = GitHubVariables.LoadAsync(options, client).GetAwaiter().GetResult();
        }
        catch (HttpRequestException ex)
        {
            CheckNoCredential(ex.Message, server, "GitHub load error");
            return;
        }

        foreach (var warning in variables.Warnings)
        {
            CheckNoCredential(warning, server, "GitHub warning");
        }

        foreach (var entry in variables.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                throw new InvariantException("GitHubVariables kept an entry with no name.");
            }

            if (entry.Kind == GitHubVariableKind.Secret && entry.Value is not null)
            {
                throw new InvariantException($"GitHub secret {entry.Name} has a value.");
            }

            if (!variables.Find(entry.Name, entry.Kind).Contains(entry))
            {
                throw new InvariantException($"GitHubVariables.Find did not return {entry.Name}.");
            }
        }

        var traceOptions = new GitHubTraceOptions { MatchByName = (flags & 8) == 0 };
        foreach (var text in responses.Select(r => r.Body).Take(10))
        {
            traceOptions.AddWorkflowText(text, "fuzz.yml");
        }

        var resolver = new GitHubResolver(variables, traceOptions);
        var diagnostics = new TraceDiagnostics();
        var names = variables.Entries.Select(e => e.Name).Concat(traceOptions.Mappings.Select(m => m.Name)).Distinct(StringComparer.Ordinal).Take(100);
        foreach (var name in names)
        {
            foreach (var layer in NamedLayers(name))
            {
                foreach (var step in resolver.Resolve(layer, diagnostics))
                {
                    if (step.Kind != LayerKind.GitHubActions || step.Reference is not GitHubVariableRef reference)
                    {
                        throw new InvariantException($"GitHub step for {name} has kind {step.Kind} and reference {step.Reference}.");
                    }

                    if (reference.Kind == GitHubVariableKind.Secret && (!step.IsSensitive || step.Value is not null))
                    {
                        throw new InvariantException($"GitHub secret step for {name} is not sensitive or has a value.");
                    }

                    CheckNote(step, name);
                }
            }
        }
    }

    private static void OctopusApi(ReadOnlySpan<byte> data)
    {
        var flags = data.Length > 0 ? data[0] : (byte)0;
        var responses = Responses(data.Length > 0 ? data[1..] : data);
        var options = new OctopusVariablesOptions
        {
            ServerUri = new Uri("https://octopus.example.com/"),
            ApiKey = Token,
            Project = (flags & 1) != 0 ? "Projects-1" : "Api",
            Space = (flags & 6) switch { 2 => "Spaces-1", 4 => "Default", _ => null },
            IncludeLibraryVariableSets = (flags & 8) == 0,
        };

        using var server = new ScriptedServer(responses, "[]");
        using var client = new HttpClient(server, disposeHandler: false);
        OctopusVariables variables;
        try
        {
            variables = OctopusVariables.LoadAsync(options, client).GetAwaiter().GetResult();
        }
        catch (HttpRequestException ex)
        {
            CheckNoCredential(ex.Message, server, "Octopus load error");
            return;
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Octopus ", StringComparison.Ordinal))
        {
            // The space or project was not found.
            CheckNoCredential(ex.Message, server, "Octopus load error");
            return;
        }

        foreach (var warning in variables.Warnings)
        {
            CheckNoCredential(warning, server, "Octopus warning");
        }

        foreach (var entry in variables.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                throw new InvariantException("OctopusVariables kept an entry with no name.");
            }

            if (entry.IsSensitive && entry.Value is not null)
            {
                throw new InvariantException($"Sensitive Octopus variable {entry.Name} has a value.");
            }

            if (!variables.Find(entry.Name).Contains(entry))
            {
                throw new InvariantException($"OctopusVariables.Find did not return {entry.Name}.");
            }
        }

        var traceOptions = new OctopusTraceOptions
        {
            Environment = (flags & 16) != 0 ? "Production" : null,
            Machine = (flags & 32) != 0 ? "web-01" : null,
            Channel = (flags & 64) != 0 ? "Default" : null,
            MatchByName = (flags & 128) == 0,
        };
        traceOptions.Roles.Add("web");
        traceOptions.TenantTags.Add("Region/West");

        var resolver = new OctopusResolver(variables, traceOptions);
        var diagnostics = new TraceDiagnostics();
        foreach (var name in variables.Entries.Select(e => e.Name).Distinct(StringComparer.Ordinal).Take(100))
        {
            var layers = NamedLayers(name).Append(new ProvenanceStep
            {
                Kind = LayerKind.DotNetProvider,
                Source = "appsettings.json",
                Value = LayerValue,
                Reference = new FileRef("/app/appsettings.json"),
                ConfigurationKey = name,
            });
            foreach (var layer in layers)
            {
                foreach (var step in resolver.Resolve(layer, diagnostics))
                {
                    if (step.Kind != LayerKind.OctopusDeploy || step.Reference is not OctopusVariableRef)
                    {
                        throw new InvariantException($"Octopus step for {name} has kind {step.Kind} and reference {step.Reference}.");
                    }

                    if (step.IsSensitive && step.Value is not null)
                    {
                        throw new InvariantException($"Sensitive Octopus step for {name} has a value.");
                    }

                    CheckNote(step, name);
                }
            }
        }
    }

    // An environment variable, a Kubernetes env entry, a ConfigMap key and a Secret key, all named name.
    private static IEnumerable<ProvenanceStep> NamedLayers(string name) =>
    [
        Step(LayerKind.DotNetProvider, new EnvironmentVariableRef(name), LayerValue),
        Step(LayerKind.KubernetesWorkload, new WorkloadEnvRef("api", "app", name), LayerValue),
        Step(LayerKind.KubernetesConfigMap, new ConfigMapKeyRef("cm", name), LayerValue),
        Step(LayerKind.KubernetesSecret, new SecretKeyRef("secret", name), null),
    ];

    // Notes are not redacted, so they must never hold the layer's value or the credential.
    private static void CheckNote(ProvenanceStep step, string name)
    {
        if (step.Source is null)
        {
            throw new InvariantException($"A step for {name} has no source.");
        }

        if (step.Note is { } note && (note.Contains(LayerValue, StringComparison.Ordinal) || note.Contains(Token, StringComparison.Ordinal)))
        {
            throw new InvariantException($"The note on {step.Source} holds a value: {note}");
        }
    }

    // The credential may appear in a message only if the server itself sent it back in a response.
    private static void CheckNoCredential(string message, ScriptedServer server, string what)
    {
        if (message.Contains(Token, StringComparison.Ordinal) && !server.Echoed)
        {
            throw new InvariantException($"The {what} holds the credential: {message}");
        }

        if (server.Requests.Any(r => r.Contains(Token, StringComparison.Ordinal)))
        {
            throw new InvariantException("The credential was put into a request URL.");
        }
    }

    private static List<(HttpStatusCode Status, string Body)> Responses(ReadOnlySpan<byte> data)
    {
        var result = new List<(HttpStatusCode, string)>();
        foreach (var part in Split(data).Take(50))
        {
            var status = HttpStatusCode.OK;
            var body = part;
            if (part.StartsWith("HTTP ", StringComparison.Ordinal) && part.Length >= 8 && int.TryParse(part.AsSpan(5, 3), out var code) && code >= 100)
            {
                status = (HttpStatusCode)code;
                var newline = part.IndexOf('\n', StringComparison.Ordinal);
                body = newline < 0 ? string.Empty : part[(newline + 1)..];
            }

            result.Add((status, body));
        }

        return result;
    }

    /// <summary>Answers each request with the next scripted response, then with a fallback once they run out.</summary>
    private sealed class ScriptedServer(List<(HttpStatusCode Status, string Body)> responses, string fallback) : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _sent = [];
        private int _next;

        public List<string> Requests { get; } = [];

        public bool Echoed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            var (status, body) = _next < responses.Count ? responses[_next] : (HttpStatusCode.OK, fallback);
            _next++;
            Echoed |= body.Contains(Token, StringComparison.Ordinal);
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            _sent.Add(response);
            return Task.FromResult(response);
        }

        // The loaders dispose what they read, but the server owns what it sent, so it disposes it too.
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var response in _sent)
                {
                    response.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}
