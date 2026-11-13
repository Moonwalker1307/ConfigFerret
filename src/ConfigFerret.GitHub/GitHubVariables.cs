using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ConfigFerret.Shared;

namespace ConfigFerret.GitHub;

/// <summary>
/// A snapshot of the GitHub Actions variables and secret names that apply to one repository and, optionally, one
/// deployment environment. Load it with <see cref="LoadAsync(GitHubVariablesOptions, CancellationToken)"/>, or build
/// it from entries you already have, then pass it to <see cref="GitHubTracerExtensions.WithGitHub"/>.
/// </summary>
public sealed class GitHubVariables
{
    // The variables endpoints allow at most 30 per page, the secrets endpoints 100.
    private const int VariablesPageSize = 30;
    private const int SecretsPageSize = 100;

    // Stop after this many pages so a misbehaving server cannot keep a trace looping.
    private const int MaxPages = 100;

    private readonly Dictionary<string, List<GitHubVariable>> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a snapshot from entries you already have, for example from <c>gh variable list --json</c>.</summary>
    public GitHubVariables(IEnumerable<GitHubVariable> entries, IEnumerable<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Entries = [.. entries.OrderBy(e => e.Scope)];
        foreach (var entry in Entries)
        {
            if (!_byName.TryGetValue(entry.Name, out var list))
            {
                _byName[entry.Name] = list = [];
            }

            list.Add(entry);
        }

        Warnings = [.. warnings ?? []];
    }

    /// <summary>Every variable and secret, organization first, then repository, then environment.</summary>
    public IReadOnlyList<GitHubVariable> Entries { get; }

    /// <summary>Problems found while loading, such as organization variables the token could not read.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// Returns the entries named <paramref name="name"/> of one kind, lowest priority first
    /// (organization, repository, environment), so the last one is the value a workflow sees.
    /// </summary>
    public IReadOnlyList<GitHubVariable> Find(string name, GitHubVariableKind kind) =>
        _byName.TryGetValue(name, out var list) ? list.Where(e => e.Kind == kind).ToList() : [];

