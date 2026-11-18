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

    /// <summary>
    /// The deployment process, when it was read (see <see cref="OctopusVariablesOptions.ReadDeploymentProcess"/>), or
    /// null. With it, the trace says which step replaced a value and links <c>#{Variable}</c> in files to the steps that
    /// substitute them.
    /// </summary>
    public OctopusDeploymentProcess? Process { get; init; }

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
        var projectId = GetString(project, "Id") ?? options.Project;

        // A release keeps snapshots of the variables and the process it was created with, which its deployments use.
        JsonElement? release = null;
        if (!string.IsNullOrWhiteSpace(options.Release))
        {
            release = await FindReleaseAsync(api, space, projectId, projectName, options.Release, cancellationToken).ConfigureAwait(false);
        }

        var projectSetId = release is { } r ? GetString(r, "ProjectVariableSetSnapshotId") ?? GetString(project, "VariableSetId") : GetString(project, "VariableSetId");
        var names = await ReadSetAsync(api, space, projectSetId, projectName, isProject: true, entries, cancellationToken).ConfigureAwait(false);

        if (options.IncludeLibraryVariableSets)
        {
            foreach (var (id, snapshotId) in LibrarySets(project, release))
            {
                if (id is null)
                {
                    // A snapshot id that does not name its library variable set: read it under its own id.
                    await ReadSetAsync(api, space, snapshotId, $"library variable set {snapshotId}", isProject: false, entries, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                using var set = await api.GetAsync($"{space}/libraryvariablesets/{Uri.EscapeDataString(id)}", cancellationToken).ConfigureAwait(false);
                var setName = GetString(set.RootElement, "Name") ?? id;
                await ReadSetAsync(api, space, snapshotId ?? GetString(set.RootElement, "VariableSetId"), $"library variable set {setName}", isProject: false, entries, cancellationToken).ConfigureAwait(false);
            }
        }

        // A version-controlled project can keep its variables in Git; Octopus then keeps only the sensitive ones itself.
        // When none are left in Octopus, that is the reason rather than what the API key can see.
        if (VariablesAreInGit(project))
        {
            warnings.Add($"Octopus project {projectName} keeps its variables in Git, and those are not read: only the variables stored in Octopus itself, such as its sensitive ones, are traced.");
        }
        else if (entries.Count == 0)
        {
            warnings.Add($"Octopus project {projectName} has no variables the API key can see.");
        }

        OctopusDeploymentProcess? process = null;
        if (options.ReadDeploymentProcess || release is not null)
        {
            process = await ReadProcessAsync(api, space, project, projectId, projectName, release, options, names, warnings, cancellationToken).ConfigureAwait(false);
            if (process is not null)
            {
                process = await NameStepTemplatesAsync(api, space, process, cancellationToken).ConfigureAwait(false);
            }
        }

        return new OctopusVariables(entries, warnings) { Process = process };
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

    // Returns the scope names (environment and channel ids to names) the set lists.
    private static async Task<Dictionary<string, Dictionary<string, string>>> ReadSetAsync(Api api, string space, string? variableSetId, string setName, bool isProject, List<OctopusVariable> into, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(variableSetId))
        {
            return [];
        }

        using var doc = await api.GetAsync($"{space}/variables/{Uri.EscapeDataString(variableSetId)}", cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var processTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = ReadScopeNames(root, processTypes);
        if (!root.TryGetProperty("Variables", out var variables) || variables.ValueKind != JsonValueKind.Array)
        {
            return names;
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
            into.Add(new OctopusVariable(name, value, sensitive, type, setName, isProject, ReadScope(v, names)) { Process = ReadProcessScope(v, processTypes) });
        }

        return names;
    }

    private static Dictionary<string, Dictionary<string, string>> ReadScopeNames(JsonElement root, Dictionary<string, string> processTypes)
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

                // Processes lists the deployment process and each runbook, told apart by ProcessType.
                if (dimension == "ProcessOwner" && GetString(item, "ProcessType") is { } processType)
                {
                    processTypes[id!] = processType;
                }
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

            // Octopus names a tenant tag by its canonical name, tag set/tag, as its id, and by the tag alone as its name,
            // which is ambiguous across tag sets; keep the canonical name, which a tag name still matches.
            var lookup = string.Equals(dimension.Name, "TenantTag", StringComparison.OrdinalIgnoreCase) ? null : names.GetValueOrDefault(dimension.Name);
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

    // Decided by id, because a runbook can have any name, the project's included.
    private static ProcessScope ReadProcessScope(JsonElement variable, Dictionary<string, string> processTypes)
    {
        if (!variable.TryGetProperty("Scope", out var s) || s.ValueKind != JsonValueKind.Object
            || !s.TryGetProperty("ProcessOwner", out var owners) || owners.ValueKind != JsonValueKind.Array)
        {
            return ProcessScope.TypeNotGiven;
        }

        var types = owners.EnumerateArray()
            .Select(AsString)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => processTypes.GetValueOrDefault(id!))
            .ToList();
        return types.Any(t => string.Equals(t, "Deployment", StringComparison.OrdinalIgnoreCase)) ? ProcessScope.Deployment
            : types.Count > 0 && types.All(t => string.Equals(t, "Runbook", StringComparison.OrdinalIgnoreCase)) ? ProcessScope.RunbooksOnly
            : ProcessScope.TypeNotGiven;
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

    private static bool VariablesAreInGit(JsonElement project) =>
        project.TryGetProperty("PersistenceSettings", out var settings)
        && string.Equals(GetString(settings, "Type"), "VersionControlled", StringComparison.OrdinalIgnoreCase)
        && settings.TryGetProperty("ConversionState", out var state)
        && state.ValueKind == JsonValueKind.Object
        && state.TryGetProperty("VariablesAreInGit", out var inGit)
        && inGit.ValueKind == JsonValueKind.True;

    // Ids that are not strings are skipped rather than failing the whole trace.
    private static string? AsString(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"^Spaces-\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceId();

    private sealed class Api(HttpClient client, Uri baseAddress, string apiKey)
    {
        public async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
        {
            var uri = new Uri(baseAddress, path);
            var doc = await JsonApi.GetAsync(client, uri, h => h.Add("X-Octopus-ApiKey", apiKey), "Octopus", null, apiKey, cancellationToken).ConfigureAwait(false);
            try
            {
                JsonApi.RequireValidText(doc, "Octopus", uri);
                return doc;
            }
            catch
            {
                doc.Dispose();
                throw;
            }
        }
    }
}
