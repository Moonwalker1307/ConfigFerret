using System.Text.RegularExpressions;
using ConfigFerret.Sources;

namespace ConfigFerret.AzureDevOps;

/// <summary>
/// Links environment variables, Kubernetes env entries and ConfigMap/Secret keys to Azure Pipelines variable group
/// variables and pipeline YAML variables.
/// </summary>
internal sealed partial class AzureDevOpsResolver(AzureDevOpsVariableGroups groups, AzureDevOpsTraceOptions options) : IOriginResolver
{
    private readonly List<string> _pipelineWarnings = options.ReadConfiguredPipelines();
    private ILookup<string, AzurePipelinesVariable>? _byName;
    private int _indexed = -1;
    private bool _warned;

    /// <summary>One entry per variable group variable, matching the steps' references.</summary>
    public IEnumerable<SourceEntry> Entries() => groups.Variables.Select(v => new SourceEntry(
        v.Name,
        Reference(v),
        IsSensitive(v),
        v.GroupType == AzureDevOpsGroupType.AzureKeyVault
            ? $"variable group {v.Group}, linked to Azure Key Vault {v.KeyVaultName ?? "(unknown vault)"}"
            : $"variable group {v.Group}{(v.IsSecret ? ", secret" : string.Empty)}",
        Location(v)));

