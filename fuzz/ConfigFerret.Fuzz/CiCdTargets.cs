using System.Net;
using System.Text;
using ConfigFerret.AzureDevOps;
using ConfigFerret.GitLab;

namespace ConfigFerret.Fuzz;

/// <summary>
/// Fuzz targets for the Azure DevOps and GitLab packages, fed like the GitHub and Octopus ones: the first byte picks the
/// options, and the rest holds one response per request, split by <c>%%</c> lines. A response may start with a line such
/// as <c>HTTP 404</c> to set its status, then lines such as <c>&gt; X-Next-Page: 2</c> to set headers, since both APIs
/// page with headers. The same text is also read as pipeline or CI YAML.
/// </summary>
internal static partial class Targets
{
    private static void AzureDevOpsApi(ReadOnlySpan<byte> data)
    {
        var flags = data.Length > 0 ? data[0] : (byte)0;
        var responses = HeaderResponses(data.Length > 0 ? data[1..] : data);
        var options = new AzureDevOpsOptions
        {
            OrganizationUrl = new Uri("https://dev.azure.com/contoso/"),
            Project = "api",
        };
        if ((flags & 1) != 0)
        {
            options.GroupNames.Add("shared");
            options.GroupNames.Add("api-*");
        }

        if ((flags & 2) != 0)
        {
            options.AccessToken = Token;
        }
        else
        {
            options.PersonalAccessToken = Token;
        }

        using var server = new HeaderServer(responses, """{"count":0,"value":[]}""");
        using var client = new HttpClient(server, disposeHandler: false);
        AzureDevOpsVariableGroups groups;
        try
        {
            groups = AzureDevOpsVariableGroups.LoadAsync(options, client).GetAwaiter().GetResult();
        }
        catch (HttpRequestException ex)
        {
            server.Check(ex.Message, "Azure DevOps load error");
            return;
        }

        server.Check(string.Empty, "Azure DevOps request");
        var queries = Math.Max(1, options.GroupNames.Count);
        if (server.Requests.Count > queries * 100)
        {
            throw new InvariantException($"Azure DevOps sent {server.Requests.Count} requests, more than 100 pages per group name.");
        }

        foreach (var request in server.Requests.Select(r => new Uri(r)))
        {
            if (request.Host != "dev.azure.com" || request.AbsolutePath != "/contoso/api/_apis/distributedtask/variablegroups")
            {
                throw new InvariantException($"Azure DevOps sent a request outside the variable groups API: {request}");
            }
        }

        foreach (var warning in groups.Warnings)
        {
            server.Check(warning, "Azure DevOps warning");
        }

        if (groups.Groups.Select(g => g.Id).Distinct().Count() != groups.Groups.Count)
        {
            throw new InvariantException("AzureDevOpsVariableGroups kept a group twice.");
        }

        foreach (var variable in groups.Variables)
        {
            if (string.IsNullOrEmpty(variable.Name))
            {
                throw new InvariantException("AzureDevOpsVariableGroups kept a variable with no name.");
            }

            if (variable.IsSensitive && variable.Value is not null)
            {
                throw new InvariantException($"Azure DevOps secret {variable.Name} has a value.");
            }

            if (!groups.Find(variable.Name).Contains(variable) || !groups.FindByEnvironmentName(variable.EnvironmentName).Contains(variable))
            {
                throw new InvariantException($"AzureDevOpsVariableGroups.Find did not return {variable.Name}.");
            }
        }

        var traceOptions = new AzureDevOpsTraceOptions { MatchByName = (flags & 4) == 0 };
        foreach (var text in responses.Select(r => r.Body).Take(10))
        {
            traceOptions.AddPipelineText(text, "azure-pipelines.yml");
        }

        var resolver = new AzureDevOpsResolver(groups, traceOptions);
        var entries = resolver.Entries().Select(e => e.Reference).ToHashSet();
        var diagnostics = new TraceDiagnostics();
        var names = groups.Variables.SelectMany(v => new[] { v.Name, v.EnvironmentName })
            .Concat(traceOptions.Mappings.Select(m => m.Name))
            .Concat(traceOptions.Variables.Select(v => v.Name))
            .Distinct(StringComparer.Ordinal)
            .Take(100);
        var pending = new Queue<(ProvenanceStep Layer, int Depth)>(names.SelectMany(NamedLayers).Select(l => (l, 0)));
        while (pending.Count > 0)
        {
            var (layer, depth) = pending.Dequeue();
            foreach (var step in resolver.Resolve(layer, diagnostics))
            {
                if (step.Kind != LayerKind.AzureDevOps || step.Reference is not (AzureDevOpsVariableRef or AzurePipelinesVariableRef))
                {
                    throw new InvariantException($"Azure DevOps step has kind {step.Kind} and reference {step.Reference}.");
                }

                if (step.Reference is AzureDevOpsVariableRef && !entries.Contains(step.Reference))
                {
                    throw new InvariantException($"Azure DevOps step {step.Source} has a reference no entry has.");
                }

                if (step.IsSensitive && step.Value is not null)
                {
                    throw new InvariantException($"Sensitive Azure DevOps step {step.Source} has a value.");
                }

                server.CheckNote(step);
                if (step.Reference is AzurePipelinesVariableRef && depth < 3)
                {
                    pending.Enqueue((step, depth + 1));
                }
            }
        }

        foreach (var warning in diagnostics.Warnings)
        {
            server.Check(warning, "Azure DevOps trace warning");
        }
    }

