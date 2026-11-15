using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ConfigFerret.Shared;

namespace ConfigFerret.AzureDevOps;

/// <summary>
/// A snapshot of the Azure Pipelines variable groups of one project. Load it with
/// <see cref="LoadAsync(AzureDevOpsOptions, CancellationToken)"/>, or build it from groups you already have, then pass it to
/// <see cref="AzureDevOpsTracerExtensions.WithAzureDevOps"/>.
/// </summary>
public sealed class AzureDevOpsVariableGroups
{
    private const string ApiVersion = "7.1";
    private const int PageSize = 100;

    // Stop after this many pages per query so a misbehaving server cannot keep a trace looping.
    private const int MaxPages = 100;

    /// <summary>Creates a snapshot from groups you already have.</summary>
    /// <param name="project">The project the groups belong to, for example <c>contoso/api</c>.</param>
    /// <param name="groups">The groups, lowest priority first.</param>
    /// <param name="warnings">Problems found while loading.</param>
    public AzureDevOpsVariableGroups(string project, IEnumerable<AzureDevOpsVariableGroup> groups, IEnumerable<string>? warnings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(groups);
        Project = project;
        Groups = [.. groups];
        Variables = [.. Groups.SelectMany(g => g.Variables)];
        Warnings = [.. warnings ?? []];
    }

    /// <summary>The project, for example <c>contoso/api</c>.</summary>
    public string Project { get; }

    /// <summary>The groups, in the order they were asked for (or by id when every group was read).</summary>
    public IReadOnlyList<AzureDevOpsVariableGroup> Groups { get; }

    /// <summary>Every variable of every group, in group order.</summary>
    public IReadOnlyList<AzureDevOpsVariable> Variables { get; }