    public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!_warned)
        {
            _warned = true;
            foreach (var warning in groups.Warnings.Concat(_pipelineWarnings))
            {
                diagnostics.Warn(warning);
            }
        }

        if (layer.Reference is AzurePipelinesVariableRef pipelineRef)
        {
            // A pipeline variable whose whole value is $(Other) gets its value from Other.
            var defined = options.Variables.FirstOrDefault(v => v.Line == pipelineRef.Line && v.File == pipelineRef.File && v.Name == pipelineRef.Name);
            if (AzureDevOpsTraceOptions.MacroTarget(defined?.Value) is not { } target)
            {
                return [];
            }

            return Chain(target, null, defined!)
                .Select(c => ToStep(c, layer, $"{defined!.File} line {defined.Line} sets {defined.Name} from $({target})", inferred: false))
                .ToList();
        }

        if (layer.Reference is ExternalSecretRef or AzureDevOpsVariableRef || LayerNames.Of(layer) is not { } name)
        {
            return [];
        }

        if (options.FindMapping(name) is { } mapping)
        {
            var linked = Chain(mapping.VariableName, mapping.Group, null);
            if (linked.Count == 0)
            {
                diagnostics.Warn($"{name} is linked to Azure Pipelines variable {(mapping.Group is null ? string.Empty : mapping.Group + "/")}{mapping.VariableName}{(mapping.Where is null ? string.Empty : $" ({mapping.Where})")}, but no such variable was loaded.");
                return [];
            }

            var how = mapping.Where is null ? "linked explicitly" : $"{mapping.Where} sets {mapping.Name} from $({mapping.VariableName})";
            return linked.Select(c => ToStep(c, layer, how, inferred: false)).ToList();
        }

        if (!options.MatchByName)
        {
            return [];
        }

        return Chain(name, null, null, byEnvironmentName: true).Select(c => ToStep(c, layer, How(c, name), inferred: true)).ToList();
    }

    private static string How(Candidate candidate, string name) =>
        string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)
            ? "matched by name"
            : $"matched by name: scripts see {candidate.Name} as {name}";

    // Every group and pipeline variable that sets name, lowest priority first: a group or variable listed later in the
    // pipeline's variables wins, and groups no pipeline file links come first. When except refers to name, a pipeline
    // variable that refers back to name cannot supply a value, so it is left out: Version: $(Version) in every stage
    // would otherwise lead from each stage to all the others, for each level followed. What a variable that refers to
    // itself leads to in the file is only inferred, since it is not certain which one Azure Pipelines expands it to.
    private List<Candidate> Chain(string name, string? group, AzurePipelinesVariable? except, bool byEnvironmentName = false)
    {
        bool Named(string candidate) => byEnvironmentName
            ? AzureDevOpsVariableGroups.Matches(candidate, name)
            : string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase);

        var fromGroups = groups.Variables
            .Select((v, index) => (Variable: v, Index: index))
            .Where(v => Named(v.Variable.Name) && (group is null || string.Equals(v.Variable.Group, group, StringComparison.OrdinalIgnoreCase)))
            .Select(v => new Candidate(v.Variable.Name, v.Variable, null, options.GroupOrder(v.Variable.Group) ?? -1, v.Index));
        var self = except is not null && string.Equals(except.Name, name, StringComparison.OrdinalIgnoreCase);
        var fromYaml = group is not null
            ? []
            : except is null
                ? options.Variables.Where(v => Named(v.Name)).Select(v => new Candidate(v.Name, null, v, v.Order, 0))
                : Exactly(name)
                    .Where(v => !ReferenceEquals(v, except) && !string.Equals(AzureDevOpsTraceOptions.MacroTarget(v.Value), name, StringComparison.OrdinalIgnoreCase))
                    .Select(v => new Candidate(v.Name, null, v, v.Order, 0, self));
        return fromGroups.Concat(fromYaml).OrderBy(c => c.Order).ThenBy(c => c.Index).ToList();
    }

    // The pipeline variables named name, in file order. They are indexed once, and again only when a pipeline was added.
    private IEnumerable<AzurePipelinesVariable> Exactly(string name)
    {
        if (_byName is null || _indexed != options.Variables.Count)
        {
            _byName = options.Variables.ToLookup(v => v.Name, StringComparer.OrdinalIgnoreCase);
            _indexed = options.Variables.Count;
        }

        return _byName[name];
    }

    private ProvenanceStep ToStep(Candidate candidate, ProvenanceStep layer, string how, bool inferred)
    {
        var notes = new List<string> { how };
        string? value;
        if (candidate.Variable is { } variable)
        {
            if (variable.GroupType == AzureDevOpsGroupType.AzureKeyVault)
            {
                notes.Add($"the value comes from secret {variable.Name} in Azure Key Vault {variable.KeyVaultName ?? "(unknown vault)"}");
            }
            else if (variable.IsSecret)
            {
                notes.Add("Azure DevOps does not return secret values");
            }

            if (variable.IsSensitive && inferred)
            {
                notes.Add("secret variables reach scripts only when mapped in an env: block");
            }

            if (options.GroupLinks.Count > 0 && options.GroupOrder(variable.Group) is null)
            {
                notes.Add("no pipeline file read links this group");
            }

            value = variable.Value;
        }
        else
        {
            value = candidate.Yaml!.Value;
        }

        var macro = AzureDevOpsTraceOptions.MacroTarget(value) is not null || AzureDevOpsTraceOptions.MacroTarget(layer.Value) is not null;
        if (layer.Value is not null && value is not null && !macro && !string.Equals(layer.Value, value, StringComparison.Ordinal))
        {
            // Never print either value here: notes are not redacted.
            notes.Add("the Azure DevOps value differs from the value seen here, so the deployment may be out of date or the value was transformed");
        }

        if (candidate.Variable is { } v)
        {
            return new ProvenanceStep
            {
                Kind = LayerKind.AzureDevOps,
                Source = $"Azure DevOps variable group {v.Group} {(v.IsSensitive ? "secret" : "variable")} {v.Name}",
                Location = Location(v),
                Value = v.Value,
                IsSensitive = IsSensitive(v),
                IsInferred = inferred,
                Note = string.Join("; ", notes),
                Reference = Reference(v),
            };
        }

        var yaml = candidate.Yaml!;
        return new ProvenanceStep
        {
            Kind = LayerKind.AzureDevOps,
            Source = $"Azure Pipelines variable {yaml.Name}",
            Location = $"{yaml.File} line {yaml.Line}",
            Value = yaml.Value,
            IsSensitive = SensitiveName().IsMatch(yaml.Name),
            IsInferred = inferred || candidate.Inferred,
            Note = string.Join("; ", notes),
            Reference = new AzurePipelinesVariableRef(yaml.File, yaml.Line, yaml.Name),
        };
    }

    // Azure DevOps hides a value only when it is marked secret, so a variable named like a secret is treated as secret too.
    private static bool IsSensitive(AzureDevOpsVariable variable) => variable.IsSensitive || SensitiveName().IsMatch(variable.Name);

    private static string Location(AzureDevOpsVariable variable) =>
        variable.GroupType == AzureDevOpsGroupType.AzureKeyVault && variable.KeyVaultName is not null ? variable.KeyVaultName : variable.Group;

    private AzureDevOpsVariableRef Reference(AzureDevOpsVariable variable) => new(groups.Project, variable.Group, variable.Name);

    private sealed record Candidate(string Name, AzureDevOpsVariable? Variable, AzurePipelinesVariable? Yaml, int Order, int Index, bool Inferred = false);

    // The default rule a trace uses for configuration keys. A resolver is built without the trace, so it cannot see the
    // trace's own SensitiveKeyPattern.
    [GeneratedRegex(TraceOptions.DefaultSensitiveKeyPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveName();
}
