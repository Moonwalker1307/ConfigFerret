using System.Globalization;
using System.Text.RegularExpressions;

namespace ConfigFerret.Octopus;

/// <summary>
/// Links JSON configuration keys, environment variables, Kubernetes env entries and ConfigMap/Secret keys to Octopus
/// Deploy variables, picking the ones whose scope matches the deployment and ordering them by how specific the scope is.
/// </summary>
internal sealed partial class OctopusResolver(OctopusVariables variables, OctopusTraceOptions options) : IOriginResolver
{
    // Octopus picks the most specifically scoped variable. This follows the order on its scope specificity page
    // (https://octopus.com/docs/projects/variables/getting-started): step, target, target tag, tenant tag, environment,
    // channel, then the deployment or runbook process. That page does not tell target tags the step targets apart from
    // the others here, and does not say how several scopes on one value combine; here the most specific one decides.
    private static readonly Dictionary<string, int> Weights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Action"] = 64,
        ["Machine"] = 32,
        ["Role"] = 16,
        ["TenantTag"] = 8,
        ["Environment"] = 4,
        ["Channel"] = 2,
        ["ProcessOwner"] = 1,
    };

    // The most variables one value is linked to through #{...}.
    private const int MaxTokens = 20;

    private bool _warned;

    public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!_warned)
        {
            _warned = true;
            foreach (var warning in variables.Warnings)
            {
                diagnostics.Warn(warning);
            }
        }

        var (name, how) = Target(layer);
        var mapped = name is null ? null : options.FindMapping(name)
            ?? (layer.ConfigurationKey is { } key && layer.Reference is FileRef ? options.FindMapping(key) : null);
        if (mapped is not null)
        {
            return Link(mapped, layer, new Linking(null, false, null), diagnostics, warnAs: name);
        }

        var path = (layer.Reference as FileRef)?.Path;
        if (options.MatchSubstitutions && layer.Value is { } value && SubstitutionTokens(value) is { Count: > 0 } tokens)
        {
            return Substitutions(tokens, layer, path, diagnostics);
        }

        if (name is null || how is null)
        {
            return [];
        }

        var linking = path is not null && how == StructuredNote
            ? Structured(path)
            : new Linking(how, false, null);
        return Link(name, layer, linking, diagnostics, warnAs: null);
    }

    // How a layer was linked: the note, whether a step of the process confirms it, and the actions that change the file.
    private sealed record Linking(string? How, bool Confirmed, List<OctopusDeploymentAction>? Actions);

    private Linking Structured(string path)
    {
        if (variables.Process is not { } process)
        {
            return new Linking(StructuredNote, false, null);
        }

        var (match, found) = Changing(process, a => a.StructuredConfigurationTargets, path);
        return match switch
        {
            TargetMatch.Yes => new Linking($"replaced by structured configuration variables in {Steps(found)}, matched by name", true, Actions(found)),
            TargetMatch.Maybe => new Linking($"may be replaced by structured configuration variables in {Steps(found)}, whose target files are built from variables, matched by name", false, Actions(found)),
            _ => new Linking($"{StructuredNote}, but no step in the {process.Description} replaces values in this file", false, null),
        };
    }

    private List<ProvenanceStep> Substitutions(List<string> tokens, ProvenanceStep layer, string? path, TraceDiagnostics diagnostics)
    {
        Linking linking;
        if (path is null)
        {
            linking = new Linking("written here, and Octopus substitutes it when a step runs Substitute Variables in Templates on the file it comes from", false, null);
        }
        else if (variables.Process is not { } process)
        {
            linking = new Linking("written in this file, and Octopus substitutes it when a step runs Substitute Variables in Templates on it", false, null);
        }
        else
        {
            var (match, found) = Changing(process, a => a.SubstituteInFilesTargets, path);
            linking = match switch
            {
                TargetMatch.Yes => new Linking($"substituted into this file in {Steps(found)}", true, Actions(found)),
                TargetMatch.Maybe => new Linking($"may be substituted into this file in {Steps(found)}, whose target files are built from variables", false, Actions(found)),
                _ => new Linking($"written in this file, but no step in the {process.Description} substitutes variables in it", false, null),
            };
        }

        var result = new List<ProvenanceStep>();
        foreach (var token in tokens)
        {
            var how = $"#{{{token}}} {linking.How}";
            if (tokens.Count > 1)
            {
                how += string.Create(CultureInfo.InvariantCulture, $"; one of {tokens.Count} variables in this value");
            }

            result.AddRange(Link(token, layer, linking with { How = how }, diagnostics, warnAs: null));
        }

        // Only the last step decides whether the layer is redacted, so it carries a sensitive value substituted before it.
        if (result.Count > 1 && !result[^1].IsSensitive && result.Exists(s => s.IsSensitive))
        {
            result[^1] = result[^1] with
            {
                IsSensitive = true,
                Note = string.Join("; ", new[] { result[^1].Note, "another variable substituted into this value is sensitive" }.Where(n => !string.IsNullOrEmpty(n))),
            };
        }

        return result;
    }

    // The actions that run and change the file at path with the feature whose targets are given.
    private (TargetMatch Match, List<(OctopusDeploymentAction Action, string Pattern)> Found) Changing(
        OctopusDeploymentProcess process, Func<OctopusDeploymentAction, IReadOnlyList<string>> targets, string path)
    {
        var yes = new List<(OctopusDeploymentAction, string)>();
        var maybe = new List<(OctopusDeploymentAction, string)>();
        foreach (var action in process.Actions.Where(Runs))
        {
            switch (OctopusDeploymentAction.MatchTarget(targets(action), path, out var pattern))
            {
                case TargetMatch.Yes:
                    yes.Add((action, pattern!));
                    break;
                case TargetMatch.Maybe:
                    maybe.Add((action, pattern!));
                    break;
            }
        }

        return yes.Count > 0 ? (TargetMatch.Yes, yes) : maybe.Count > 0 ? (TargetMatch.Maybe, maybe) : (TargetMatch.No, []);
    }

    // Whether an action runs in the deployment described: a value that was not given does not rule it out.
    private bool Runs(OctopusDeploymentAction action)
    {
        static bool In(IReadOnlyList<string> list, string value) => list.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));

        return !action.IsDisabled
            && (string.IsNullOrWhiteSpace(options.Environment)
                || ((action.Environments.Count == 0 || In(action.Environments, options.Environment)) && !In(action.ExcludedEnvironments, options.Environment)))
            && (string.IsNullOrWhiteSpace(options.Channel) || action.Channels.Count == 0 || In(action.Channels, options.Channel))
            && (options.Roles.Count == 0 || action.Roles.Count == 0 || action.Roles.Any(r => options.Roles.Contains(r)))
            && (string.IsNullOrWhiteSpace(options.Step) || string.Equals(action.Name, options.Step, StringComparison.OrdinalIgnoreCase) || string.Equals(action.StepName, options.Step, StringComparison.OrdinalIgnoreCase));
    }

    private static List<OctopusDeploymentAction> Actions(List<(OctopusDeploymentAction Action, string Pattern)> found) => [.. found.Select(f => f.Action)];

    private static string Steps(List<(OctopusDeploymentAction Action, string Pattern)> found)
    {
        var listed = found.Select(f => f.Action.StepTemplate is { } template
            ? $"{f.Action.StepName} (step template {template}, target {f.Pattern})"
            : $"{f.Action.StepName} (target {f.Pattern})").ToList();
        return (listed.Count == 1 ? "step " : "steps ") + string.Join(", ", listed);
    }

    private List<ProvenanceStep> Link(string variableName, ProvenanceStep layer, Linking linking, TraceDiagnostics diagnostics, string? warnAs)
    {
        var how = linking.How;
        var mapped = warnAs is not null ? variableName : null;
        var name = warnAs ?? variableName;

        // When one step changes the file and no step was given, a value scoped to that step applies.
        var step = options.Step;
        if (string.IsNullOrWhiteSpace(step) && linking.Actions is { } changing && changing.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
        {
            step = changing[0].Name;
        }

        var parameters = Parameters(variableName, linking.Actions);

        var matches = variables.Find(variableName)
            .Select(v => (Variable: v, Match: Applies(v, step)))
            .OrderBy(c => Rank(c.Variable))
            .ThenBy(c => c.Variable.IsProjectVariable) // Project variables win over library ones of equal rank.
            .ToList();
        var candidates = matches.Where(c => c.Match.Applies).ToList();

        // The trace is taken to be of a deployment, which cannot be changed, so a value left out only for being scoped
        // to runbooks may still be the one used: when the configuration came from a runbook, values that are certain in
        // a deployment may not apply. When it is sensitive, keep that doubt whatever outranks it, and link to it when
        // nothing else applies, so that the layer is still redacted.
        var runbookSecrets = matches.Where(c => c.Match.RunbookOnly && c.Variable.IsSensitive).ToList();
        if (candidates.Count == 0)
        {
            candidates = runbookSecrets;
        }

        if (candidates.Count == 0)
        {
            if (parameters.Count > 0)
            {
                return parameters;
            }

            if (mapped is not null)
            {
                diagnostics.Warn($"{name} is linked to Octopus variable {mapped}, but no such variable applies to this deployment.");
            }

            return [];
        }

        if (candidates.Count > 1)
        {
            var top = candidates[^1].Variable;
            var runnerUp = candidates[^2].Variable;
            if (Rank(top) == Rank(runnerUp) && top.IsProjectVariable == runnerUp.IsProjectVariable)
            {
                diagnostics.Warn($"Octopus variable {variableName} has more than one value scoped equally for this deployment, so which one Octopus uses is not certain.");
            }
        }

        // Only the last step decides whether the layer is redacted, so it carries the doubt when a sensitive value may win.
        var mayBeSensitive = !candidates[^1].Variable.IsSensitive && (runbookSecrets.Count > 0 || SensitiveMayWin(candidates));
        var chain = candidates.Select((c, i) => ToStep(c.Variable, layer, how, linking.Confirmed, c.Match.Unknown, mayBeSensitive && i == candidates.Count - 1)).ToList();
        if (parameters.Count > 0 && !parameters[^1].IsSensitive && chain.Exists(s => s.IsSensitive))
        {
            // The parameter decides the value, but a sensitive variable it may stand in for keeps the layer redacted.
            parameters[^1] = parameters[^1] with { IsSensitive = true };
        }

        chain.AddRange(parameters);
        return chain;
    }

    // A step template parameter set on the step that changes the file, which that step uses as a variable.
    private static List<ProvenanceStep> Parameters(string name, List<OctopusDeploymentAction>? actions)
    {
        if (actions is null)
        {
            return [];
        }

        var result = new List<ProvenanceStep>();
        foreach (var action in actions)
        {
            if (!action.Parameters.TryGetValue(name, out var value))
            {
                continue;
            }

            var notes = new List<string> { $"a parameter of step template {action.StepTemplate}, set on step {action.StepName}, which that step uses ahead of a variable with the same name" };
            if (value is null)
            {
                notes.Add("Octopus does not return sensitive values");
            }
            else if (value.Contains("#{", StringComparison.Ordinal))
            {
                notes.Add("the value references other Octopus variables, which are not expanded here");
            }

            result.Add(new ProvenanceStep
            {
                Kind = LayerKind.OctopusDeploy,
                Source = $"Octopus step template parameter {name}",
                Location = $"step {action.StepName}",
                Value = value,
                IsSensitive = value is null,
                IsInferred = true,
                Note = string.Join("; ", notes),
                Reference = new OctopusStepParameterRef(action.StepName, name),
            });
        }

        return result;
    }

    // A candidate may be the one Octopus uses when it is scoped as specifically as the last one, or when every
    // candidate ranked above it may not apply because its scope names something that was not given.
    private static bool SensitiveMayWin(List<(OctopusVariable Variable, (bool Applies, List<string> Unknown, bool RunbookOnly) Match)> candidates)
    {
        var i = candidates.Count - 1;
        while (i >= 0)
        {
            var top = candidates[i].Variable;
            var certain = false;
            for (; i >= 0 && Rank(candidates[i].Variable) == Rank(top) && candidates[i].Variable.IsProjectVariable == top.IsProjectVariable; i--)
            {
                if (candidates[i].Variable.IsSensitive)
                {
                    return true;
                }

                certain |= candidates[i].Match.Unknown.Count == 0;
            }

            if (certain)
            {
                return false;
            }
        }

        return false;
    }

    private (string? Name, string? How) Target(ProvenanceStep layer) => layer.Reference switch
    {
        FileRef file when layer.ConfigurationKey is { } key && IsStructuredFile(file.Path)
            => (key, options.MatchConfigurationKeys ? StructuredNote : null),
        EnvironmentVariableRef env => (env.Name, ByName()),
        WorkloadEnvRef env => (env.Name, ByName()),
        ConfigMapKeyRef cm => (cm.Key, ByName()),
        SecretKeyRef s => (s.Key, ByName()),
        _ => (null, null),
    };

    private const string StructuredNote = "structured configuration variable for this key, matched by name";

    // The files whose keys a .NET configuration provider reads with the same names Octopus structured configuration uses.
    private static bool IsStructuredFile(string path) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

    // The plain variable names in #{...} expressions, leaving out escaped ##{...}, the template statements and
    // Octopus system variables, which are not project variables.
    internal static List<string> SubstitutionTokens(string value)
    {
        var tokens = new List<string>();
        if (!value.Contains("#{", StringComparison.Ordinal))
        {
            return tokens;
        }

        foreach (Match match in Token().Matches(value))
        {
            var token = match.Groups[1].Value.Trim();
            if (token.Length > 0 && !token.StartsWith("Octopus.", StringComparison.OrdinalIgnoreCase) && !tokens.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                tokens.Add(token);
                if (tokens.Count == MaxTokens)
                {
                    break;
                }
            }
        }

        return tokens;
    }

    private string? ByName() => options.MatchByName ? "matched by name" : null;

    // RunbookOnly: the variable would apply but for its scope naming only runbooks.
    private (bool Applies, List<string> Unknown, bool RunbookOnly) Applies(OctopusVariable variable, string? step)
    {
        var unknown = new List<string>();
        var runbookOnly = false;
        foreach (var (dimension, values) in variable.Scope)
        {
            // A trace is taken to be of a deployment, so a value scoped only to runbooks does not apply.
            if (dimension == "ProcessOwner" && variable.Process != ProcessScope.TypeNotGiven)
            {
                if (variable.Process == ProcessScope.RunbooksOnly)
                {
                    runbookOnly = true;
                    unknown.Add($"runbook {string.Join(", ", values)}");
                }

                continue;
            }

            var context = dimension == "Action" && !string.IsNullOrWhiteSpace(step) ? [step] : options.Context(dimension);
            if (context is null)
            {
                // Not told about this dimension: keep the variable but say the link depends on it.
                unknown.Add($"{Describe(dimension)} {string.Join(", ", values)}");
                continue;
            }

            if (!values.Any(v => context.Any(c => string.Equals(c, v, StringComparison.OrdinalIgnoreCase) || v.EndsWith("/" + c, StringComparison.OrdinalIgnoreCase))))
            {
                return (false, unknown, false);
            }
        }

        return (!runbookOnly, unknown, runbookOnly);
    }

    private static int Rank(OctopusVariable variable) => variable.Scope.Keys.Sum(d => Weights.GetValueOrDefault(d));

    private static ProvenanceStep ToStep(OctopusVariable variable, ProvenanceStep layer, string? how, bool confirmed, List<string> unknown, bool mayBeSensitive)
    {
        var notes = new List<string>();
        if (how is not null)
        {
            notes.Add(how);
        }

        if (variable.Scope.Count > 0)
        {
            notes.Add("scoped to " + string.Join("; ", variable.Scope.Select(s => $"{Describe(s.Key)} {string.Join(", ", s.Value)}")));
        }

        if (unknown.Count > 0)
        {
            notes.Add("applies only if the deployment matches " + string.Join(" and ", unknown) + ", which was not given");
        }

        if (variable.IsSensitive)
        {
            notes.Add("Octopus does not return sensitive values");
        }
        else if (variable.Value is not null)
        {
            if (variable.Value.Contains("#{", StringComparison.Ordinal))
            {
                notes.Add("the value references other Octopus variables, which are not expanded here");
            }
            else if (layer.Value is not null && !string.Equals(layer.Value, variable.Value, StringComparison.Ordinal))
            {
                // Never print either value here: notes are not redacted.
                notes.Add("the Octopus value differs from the value seen here, so the deployment may be out of date or the value was transformed");
            }
        }

        if (!string.Equals(variable.Type, "String", StringComparison.OrdinalIgnoreCase) && !variable.IsSensitive)
        {
            notes.Add($"type {variable.Type}");
        }

        if (mayBeSensitive)
        {
            notes.Add("another value that may apply instead is sensitive");
        }

        return new ProvenanceStep
        {
            Kind = LayerKind.OctopusDeploy,
            Source = $"Octopus variable {variable.Name}",
            Location = variable.IsProjectVariable ? $"project {variable.SetName}" : variable.SetName,
            Value = variable.Value,
            IsSensitive = variable.IsSensitive || mayBeSensitive,
            IsInferred = (how is not null && !confirmed) || unknown.Count > 0,
            Note = string.Join("; ", notes),
            Reference = new OctopusVariableRef(variable.SetName, variable.Name),
        };
    }

    private static string Describe(string dimension) => dimension switch
    {
        "Machine" => "target",
        "Role" => "target tag",
        "Action" => "step",
        "TenantTag" => "tenant tag",
        "ProcessOwner" => "process",
        "Environment" => "environment",
        "Channel" => "channel",
        _ => dimension,
    };

    // #{Name} or #{Name | Filter}, not ##{escaped}, #{if ...}, #{unless ...}, #{each ...}, #{else}, #{reset} or #{/if}.
    [GeneratedRegex(@"(?<!#)#\{\s*(?!/|(?:if|unless|each)\s|(?:else|reset)\s*\})([^}|]{1,200}?)\s*(?:\|[^}]{0,200})?\}", RegexOptions.CultureInvariant)]
    private static partial Regex Token();
}
