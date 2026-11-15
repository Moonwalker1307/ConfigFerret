using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ConfigFerret.Shared;

namespace ConfigFerret.GitLab;

/// <summary>
/// A snapshot of the GitLab CI/CD variables that apply to one project: its own, its groups' and, optionally, the
/// instance's. Load it with <see cref="LoadAsync(GitLabOptions, CancellationToken)"/>, or build it from entries you
/// already have, then pass it to <see cref="GitLabTracerExtensions.WithGitLab"/>.
/// </summary>
public sealed class GitLabVariables
{
    private const int PageSize = 100;

    // Stop after this many pages, and this many nested groups, so a misbehaving server cannot keep a trace looping.
    private const int MaxPages = 100;
    private const int MaxGroupDepth = 20;

    /// <summary>Creates a snapshot from entries you already have, for example from <c>glab variable list</c>.</summary>
    /// <param name="entries">The variables. Group variables must come outer group first.</param>
    /// <param name="warnings">Problems found while loading.</param>
    public GitLabVariables(IEnumerable<GitLabVariable> entries, IEnumerable<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Entries = [.. entries.OrderBy(e => e.Level)];
        Warnings = [.. warnings ?? []];
    }

    /// <summary>Every variable, instance first, then groups from the outermost in, then the project.</summary>
    public IReadOnlyList<GitLabVariable> Entries { get; }

    /// <summary>Problems found while loading, such as group variables the token could not read.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Every variable with the key <paramref name="key"/>, in every environment scope, lowest priority first.</summary>
    public IReadOnlyList<GitLabVariable> Find(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Entries.Where(e => string.Equals(e.Key, key, StringComparison.Ordinal)).ToList();
    }