    private static void GitLabApi(ReadOnlySpan<byte> data)
    {
        var flags = data.Length > 0 ? data[0] : (byte)0;
        var responses = HeaderResponses(data.Length > 0 ? data[1..] : data);
        var options = new GitLabOptions
        {
            Project = (flags & 64) != 0 ? "42" : "contoso/platform/api",
            Token = Token,
            IncludeInstance = (flags & 1) != 0,
            IncludeGroups = (flags & 2) == 0,
            IncludeMaskedValues = (flags & 4) != 0,
        };

        using var server = new HeaderServer(responses, "[]");
        using var client = new HttpClient(server, disposeHandler: false);
        GitLabVariables variables;
        try
        {
            variables = GitLabVariables.LoadAsync(options, client).GetAwaiter().GetResult();
        }
        catch (HttpRequestException ex)
        {
            server.Check(ex.Message, "GitLab load error");
            return;
        }

        server.Check(string.Empty, "GitLab request");

        // The project, up to 20 groups and the instance, at most 100 pages each.
        if (server.Requests.Count > 1 + (22 * 100))
        {
            throw new InvariantException($"GitLab sent {server.Requests.Count} requests.");
        }

        foreach (var request in server.Requests.Select(r => new Uri(r)))
        {
            if (request.Host != "gitlab.com" || !request.AbsolutePath.StartsWith("/api/v4/", StringComparison.Ordinal))
            {
                throw new InvariantException($"GitLab sent a request outside the API: {request}");
            }

            if (request.AbsolutePath.StartsWith("/api/v4/admin/", StringComparison.Ordinal) && !options.IncludeInstance)
            {
                throw new InvariantException("GitLab read instance variables without IncludeInstance.");
            }
        }

        foreach (var warning in variables.Warnings)
        {
            server.Check(warning, "GitLab warning");
        }

        foreach (var variable in variables.Entries)
        {
            if (string.IsNullOrEmpty(variable.Key) || string.IsNullOrEmpty(variable.EnvironmentScope))
            {
                throw new InvariantException("GitLabVariables kept a variable with no key or scope.");
            }

            if (variable.IsHidden && variable.Value is not null)
            {
                throw new InvariantException($"Hidden GitLab variable {variable.Key} has a value.");
            }

            if (variable.IsMasked && !options.IncludeMaskedValues && variable.Value is not null)
            {
                throw new InvariantException($"Masked GitLab variable {variable.Key} kept its value without IncludeMaskedValues.");
            }

            if (!variables.Find(variable.Key).Contains(variable))
            {
                throw new InvariantException($"GitLabVariables.Find did not return {variable.Key}.");
            }
        }

        var traceOptions = new GitLabTraceOptions
        {
            Environment = (flags & 24) switch { 8 => "production", 16 => "review/feature-1", 24 => string.Empty, _ => null },
            MatchByName = (flags & 32) == 0,
        };
        foreach (var text in responses.Select(r => r.Body).Take(10))
        {
            traceOptions.AddCiFileText(text, ".gitlab-ci.yml");
        }

        var resolver = new GitLabResolver(variables, traceOptions);
        var entries = resolver.Entries().Select(e => e.Reference).ToHashSet();
        var diagnostics = new TraceDiagnostics();
        var names = variables.Entries.Select(v => v.Key)
            .Concat(traceOptions.Mappings.Select(m => m.Name))
            .Concat(traceOptions.Variables.Select(v => v.Name))
            .Distinct(StringComparer.Ordinal)
            .Take(100);
        var pending = new Queue<(ProvenanceStep Layer, int Depth)>(names.SelectMany(NamedLayers).Select(l => (l, 0)));
        while (pending.Count > 0)
        {
            var (layer, depth) = pending.Dequeue();
            var chain = resolver.Resolve(layer, diagnostics);
            var owners = chain.Select(s => s.Reference).OfType<GitLabVariableRef>().Select(r => (r.Level, r.Owner)).ToList();
            if (owners.Distinct().Count() != owners.Count)
            {
                throw new InvariantException("The GitLab chain holds two variables from one owner.");
            }

            if (!owners.Select(o => o.Level).SequenceEqual(owners.Select(o => o.Level).Order()))
            {
                throw new InvariantException("The GitLab chain is not ordered instance, group, project.");
            }

            foreach (var step in chain)
            {
                if (step.Kind != LayerKind.GitLabCi || step.Reference is not (GitLabVariableRef or GitLabCiVariableRef))
                {
                    throw new InvariantException($"GitLab step has kind {step.Kind} and reference {step.Reference}.");
                }

                if (step.Reference is GitLabVariableRef reference)
                {
                    if (!entries.Contains(reference))
                    {
                        throw new InvariantException($"GitLab step {step.Source} has a reference no entry has.");
                    }

                    if (GitLabVariables.ScopeRank(reference.EnvironmentScope, traceOptions.Environment) == 0)
                    {
                        throw new InvariantException($"GitLab step {step.Source} has scope {reference.EnvironmentScope}, which does not apply.");
                    }
                }

                server.CheckNote(step);
                if (step.Reference is GitLabCiVariableRef && depth < 3)
                {
                    pending.Enqueue((step, depth + 1));
                }
            }
        }

        foreach (var warning in diagnostics.Warnings)
        {
            server.Check(warning, "GitLab trace warning");
        }
    }

