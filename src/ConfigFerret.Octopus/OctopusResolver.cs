namespace ConfigFerret.Octopus;

/// <summary>
/// Links JSON configuration keys, environment variables, Kubernetes env entries and ConfigMap/Secret keys to Octopus
/// Deploy variables, picking the ones whose scope matches the deployment and ordering them by how specific the scope is.
/// </summary>
internal sealed class OctopusResolver(OctopusVariables variables, OctopusTraceOptions options) : IOriginResolver
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
        if (name is null)
        {
            return [];
        }

        var mapped = options.FindMapping(name)
            ?? (layer.ConfigurationKey is { } key && layer.Reference is FileRef ? options.FindMapping(key) : null);
        string variableName;
        if (mapped is not null)
        {
            variableName = mapped;
            how = null;
        }
        else if (how is not null)
        {
            variableName = name;
        }
        else
        {
            return [];
        }

        var matches = variables.Find(variableName)
            .Select(v => (Variable: v, Match: Applies(v)))
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
        return candidates.Select((c, i) => ToStep(c.Variable, layer, how, c.Match.Unknown, mayBeSensitive && i == candidates.Count - 1)).ToList();
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
        FileRef file when layer.ConfigurationKey is { } key && file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            => (key, options.MatchConfigurationKeys ? "structured configuration variable for this JSON key, matched by name" : null),
        EnvironmentVariableRef env => (env.Name, ByName()),
        WorkloadEnvRef env => (env.Name, ByName()),
        ConfigMapKeyRef cm => (cm.Key, ByName()),
        SecretKeyRef s => (s.Key, ByName()),
        _ => (null, null),
    };

    private string? ByName() => options.MatchByName ? "matched by name" : null;

    // RunbookOnly: the variable would apply but for its scope naming only runbooks.
    private (bool Applies, List<string> Unknown, bool RunbookOnly) Applies(OctopusVariable variable)
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

            var context = options.Context(dimension);
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

    private static ProvenanceStep ToStep(OctopusVariable variable, ProvenanceStep layer, string? how, List<string> unknown, bool mayBeSensitive)
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
            IsInferred = how is not null || unknown.Count > 0,
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
}