    /// <summary>
    /// The variables with the key <paramref name="key"/> that a job deploying to <paramref name="environment"/> gets,
    /// lowest priority first: instance, then each group from the outermost in, then the project, so the last one is the
    /// value the job sees. Where one owner has several, the scope that fits best is used: the exact environment name,
    /// then a wildcard pattern such as <c>review/*</c>, then <c>*</c>. A job with no environment gets only <c>*</c>.
    /// </summary>
    public IReadOnlyList<GitLabVariable> Effective(string key, string? environment)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Find(key)
            .Select(v => (Variable: v, Rank: ScopeRank(v.EnvironmentScope, environment)))
            .Where(v => v.Rank > 0)
            .GroupBy(v => (v.Variable.Level, v.Variable.Owner))
            .Select(g => g.OrderByDescending(v => v.Rank).First().Variable)
            .ToList();
    }

    /// <summary>
    /// How well <paramref name="scope"/> fits <paramref name="environment"/>: 0 when it does not apply, 1 for <c>*</c>, and
    /// higher for a closer fit. An exact name beats any pattern, and a longer pattern beats a shorter one.
    /// </summary>
    internal static int ScopeRank(string? scope, string? environment)
    {
        scope = string.IsNullOrEmpty(scope) ? "*" : scope;
        if (scope == "*")
        {
            return 1;
        }

        if (string.IsNullOrEmpty(environment))
        {
            return 0;
        }

        if (string.Equals(scope, environment, StringComparison.Ordinal))
        {
            return int.MaxValue;
        }

        return scope.Contains('*', StringComparison.Ordinal) && Wildcard(scope, environment) ? 1 + scope.Length : 0;
    }

    // GitLab environment scopes use * for any run of characters, and nothing else is special.
    private static bool Wildcard(string pattern, string text)
    {
        var parts = pattern.Split('*');
        if (!text.StartsWith(parts[0], StringComparison.Ordinal))
        {
            return false;
        }

        var position = parts[0].Length;
        for (var i = 1; i < parts.Length - 1; i++)
        {
            var found = text.IndexOf(parts[i], position, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            position = found + parts[i].Length;
        }

        var last = parts[^1];
        return text.Length - position >= last.Length && text.EndsWith(last, StringComparison.Ordinal);
    }

    /// <summary>Reads the variables from the GitLab REST API.</summary>
    public static async Task<GitLabVariables> LoadAsync(GitLabOptions options, CancellationToken cancellationToken = default)
    {
        using var client = JsonApi.CreateClient();
        return await LoadAsync(options, client, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the variables from the GitLab REST API with your own <see cref="HttpClient"/> (for a proxy or a test
    /// handler). The client should not follow redirects, so the token never reaches another host.
    /// </summary>
    /// <exception cref="ArgumentException">An option is missing or not valid.</exception>
    /// <exception cref="HttpRequestException">GitLab could not be reached, refused the request or sent something unexpected.</exception>
    public static async Task<GitLabVariables> LoadAsync(GitLabOptions options, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Project);
        var baseAddress = JsonApi.RequireSafeBaseAddress(options.BaseAddress, options.AllowUnencryptedHttp, nameof(options.BaseAddress));
        var loader = new Loader(httpClient, new Uri(baseAddress, "api/v4/"), options.Token, options.IncludeMaskedValues);
        var entries = new List<GitLabVariable>();
        var warnings = new List<string>();

        var project = await loader.ReadProjectAsync(options.Project.Trim(), cancellationToken).ConfigureAwait(false);
        if (options.IncludeInstance)
        {
            await loader.TryReadAsync(entries, warnings, "admin/ci/variables", GitLabVariableLevel.Instance, baseAddress.Authority, cancellationToken).ConfigureAwait(false);
        }

        if (options.IncludeGroups && project.GroupPath is { } groupPath)
        {
            // Every ancestor group, outermost first: contoso, then contoso/platform.
            var parts = groupPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = Math.Max(0, parts.Length - MaxGroupDepth); i < parts.Length; i++)
            {
                var path = string.Join('/', parts[..(i + 1)]);
                await loader.TryReadAsync(entries, warnings, $"groups/{Uri.EscapeDataString(path)}/variables", GitLabVariableLevel.Group, path, cancellationToken).ConfigureAwait(false);
            }
        }

        await loader.ReadAsync(entries, string.Create(CultureInfo.InvariantCulture, $"projects/{project.Id}/variables"), GitLabVariableLevel.Project, project.Path, cancellationToken).ConfigureAwait(false);
        return new GitLabVariables(entries, warnings);
    }

    internal static string Describe(GitLabVariableLevel level) => level switch
    {
        GitLabVariableLevel.Instance => "instance",
        GitLabVariableLevel.Group => "group",
        _ => "project",
    };

    private sealed record ProjectInfo(long Id, string Path, string? GroupPath);

    private sealed class Loader(HttpClient client, Uri api, string? token, bool includeMaskedValues)
    {
        public async Task<ProjectInfo> ReadProjectAsync(string project, CancellationToken cancellationToken)
        {
            var uri = new Uri(api, "projects/" + Uri.EscapeDataString(project));
            using var doc = await JsonApi.GetAsync(client, uri, AddHeaders, "GitLab", cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            if (JsonRead.Number(JsonRead.Property(root, "id")) is not { } id)
            {
                throw new HttpRequestException($"GitLab returned an unexpected response for {uri.AbsolutePath}: no project id.");
            }

            var path = JsonRead.String(JsonRead.Property(root, "path_with_namespace"));
            var ns = JsonRead.Object(root, "namespace");
            var kind = ns is { } n ? JsonRead.String(JsonRead.Property(n, "kind")) : null;
            var groupPath = ns is { } g && string.Equals(kind, "group", StringComparison.Ordinal) ? JsonRead.String(JsonRead.Property(g, "full_path")) : null;
            return new ProjectInfo(id, string.IsNullOrWhiteSpace(path) ? project : path, string.IsNullOrWhiteSpace(groupPath) ? null : groupPath);
        }

        public async Task TryReadAsync(List<GitLabVariable> into, List<string> warnings, string path, GitLabVariableLevel level, string owner, CancellationToken cancellationToken)
        {
            var before = into.Count;
            try
            {
                await ReadAsync(into, path, level, owner, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                // Group variables need the Maintainer role on the group, instance variables an administrator.
                into.RemoveRange(before, into.Count - before);
                warnings.Add($"Skipped GitLab {Describe(level)} variables of {owner}: {ex.Message}");
            }
        }

        public async Task ReadAsync(List<GitLabVariable> into, string path, GitLabVariableLevel level, string owner, CancellationToken cancellationToken)
        {
            for (var page = 1; page <= MaxPages;)
            {
                var uri = new Uri(api, string.Create(CultureInfo.InvariantCulture, $"{path}?per_page={PageSize}&page={page}"));
                int? next = null;
                var hasHeader = false;
                using var doc = await JsonApi.GetAsync(client, uri, AddHeaders, "GitLab", response => (hasHeader, next) = NextPage(response), cancellationToken).ConfigureAwait(false);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array)
                {
                    throw new HttpRequestException($"GitLab returned an unexpected response for {uri.AbsolutePath}: not an array of variables.");
                }

                var count = 0;
                foreach (var item in root.EnumerateArray())
                {
                    count++;
                    if (ReadVariable(item, level, owner, includeMaskedValues) is { } variable)
                    {
                        into.Add(variable);
                    }
                }

                // GitLab sends x-next-page, empty on the last page. Without it, a short page is the last one.
                if (count == 0 || (hasHeader ? next is not { } n || n <= page : count < PageSize))
                {
                    return;
                }

                page = hasHeader ? next!.Value : page + 1;
            }
        }

        private static (bool HasHeader, int? Next) NextPage(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("X-Next-Page", out var values))
            {
                return (false, null);
            }

            var text = values.FirstOrDefault()?.Trim();
            return (true, int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var next) ? next : null);
        }

        private void AddHeaders(HttpRequestHeaders headers)
        {
            headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(token))
            {
                headers.Add("PRIVATE-TOKEN", token);
            }
        }
    }

    internal static GitLabVariable? ReadVariable(JsonElement item, GitLabVariableLevel level, string owner, bool includeMaskedValues)
    {
        // Skip anything that is not a variable with a key, rather than fail the whole trace on it.
        var key = JsonRead.String(JsonRead.Property(item, "key"));
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        var masked = JsonRead.Property(item, "masked") is { ValueKind: JsonValueKind.True };
        var hidden = JsonRead.Property(item, "hidden") is { ValueKind: JsonValueKind.True };
        var scope = JsonRead.String(JsonRead.Property(item, "environment_scope"));
        var value = hidden || (masked && !includeMaskedValues) ? null : JsonRead.String(JsonRead.Property(item, "value"));
        return new GitLabVariable(
            key,
            value,
            level,
            owner,
            string.IsNullOrEmpty(scope) ? "*" : scope,
            masked,
            hidden,
            JsonRead.Property(item, "protected") is { ValueKind: JsonValueKind.True },
            string.Equals(JsonRead.String(JsonRead.Property(item, "variable_type")), "file", StringComparison.Ordinal),
            JsonRead.String(JsonRead.Property(item, "description")));
    }
}
