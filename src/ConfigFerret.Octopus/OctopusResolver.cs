namespace ConfigFerret.Octopus;

/// <summary>
/// Links JSON configuration keys, environment variables, Kubernetes env entries and ConfigMap/Secret keys to Octopus
/// Deploy variables, picking the ones whose scope matches the deployment and ordering them by how specific the scope is.
/// </summary>
internal sealed class OctopusResolver(OctopusVariables variables, OctopusTraceOptions options) : IOriginResolver
{
    // Octopus picks the most specifically scoped variable. This follows its documented ranking (step, then target,
    // then target tag, then environment); where tenant tags and channels fall is a best guess.
    private static readonly Dictionary<string, int> Weights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Action"] = 32,
        ["Machine"] = 16,
        ["Role"] = 8,
        ["TenantTag"] = 4,
        ["Environment"] = 2,
        ["Channel"] = 1,
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

        var candidates = variables.Find(variableName)
            .Select(v => (Variable: v, Match: Applies(v)))
            .Where(c => c.Match.Applies)
            .OrderBy(c => Rank(c.Variable))
            .ThenBy(c => c.Variable.IsProjectVariable) // Project variables win over library ones of equal rank.
            .ToList();
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

        return candidates.Select(c => ToStep(c.Variable, layer, how, c.Match.Unknown)).ToList();
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

    private (bool Applies, List<string> Unknown) Applies(OctopusVariable variable)
    {
        var unknown = new List<string>();
        foreach (var (dimension, values) in variable.Scope)
        {
            var context = options.Context(dimension);
            if (context is null)
            {
                // Not told about this dimension: keep the variable but say the link depends on it.
                unknown.Add($"{Describe(dimension)} {string.Join(", ", values)}");
                continue;
            }

            if (!values.Any(v => context.Any(c => string.Equals(c, v, StringComparison.OrdinalIgnoreCase) || v.EndsWith("/" + c, StringComparison.OrdinalIgnoreCase))))
            {
                return (false, unknown);
            }
        }

        return (true, unknown);
    }

    private static int Rank(OctopusVariable variable) => variable.Scope.Keys.Sum(d => Weights.GetValueOrDefault(d));

    private static ProvenanceStep ToStep(OctopusVariable variable, ProvenanceStep layer, string? how, List<string> unknown)
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

        return new ProvenanceStep
        {
            Kind = LayerKind.OctopusDeploy,
            Source = $"Octopus variable {variable.Name}",
            Location = variable.IsProjectVariable ? $"project {variable.SetName}" : variable.SetName,
            Value = variable.Value,
            IsSensitive = variable.IsSensitive,
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
