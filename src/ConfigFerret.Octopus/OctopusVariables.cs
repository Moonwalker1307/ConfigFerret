using System.Text.Json;
using System.Text.RegularExpressions;
using ConfigFerret.Shared;

namespace ConfigFerret.Octopus;

/// <summary>
/// A snapshot of the variables of one Octopus Deploy project and the library variable sets it includes. Load it with
/// <see cref="LoadAsync(OctopusVariablesOptions, CancellationToken)"/>, or build it from entries you already have, then
/// pass it to <see cref="OctopusTracerExtensions.WithOctopus"/>.
/// </summary>
public sealed partial class OctopusVariables
{
    // ScopeValues lists use plural names; Scope uses singular ones.
    private static readonly Dictionary<string, string> ScopeValueLists = new(StringComparer.Ordinal)
    {
        ["Environments"] = "Environment",
        ["Machines"] = "Machine",
        ["Roles"] = "Role",
        ["Actions"] = "Action",
        ["Channels"] = "Channel",
        ["TenantTags"] = "TenantTag",
        ["Processes"] = "ProcessOwner",
    };

    // A project can be named by id, slug or display name; an id match wins over a slug, a slug over a name.
    private static readonly string[] ProjectLookupFields = ["Id", "Slug", "Name"];

    private readonly Dictionary<string, List<OctopusVariable>> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a snapshot from entries you already have.</summary>
    public OctopusVariables(IEnumerable<OctopusVariable> entries, IEnumerable<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Entries = [.. entries];
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

    /// <summary>Every variable, project variables first, then each library variable set.</summary>
    public IReadOnlyList<OctopusVariable> Entries { get; }

    /// <summary>Problems found while loading.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Returns every variable named <paramref name="name"/>, whatever its scope.</summary>
    public IReadOnlyList<OctopusVariable> Find(string name) => _byName.TryGetValue(name, out var list) ? list : [];

    /// <summary>Reads the project's variables from the Octopus REST API.</summary>
    public static async Task<OctopusVariables> LoadAsync(OctopusVariablesOptions options, CancellationToken cancellationToken = default)
    {
        using var client = JsonApi.CreateClient();
        return await LoadAsync(options, client, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the project's variables from the Octopus REST API with your own <see cref="HttpClient"/> (for a proxy or a
    /// test handler). The client should not follow redirects, so the API key never reaches another host.
    /// </summary>
    public static async Task<OctopusVariables> LoadAsync(OctopusVariablesOptions options, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Project);
        var baseAddress = JsonApi.RequireSafeBaseAddress(options.ServerUri, options.AllowUnencryptedHttp, nameof(options.ServerUri));
        var api = new Api(httpClient, baseAddress, options.ApiKey);

        var spaceId = await FindSpaceAsync(api, options.Space, cancellationToken).ConfigureAwait(false);
        var space = $"api/{Uri.EscapeDataString(spaceId)}";

        using var projects = await api.GetAsync($"{space}/projects/all", cancellationToken).ConfigureAwait(false);
        var project = FindByIdSlugOrName(projects.RootElement, options.Project)
            ?? throw new InvalidOperationException($"Octopus project '{options.Project}' was not found in space {spaceId}, or the API key cannot see it.");
        var projectName = GetString(project, "Name") ?? options.Project;

        var entries = new List<OctopusVariable>();
        var warnings = new List<string>();
        await ReadSetAsync(api, space, GetString(project, "VariableSetId"), projectName, isProject: true, entries, cancellationToken).ConfigureAwait(false);

        if (options.IncludeLibraryVariableSets && project.TryGetProperty("IncludedLibraryVariableSetIds", out var included) && included.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in included.EnumerateArray().Select(AsString).Where(s => !string.IsNullOrEmpty(s)))
            {
                using var set = await api.GetAsync($"{space}/libraryvariablesets/{Uri.EscapeDataString(id!)}", cancellationToken).ConfigureAwait(false);
                var setName = GetString(set.RootElement, "Name") ?? id!;
                await ReadSetAsync(api, space, GetString(set.RootElement, "VariableSetId"), $"library variable set {setName}", isProject: false, entries, cancellationToken).ConfigureAwait(false);
            }
        }

        if (entries.Count == 0)
        {
            warnings.Add($"Octopus project {projectName} has no variables the API key can see.");
        }

        return new OctopusVariables(entries, warnings);
    }

    private static async Task<string> FindSpaceAsync(Api api, string? space, CancellationToken cancellationToken)
    {
        if (space is not null && SpaceId().IsMatch(space))
        {
            return space;
        }

        using var spaces = await api.GetAsync("api/spaces/all", cancellationToken).ConfigureAwait(false);
        foreach (var item in Items(spaces.RootElement))
        {
            var matches = space is null
                ? item.ValueKind == JsonValueKind.Object && item.TryGetProperty("IsDefault", out var d) && d.ValueKind == JsonValueKind.True
                : string.Equals(GetString(item, "Name"), space, StringComparison.OrdinalIgnoreCase);
            if (matches && GetString(item, "Id") is { } id)
            {
                return id;
            }
        }

        throw new InvalidOperationException(space is null
            ? "Octopus has no default space the API key can see. Set Space."
            : $"Octopus space '{space}' was not found, or the API key cannot see it.");
    }

    private static async Task ReadSetAsync(Api api, string space, string? variableSetId, string setName, bool isProject, List<OctopusVariable> into, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(variableSetId))
        {
            return;
        }

        using var doc = await api.GetAsync($"{space}/variables/{Uri.EscapeDataString(variableSetId)}", cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var names = ReadScopeNames(root);
        if (!root.TryGetProperty("Variables", out var variables) || variables.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var v in variables.EnumerateArray())
        {
            var name = GetString(v, "Name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var type = GetString(v, "Type") ?? "String";
            var sensitive = (v.TryGetProperty("IsSensitive", out var s) && s.ValueKind == JsonValueKind.True)
                || string.Equals(type, "Sensitive", StringComparison.OrdinalIgnoreCase);

            // Octopus returns null for sensitive values; drop one anyway if a server ever sends it.
            var value = sensitive ? null : GetString(v, "Value");
            into.Add(new OctopusVariable(name, value, sensitive, type, setName, isProject, ReadScope(v, names)));
        }
    }

    private static Dictionary<string, Dictionary<string, string>> ReadScopeNames(JsonElement root)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (!root.TryGetProperty("ScopeValues", out var scopeValues) || scopeValues.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var list in scopeValues.EnumerateObject())
        {
            if (!ScopeValueLists.TryGetValue(list.Name, out var dimension) || list.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, item) in list.Value.EnumerateArray().Select(i => (Id: GetString(i, "Id"), Item: i)).Where(x => x.Id is not null))
            {
                map[id!] = GetString(item, "Name") ?? id!;
            }

            result[dimension] = map;
        }

        return result;
    }