    /// <summary>Problems found while loading, such as a group that was asked for but not found.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The variables named <paramref name="name"/> (compared case-insensitively, like Azure Pipelines does), in group order.</summary>
    public IReadOnlyList<AzureDevOpsVariable> Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Variables.Where(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// The variables a script would see as the environment variable <paramref name="environmentName"/>: those with that
    /// exact name, and those whose <see cref="AzureDevOpsVariable.EnvironmentName"/> it is, in group order.
    /// </summary>
    public IReadOnlyList<AzureDevOpsVariable> FindByEnvironmentName(string environmentName)
    {
        ArgumentNullException.ThrowIfNull(environmentName);
        return Variables.Where(v => Matches(v.Name, environmentName)).ToList();
    }

    /// <summary>True when the variable <paramref name="variableName"/> is the environment variable <paramref name="environmentName"/> in a script.</summary>
    internal static bool Matches(string variableName, string environmentName) =>
        string.Equals(variableName, environmentName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(ToEnvironmentName(variableName), environmentName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The environment variable name Azure Pipelines gives a variable: upper case, with <c>.</c> and spaces turned into
    /// <c>_</c>. For example <c>Db.Timeout</c> becomes <c>DB_TIMEOUT</c>.
    /// </summary>
    public static string ToEnvironmentName(string variableName)
    {
        ArgumentNullException.ThrowIfNull(variableName);
        return variableName.ToUpperInvariant().Replace('.', '_').Replace(' ', '_');
    }

    /// <summary>Reads the variable groups from the Azure DevOps REST API.</summary>
    public static async Task<AzureDevOpsVariableGroups> LoadAsync(AzureDevOpsOptions options, CancellationToken cancellationToken = default)
    {
        using var client = JsonApi.CreateClient();
        return await LoadAsync(options, client, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the variable groups from the Azure DevOps REST API with your own <see cref="HttpClient"/> (for a proxy or a
    /// test handler). The client should not follow redirects, so the token never reaches another host.
    /// </summary>
    /// <exception cref="ArgumentException">An option is missing or not valid.</exception>
    /// <exception cref="HttpRequestException">Azure DevOps could not be reached, refused the request or sent something unexpected.</exception>
    public static async Task<AzureDevOpsVariableGroups> LoadAsync(AzureDevOpsOptions options, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        var baseAddress = JsonApi.RequireSafeBaseAddress(options.OrganizationUrl, options.AllowUnencryptedHttp, nameof(options.OrganizationUrl));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Project);
        var authorization = Authorization(options);

        var project = options.Project.Trim();
        var projectName = $"{OrganizationName(baseAddress)}/{project}";
        var loader = new Loader(httpClient, new Uri(baseAddress, Uri.EscapeDataString(project) + "/_apis/distributedtask/variablegroups"), authorization);
        var names = options.GroupNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var groups = new List<AzureDevOpsVariableGroup>();
        var seen = new HashSet<int>();
        var warnings = new List<string>();
        if (names.Count == 0)
        {
            groups.AddRange(await loader.ReadAsync(null, cancellationToken).ConfigureAwait(false));
        }

        foreach (var name in names)
        {
            var found = await loader.ReadAsync(name, cancellationToken).ConfigureAwait(false);
            if (found.Count == 0 && !name.Contains('*', StringComparison.Ordinal))
            {
                warnings.Add($"Azure DevOps variable group {name} was not found in project {projectName}, or the token cannot read it.");
            }

            // A group that an earlier name or pattern found keeps its earlier place.
            groups.AddRange(found.Where(g => seen.Add(g.Id)));
        }

        return new AzureDevOpsVariableGroups(projectName, names.Count == 0 ? groups.DistinctBy(g => g.Id).OrderBy(g => g.Id) : groups, warnings);
    }

    /// <summary>The organization or collection name: the last path segment, or the first label of a <c>*.visualstudio.com</c> host.</summary>
    internal static string OrganizationName(Uri baseAddress)
    {
        var segments = baseAddress.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0)
        {
            return Uri.UnescapeDataString(segments[^1]);
        }

        var host = baseAddress.Host;
        var dot = host.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 && host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase) ? host[..dot] : host;
    }

    private static AuthenticationHeaderValue Authorization(AzureDevOpsOptions options)
    {
        var pat = string.IsNullOrWhiteSpace(options.PersonalAccessToken) ? null : options.PersonalAccessToken.Trim();
        var bearer = string.IsNullOrWhiteSpace(options.AccessToken) ? null : options.AccessToken.Trim();
        if (pat is not null && bearer is not null)
        {
            throw new ArgumentException("Set PersonalAccessToken or AccessToken, not both.", nameof(options));
        }

        if (pat is not null)
        {
            // Basic authentication with an empty user name, as Azure DevOps expects for a personal access token.
            return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + pat)));
        }

        return bearer is not null
            ? new AuthenticationHeaderValue("Bearer", bearer)
            : throw new ArgumentException("Set PersonalAccessToken or AccessToken: Azure DevOps does not list variable groups without signing in.", nameof(options));
    }

    private sealed class Loader(HttpClient client, Uri endpoint, AuthenticationHeaderValue authorization)
    {
        public async Task<List<AzureDevOpsVariableGroup>> ReadAsync(string? groupName, CancellationToken cancellationToken)
        {
            var groups = new List<AzureDevOpsVariableGroup>();
            string? continuation = null;
            for (var page = 0; page < MaxPages; page++)
            {
                var query = new StringBuilder("?");
                if (groupName is not null)
                {
                    query.Append("groupName=").Append(Uri.EscapeDataString(groupName)).Append('&');
                }

                query.Append(CultureInfo.InvariantCulture, $"queryOrder=IdAscending&$top={PageSize}");
                if (continuation is not null)
                {
                    query.Append("&continuationToken=").Append(Uri.EscapeDataString(continuation));
                }

                query.Append("&api-version=").Append(ApiVersion);
                var uri = new Uri(endpoint + query.ToString());
                string? next = null;
                using var doc = await JsonApi.GetAsync(client, uri, AddHeaders, "Azure DevOps", response => next = Inspect(response, uri), cancellationToken).ConfigureAwait(false);
                var items = JsonRead.Property(doc.RootElement, "value") is { ValueKind: JsonValueKind.Array } value
                    ? value
                    : throw new HttpRequestException($"Azure DevOps returned an unexpected response for {uri.AbsolutePath}: no 'value' array.");
                var count = 0;
                foreach (var item in items.EnumerateArray())
                {
                    count++;
                    if (ReadGroup(item) is { } group)
                    {
                        groups.Add(group);
                    }
                }

                if (count == 0 || string.IsNullOrEmpty(next) || next == continuation)
                {
                    break;
                }

                continuation = next;
            }

            return groups;
        }