    private static List<(HttpStatusCode Status, List<(string Name, string Value)> Headers, string Body)> HeaderResponses(ReadOnlySpan<byte> data)
    {
        var result = new List<(HttpStatusCode, List<(string, string)>, string)>();
        foreach (var (status, part) in Responses(data))
        {
            var headers = new List<(string, string)>();
            var body = part;
            while (body.StartsWith("> ", StringComparison.Ordinal))
            {
                var newline = body.IndexOf('\n', StringComparison.Ordinal);
                var line = newline < 0 ? body[2..] : body[2..newline];
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    headers.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
                }

                body = newline < 0 ? string.Empty : body[(newline + 1)..];
            }

            result.Add((status, headers, body));
        }

        return result;
    }

    /// <summary>Answers each request with the next scripted response and its headers, then with a fallback.</summary>
    private sealed class HeaderServer(List<(HttpStatusCode Status, List<(string Name, string Value)> Headers, string Body)> responses, string fallback) : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _sent = [];
        private int _next;

        public List<string> Requests { get; } = [];

        private bool Echoed { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            var (status, headers, body) = _next < responses.Count ? responses[_next] : (HttpStatusCode.OK, [], fallback);
            _next++;
            Echoed |= body.Contains(Token, StringComparison.Ordinal) || headers.Any(h => h.Value.Contains(Token, StringComparison.Ordinal));
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var (name, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            _sent.Add(response);
            return Task.FromResult(response);
        }

        // The credential may appear in a message only if the server itself sent it back.
        public void Check(string message, string what)
        {
            if (message.Contains(Token, StringComparison.Ordinal) && !Echoed)
            {
                throw new InvariantException($"The {what} holds the credential: {message}");
            }

            if (Requests.Any(r => r.Contains(Token, StringComparison.Ordinal)))
            {
                throw new InvariantException("The credential was put into a request URL.");
            }
        }

        // Notes are not redacted, so they must never hold the layer's value, or the credential unless the server sent it.
        public void CheckNote(ProvenanceStep step)
        {
            if (step.Note is { } note && (note.Contains(LayerValue, StringComparison.Ordinal) || (note.Contains(Token, StringComparison.Ordinal) && !Echoed)))
            {
                throw new InvariantException($"The note on {step.Source} holds a value: {note}");
            }
        }

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
