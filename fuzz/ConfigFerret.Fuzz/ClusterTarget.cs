using System.Net;
using System.Text;
using ConfigFerret.Kubernetes;
using ConfigFerret.KubernetesCluster;
using k8s;

namespace ConfigFerret.Fuzz;

/// <summary>
/// The fuzz target for the ConfigFerret.KubernetesCluster package. The input is the API server, like the GitHub and
/// Octopus targets: its first byte picks the options, and the rest holds one JSON response per request, in the order
/// the loader sends them (the workload, the operator object lists, the ConfigMaps, then the Secrets), split by
/// <c>%%</c> lines, each optionally starting with <c>HTTP &lt;status&gt;</c>.
/// </summary>
internal static partial class Targets
{
    private static readonly string[] WorkloadKinds = ["deployment/api", "statefulset/api", "daemonset/api", "cronjob/api", "job/api", "pod/api", "api", "deploy/api"];

    private static void KubernetesClusterApi(ReadOnlySpan<byte> data)
    {
        var flags = data.Length > 0 ? data[0] : (byte)0;
        var responses = Responses(data.Length > 0 ? data[1..] : data);
        var options = new KubernetesClusterOptions
        {
            Workload = WorkloadKinds[(flags >> 1) & 7],
            IncludeSecrets = (flags & 1) != 0,
            Container = (flags & 16) != 0 ? "api" : null,
        };

        using var server = new ScriptedClusterServer(responses);
        var config = new KubernetesClientConfiguration { Host = "https://fuzz-cluster.example", AccessToken = Token };
        using var client = new k8s.Kubernetes(config, server);
        KubernetesClusterSnapshot cluster;
        try
        {
            cluster = KubernetesClusterReader.LoadAsync(client, "orders", "fuzz", WorkloadReference.Parse(options.Workload), options, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (InvalidOperationException ex)
        {
            CheckClusterMessage(ex.Message, server, "Kubernetes load error");
            return;
        }

        if (!options.IncludeSecrets && server.Paths.Any(p => p.Contains("/secrets/", StringComparison.Ordinal)))
        {
            throw new InvariantException("Secrets were read with IncludeSecrets off.");
        }

        foreach (var warning in cluster.Warnings)
        {
            CheckClusterMessage(warning, server, "Kubernetes warning");
        }

        foreach (var entry in cluster.Entries)
        {
            if (entry.IsSensitive != entry.Reference is SecretKeyRef)
            {
                throw new InvariantException($"Entry {entry.Name} is sensitive: {entry.IsSensitive}, but its reference is {entry.Reference}.");
            }
        }

        // Every Secret value read must stay out of the rendered report.
        var secretValues = cluster.Manifests.Secrets.Values.SelectMany(s => s.Data.Values).Select(v => v.Value)
            .Where(v => v is { Length: >= 6 }).Cast<string>().ToList();
        var report = ConfigTracer.ForContainer(cluster.Manifests, cluster.Workload.Name, cluster.Container).Trace();
        var text = report.ToText();
        var json = report.ToJson();
        foreach (var value in secretValues.Take(50))
        {
            // A ConfigMap or literal may hold the same text, which is not a leak.
            var elsewhere = cluster.Manifests.ConfigMaps.Values.Any(c => c.Data.Values.Any(v => v.Value?.Contains(value, StringComparison.Ordinal) == true))
                || cluster.Workload.Containers.Any(c => c.Env.Any(e => e.Source is EnvValueSource.Literal l && l.Value.Contains(value, StringComparison.Ordinal)))
                || server.Bodies.Any(b => !b.Contains("\"Secret\"", StringComparison.Ordinal) && b.Contains(value, StringComparison.Ordinal));
            if (!elsewhere && (text.Contains(value, StringComparison.Ordinal) || json.Contains(value, StringComparison.Ordinal)))
            {
                throw new InvariantException("A Secret value read from the cluster is in the report.");
            }
        }

        _ = cluster.ToLoadedSource().FindUnused(report);
    }

    private static void CheckClusterMessage(string message, ScriptedClusterServer server, string what)
    {
        if (message.Contains(Token, StringComparison.Ordinal) && !server.Echoed)
        {
            throw new InvariantException($"The {what} holds the credential: {message}");
        }
    }

    /// <summary>Answers each request with the next scripted response, then with a 404 Status once they run out.</summary>
    private sealed class ScriptedClusterServer(List<(HttpStatusCode Status, string Body)> responses) : DelegatingHandler
    {
        private readonly List<HttpResponseMessage> _sent = [];

        private const string NotFound = """{"kind":"Status","apiVersion":"v1","status":"Failure","reason":"NotFound","code":404}""";
        private int _next;

        public List<string> Paths { get; } = [];

        public List<string> Bodies { get; } = [];

        public bool Echoed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            var (status, body) = _next < responses.Count ? responses[_next] : (HttpStatusCode.NotFound, NotFound);
            _next++;
            Bodies.Add(body);
            Echoed |= body.Contains(Token, StringComparison.Ordinal);
            var response = new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            _sent.Add(response);
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _sent.ForEach(r => r.Dispose());
            }

            base.Dispose(disposing);
        }
    }
}