        // Azure DevOps answers a token it does not accept with 203 and a sign-in page instead of 401.
        private static string? Inspect(HttpResponseMessage response, Uri uri)
        {
            if (response.StatusCode == HttpStatusCode.NonAuthoritativeInformation)
            {
                throw new HttpRequestException(
                    $"Azure DevOps returned 203 NonAuthoritativeInformation for {uri.AbsolutePath}: it sent a sign-in page, so the token was not accepted. Check that it is valid and has the Variable Groups (Read) scope.",
                    null,
                    HttpStatusCode.Unauthorized);
            }

            return response.Headers.TryGetValues("X-MS-ContinuationToken", out var values) ? values.FirstOrDefault()?.Trim() : null;
        }

        private void AddHeaders(HttpRequestHeaders headers)
        {
            headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            // Ask for 401 instead of a redirect to the sign-in page when the token is not accepted.
            headers.Add("X-TFS-FedAuthRedirect", "Suppress");
            headers.Authorization = authorization;
        }
    }

    internal static AzureDevOpsVariableGroup? ReadGroup(JsonElement item)
    {
        // Skip anything that is not a group with an id and a name, rather than fail the whole trace on it.
        var name = JsonRead.String(JsonRead.Property(item, "name"));
        if (string.IsNullOrWhiteSpace(name) || JsonRead.Number(JsonRead.Property(item, "id")) is not { } id || id is < int.MinValue or > int.MaxValue)
        {
            return null;
        }

        var typeText = JsonRead.String(JsonRead.Property(item, "type"));
        var type = typeText switch
        {
            null or "" => AzureDevOpsGroupType.Vsts,
            _ when string.Equals(typeText, "Vsts", StringComparison.OrdinalIgnoreCase) => AzureDevOpsGroupType.Vsts,
            _ when string.Equals(typeText, "AzureKeyVault", StringComparison.OrdinalIgnoreCase) => AzureDevOpsGroupType.AzureKeyVault,
            _ => AzureDevOpsGroupType.Other,
        };
        var vault = type == AzureDevOpsGroupType.AzureKeyVault && JsonRead.Object(item, "providerData") is { } provider
            ? JsonRead.String(JsonRead.Property(provider, "vault"))
            : null;
        if (string.IsNullOrWhiteSpace(vault))
        {
            vault = null;
        }

        var groupId = (int)id;
        var variables = JsonRead.Object(item, "variables") is { } map
            ? JsonRead.Properties(map)
                .Where(p => p.Name.Length > 0)
                .Select(p => ReadVariable(p.Name, p.Value, name, groupId, type, vault))
                .ToList()
            : [];
        return new AzureDevOpsVariableGroup(
            groupId,
            name,
            type,
            JsonRead.String(JsonRead.Property(item, "description")),
            vault,
            JsonRead.Date(JsonRead.Property(item, "modifiedOn")),
            variables);
    }

    private static AzureDevOpsVariable ReadVariable(string name, JsonElement value, string group, int groupId, AzureDevOpsGroupType type, string? vault)
    {
        var secret = JsonRead.Property(value, "isSecret") is { ValueKind: JsonValueKind.True };

        // Secret values are never returned; ignore one if a server ever sends it, and never keep a key vault value either.
        var text = secret || type == AzureDevOpsGroupType.AzureKeyVault ? null : JsonRead.String(JsonRead.Property(value, "value"));
        return new AzureDevOpsVariable(name, text, secret, group, groupId, type, vault);
    }
}