    private static Dictionary<string, IReadOnlyList<string>> ReadScope(JsonElement variable, Dictionary<string, Dictionary<string, string>> names)
    {
        var scope = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (!variable.TryGetProperty("Scope", out var s) || s.ValueKind != JsonValueKind.Object)
        {
            return scope;
        }

        foreach (var dimension in s.EnumerateObject())
        {
            if (dimension.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var lookup = names.GetValueOrDefault(dimension.Name);
            var values = dimension.Value.EnumerateArray()
                .Select(AsString)
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => lookup is not null && lookup.TryGetValue(id!, out var display) ? display : id!)
                .ToList();
            if (values.Count > 0)
            {
                scope[dimension.Name] = values;
            }
        }

        return scope;
    }

    private static JsonElement? FindByIdSlugOrName(JsonElement list, string wanted)
    {
        foreach (var field in ProjectLookupFields)
        {
            foreach (var item in Items(list))
            {
                if (string.Equals(GetString(item, field), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return item.Clone();
                }
            }
        }

        return null;
    }

    // The "all" endpoints return a bare array; older paged endpoints wrap it in "Items".
    private static JsonElement[] Items(JsonElement root) => root.ValueKind switch
    {
        JsonValueKind.Array => [.. root.EnumerateArray()],
        JsonValueKind.Object when root.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array => [.. items.EnumerateArray()],
        _ => [],
    };

    // Ids that are not strings are skipped rather than failing the whole trace.
    private static string? AsString(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"^Spaces-\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceId();

    private sealed class Api(HttpClient client, Uri baseAddress, string apiKey)
    {
        public Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken) =>
            JsonApi.GetAsync(client, new Uri(baseAddress, path), h => h.Add("X-Octopus-ApiKey", apiKey), "Octopus", cancellationToken);
    }
}
