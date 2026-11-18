using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ConfigFerret.Octopus;

// Reading releases and the deployment process.
public sealed partial class OctopusVariables
{
    // The most actions and step templates one process is read with, so a hostile server cannot make the trace slow.
    private const int MaxActions = 1_000;
    private const int MaxStepTemplates = 50;

    private const string EnabledFeatures = "Octopus.Action.EnabledFeatures";

    private static async Task<JsonElement> FindReleaseAsync(Api api, string space, string projectId, string projectName, string version, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await api.GetAsync($"{space}/projects/{Uri.EscapeDataString(projectId)}/releases/{Uri.EscapeDataString(version.Trim())}", cancellationToken).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"Octopus returned a release of project {projectName} that is not an object.");
            }

            return doc.RootElement.Clone();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"Octopus release {version.Trim()} of project {projectName} was not found, or the API key cannot see it.", ex);
        }
    }

    // The library variable sets to read, with the snapshot to read each from when a release gives one.
    private static List<(string? Id, string? SnapshotId)> LibrarySets(JsonElement project, JsonElement? release)
    {
        var result = new List<(string? Id, string? SnapshotId)>();
        if (release is { } r && r.TryGetProperty("LibraryVariableSetSnapshotIds", out var snapshots) && snapshots.ValueKind == JsonValueKind.Array)
        {
            foreach (var snapshot in snapshots.EnumerateArray().Select(AsString).Where(s => !string.IsNullOrEmpty(s)))
            {
                var match = LibrarySnapshotId().Match(snapshot!);
                result.Add((match.Success ? match.Groups[1].Value : null, snapshot));
            }

            return result;
        }

        if (project.TryGetProperty("IncludedLibraryVariableSetIds", out var included) && included.ValueKind == JsonValueKind.Array)
        {
            result.AddRange(included.EnumerateArray().Select(AsString).Where(s => !string.IsNullOrEmpty(s)).Select(id => ((string?)id, (string?)null)));
        }

        return result;
    }

    private static async Task<OctopusDeploymentProcess?> ReadProcessAsync(
        Api api,
        string space,
        JsonElement project,
        string projectId,
        string projectName,
        JsonElement? release,
        OctopusVariablesOptions options,
        Dictionary<string, Dictionary<string, string>> names,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        string path;
        string description;
        if (release is { } r)
        {
            var snapshot = GetString(r, "ProjectDeploymentProcessSnapshotId");
            var version = GetString(r, "Version") ?? options.Release!.Trim();
            if (string.IsNullOrEmpty(snapshot))
            {
                warnings.Add($"Octopus release {version} of project {projectName} names no deployment process snapshot, so the steps that replace values are not known and values are linked by name only.");
                return null;
            }

            path = $"{space}/deploymentprocesses/{Uri.EscapeDataString(snapshot)}";
            description = $"deployment process of release {version}";
        }
        else if (IsVersionControlled(project))
        {
            var gitRef = GitReference(options.GitRef ?? (project.TryGetProperty("PersistenceSettings", out var settings) ? GetString(settings, "DefaultBranch") : null) ?? "main");
            path = $"{space}/projects/{Uri.EscapeDataString(projectId)}/{Uri.EscapeDataString(gitRef)}/deploymentprocesses";
            description = $"deployment process at {gitRef}";
        }
        else
        {
            path = $"{space}/projects/{Uri.EscapeDataString(projectId)}/deploymentprocesses";
            description = "current deployment process";
        }

        JsonDocument doc;
        try
        {
            doc = await api.GetAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            warnings.Add($"The {description} of Octopus project {projectName} could not be read (HTTP {(int)ex.StatusCode}), so the steps that replace values are not known and values are linked by name only. The API key needs permission to view the process (ProcessView).");
            return null;
        }

        var actions = new List<OctopusDeploymentAction>();
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("Steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            {
                warnings.Add($"The {description} of Octopus project {projectName} has no steps.");
                return new OctopusDeploymentProcess([], description);
            }

            foreach (var step in steps.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object))
            {
                var stepName = GetString(step, "Name") ?? "(unnamed step)";
                var roles = SplitList(Property(step, "Octopus.Action.TargetRoles"), ',');
                if (!step.TryGetProperty("Actions", out var stepActions) || stepActions.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var action in stepActions.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object))
                {
                    if (actions.Count == MaxActions)
                    {
                        warnings.Add($"The {description} of Octopus project {projectName} has more than {MaxActions:N0} actions; only the first {MaxActions:N0} are used.");
                        return Finish();
                    }

                    var parsed = ParseAction(action, stepName, roles, names);
                    if (Property(action, "Octopus.Action.Template.Id") is { Length: > 0 } templateId)
                    {
                        parsed = parsed with { StepTemplate = templateId };
                    }

                    actions.Add(parsed);
                }
            }
        }

        return Finish();

        OctopusDeploymentProcess Finish()
        {
            foreach (var action in actions.Where(a => a.ConfigurationVariables || a.ConfigurationTransforms).Where(a => !a.IsDisabled))
            {
                var what = (action.ConfigurationVariables, action.ConfigurationTransforms) switch
                {
                    (true, true) => ".NET configuration variables and XML configuration transforms",
                    (true, false) => ".NET configuration variables",
                    _ => "XML configuration transforms",
                };
                warnings.Add($"Step {action.StepName} of Octopus project {projectName} runs {what} on .config files, which are not traced.");
            }

            return new OctopusDeploymentProcess(actions, description);
        }
    }

    // Names the step templates the process uses. Best effort: a template the API key cannot see keeps its id.
    private static async Task<OctopusDeploymentProcess> NameStepTemplatesAsync(Api api, string space, OctopusDeploymentProcess process, CancellationToken cancellationToken)
    {
        var ids = process.Actions.Select(a => a.StepTemplate).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxStepTemplates).ToList();
        if (ids.Count == 0)
        {
            return process;
        }

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            try
            {
                using var template = await api.GetAsync($"{space}/actiontemplates/{Uri.EscapeDataString(id)}", cancellationToken).ConfigureAwait(false);
                if (GetString(template.RootElement, "Name") is { Length: > 0 } name)
                {
                    names[id] = name;
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                // Keep the id.
            }
        }

        return new OctopusDeploymentProcess(
            process.Actions.Select(a => a.StepTemplate is { } id && names.TryGetValue(id, out var name) ? a with { StepTemplate = name } : a),
            process.Description);
    }

    private static OctopusDeploymentAction ParseAction(JsonElement action, string stepName, IReadOnlyList<string> roles, Dictionary<string, Dictionary<string, string>> names)
    {
        var features = SplitList(Property(action, EnabledFeatures), ',');
        bool Has(string feature, string? flag = null) =>
            features.Contains(feature, StringComparer.OrdinalIgnoreCase)
            || (flag is not null && string.Equals(Property(action, flag), "True", StringComparison.OrdinalIgnoreCase));

        var structured = Has("Octopus.Features.JsonConfigurationVariables", "Octopus.Action.Package.JsonConfigurationVariablesEnabled");
        var substitute = Has("Octopus.Features.SubstituteInFiles", "Octopus.Action.SubstituteInFiles.Enabled");
        var transforms = Has("Octopus.Features.ConfigurationTransforms")
            || string.Equals(Property(action, "Octopus.Action.Package.AutomaticallyRunConfigurationTransformationFiles"), "True", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(Property(action, "Octopus.Action.Package.AdditionalXmlConfigurationTransforms"));
        var configurationVariables = Has("Octopus.Features.ConfigurationVariables", "Octopus.Action.Package.AutomaticallyUpdateAppSettingsAndConnectionStrings");

        var isTemplate = !string.IsNullOrEmpty(Property(action, "Octopus.Action.Template.Id"));
        var parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (isTemplate && action.TryGetProperty("Properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject().Where(p => !p.Name.StartsWith("Octopus.", StringComparison.OrdinalIgnoreCase) && p.Name.Length > 0))
            {
                // A sensitive parameter comes back as an object that says whether it has a value, never the value.
                parameters[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }
        }

        return new OctopusDeploymentAction(stepName, GetString(action, "Name") ?? stepName, GetString(action, "ActionType") ?? string.Empty)
        {
            IsDisabled = action.TryGetProperty("IsDisabled", out var disabled) && disabled.ValueKind == JsonValueKind.True,
            Roles = roles,
            Environments = Names(action, "Environments", names.GetValueOrDefault("Environment")),
            ExcludedEnvironments = Names(action, "ExcludedEnvironments", names.GetValueOrDefault("Environment")),
            Channels = Names(action, "Channels", names.GetValueOrDefault("Channel")),
            StructuredConfigurationTargets = structured ? SplitList(Property(action, "Octopus.Action.Package.JsonConfigurationVariablesTargets"), '\n') : [],
            SubstituteInFilesTargets = substitute ? SplitList(Property(action, "Octopus.Action.SubstituteInFiles.TargetFiles"), '\n') : [],
            ConfigurationVariables = configurationVariables,
            ConfigurationTransforms = transforms,
            Parameters = parameters,
        };
    }

    private static List<string> Names(JsonElement action, string field, Dictionary<string, string>? lookup) =>
        action.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(AsString).Where(id => !string.IsNullOrEmpty(id)).Select(id => lookup is not null && lookup.TryGetValue(id!, out var name) ? name : id!)]
            : [];

    private static string? Property(JsonElement element, string name) =>
        element.TryGetProperty("Properties", out var properties) && properties.ValueKind == JsonValueKind.Object ? GetString(properties, name) : null;

    private static List<string> SplitList(string? value, char separator) =>
        value is null ? [] : [.. value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static bool IsVersionControlled(JsonElement project) =>
        project.TryGetProperty("PersistenceSettings", out var settings)
        && string.Equals(GetString(settings, "Type"), "VersionControlled", StringComparison.OrdinalIgnoreCase);

    // Octopus takes a full reference; a bare name is a branch.
    private static string GitReference(string name)
    {
        var trimmed = name.Trim();
        return trimmed.StartsWith("refs/", StringComparison.Ordinal) ? trimmed : $"refs/heads/{trimmed}";
    }

    [GeneratedRegex(@"^variableset-(LibraryVariableSets-\d+)-s-", RegexOptions.CultureInvariant)]
    private static partial Regex LibrarySnapshotId();
}