    /// <summary>Reads the variables and secret names from the GitHub REST API.</summary>
    public static async Task<GitHubVariables> LoadAsync(GitHubVariablesOptions options, CancellationToken cancellationToken = default)
    {
        using var client = JsonApi.CreateClient();
        return await LoadAsync(options, client, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the variables and secret names from the GitHub REST API with your own <see cref="HttpClient"/>
    /// (for a proxy or a test handler). The client should not follow redirects, so the token never reaches another host.
    /// </summary>
    public static async Task<GitHubVariables> LoadAsync(GitHubVariablesOptions options, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Repository);
        var baseAddress = JsonApi.RequireSafeBaseAddress(options.ApiBaseAddress, options.AllowUnencryptedHttp, nameof(options.ApiBaseAddress));

        var loader = new Loader(httpClient, baseAddress, options.Token);
        var repo = $"repos/{Escape(options.Owner)}/{Escape(options.Repository)}";
        var repoName = $"{options.Owner}/{options.Repository}";
        var entries = new List<GitHubVariable>();
        var warnings = new List<string>();

        if (options.IncludeOrganization)
        {
            // These list only the organization entries shared with this repository, and need no organization-level permission.
            await loader.TryReadAsync(entries, warnings, $"{repo}/actions/organization-variables", GitHubVariableKind.Variable, GitHubVariableScope.Organization, options.Owner, cancellationToken).ConfigureAwait(false);
            if (options.IncludeSecrets)
            {
                await loader.TryReadAsync(entries, warnings, $"{repo}/actions/organization-secrets", GitHubVariableKind.Secret, GitHubVariableScope.Organization, options.Owner, cancellationToken).ConfigureAwait(false);
            }
        }

        await loader.ReadAsync(entries, $"{repo}/actions/variables", GitHubVariableKind.Variable, GitHubVariableScope.Repository, repoName, cancellationToken).ConfigureAwait(false);
        if (options.IncludeSecrets)
        {
            await loader.TryReadAsync(entries, warnings, $"{repo}/actions/secrets", GitHubVariableKind.Secret, GitHubVariableScope.Repository, repoName, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(options.Environment))
        {
            var env = $"{repo}/environments/{Escape(options.Environment)}";
            await loader.ReadAsync(entries, $"{env}/variables", GitHubVariableKind.Variable, GitHubVariableScope.Environment, options.Environment, cancellationToken).ConfigureAwait(false);
            if (options.IncludeSecrets)
            {
                await loader.TryReadAsync(entries, warnings, $"{env}/secrets", GitHubVariableKind.Secret, GitHubVariableScope.Environment, options.Environment, cancellationToken).ConfigureAwait(false);
            }
        }

        return new GitHubVariables(entries, warnings);
    }

    private static string Escape(string segment) => Uri.EscapeDataString(segment.Trim());

    private sealed class Loader(HttpClient client, Uri baseAddress, string? token)
    {
        public async Task TryReadAsync(
            List<GitHubVariable> into,
            List<string> warnings,
            string path,
            GitHubVariableKind kind,
            GitHubVariableScope scope,
            string owner,
            CancellationToken cancellationToken)
        {
            try
            {
                await ReadAsync(into, path, kind, scope, owner, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                // Organization entries do not exist for a user-owned repository, and secret names need an extra permission.
                warnings.Add($"Skipped GitHub {Describe(scope)} {(kind == GitHubVariableKind.Secret ? "secrets" : "variables")}: {ex.Message}");
            }
        }

        public async Task ReadAsync(
            List<GitHubVariable> into,
            string path,
            GitHubVariableKind kind,
            GitHubVariableScope scope,
            string owner,
            CancellationToken cancellationToken)
        {
            var pageSize = kind == GitHubVariableKind.Variable ? VariablesPageSize : SecretsPageSize;
            var arrayName = kind == GitHubVariableKind.Variable ? "variables" : "secrets";
            var read = 0;
            for (var page = 1; page <= MaxPages; page++)
            {
                var uri = new Uri(baseAddress, string.Create(CultureInfo.InvariantCulture, $"{path}?per_page={pageSize}&page={page}"));
                using var doc = await JsonApi.GetAsync(client, uri, AddHeaders, "GitHub", cancellationToken).ConfigureAwait(false);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(arrayName, out var items) || items.ValueKind != JsonValueKind.Array)
                {
                    throw new HttpRequestException($"GitHub returned an unexpected response for {uri.AbsolutePath}: no '{arrayName}' array.");
                }

                var total = root.TryGetProperty("total_count", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var n) ? n : (int?)null;
                var count = 0;
                foreach (var item in items.EnumerateArray())
                {
                    count++;

                    // Skip anything that is not an entry with a name, rather than fail the whole trace on it.
                    var name = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                        ? nameValue.GetString()
                        : null;
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    // Secrets never carry a value; ignore one if a server ever sends it.
                    var value = kind == GitHubVariableKind.Variable && item.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    DateTimeOffset? updated = item.TryGetProperty("updated_at", out var u) && u.ValueKind == JsonValueKind.String && u.TryGetDateTimeOffset(out var d) ? d : null;
                    into.Add(new GitHubVariable(name, value, kind, scope, owner, updated));
                }

                read += count;
                if (count == 0 || count < pageSize || (total is not null && read >= total))
                {
                    return;
                }
            }
        }

        private void AddHeaders(HttpRequestHeaders headers)
        {
            headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (!string.IsNullOrEmpty(token))
            {
                headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }
    }

    internal static string Describe(GitHubVariableScope scope) => scope switch
    {
        GitHubVariableScope.Organization => "organization",
        GitHubVariableScope.Repository => "repository",
        _ => "environment",
    };
}
