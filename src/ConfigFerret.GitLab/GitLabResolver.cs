using System.Text.RegularExpressions;
using ConfigFerret.Sources;

namespace ConfigFerret.GitLab;

/// <summary>Links environment variables, Kubernetes env entries and ConfigMap/Secret keys to GitLab CI/CD variables.</summary>
internal sealed partial class GitLabResolver(GitLabVariables variables, GitLabTraceOptions options) : IOriginResolver
{
    private readonly List<string> _fileWarnings = options.ReadConfiguredCiFiles();
    private readonly HashSet<string> _scopeWarnings = new(StringComparer.Ordinal);
    private bool _warned;

    /// <summary>One entry per variable and environment scope, matching the steps' references.</summary>
    public IEnumerable<SourceEntry> Entries() => variables.Entries.Select(v => new SourceEntry(
        v.Key,
        Reference(v),
        IsSensitive(v),
        Detail(v),
        Location(v)));

    public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!_warned)
        {
            _warned = true;
            foreach (var warning in variables.Warnings.Concat(_fileWarnings))
            {
                diagnostics.Warn(warning);
            }
        }

        if (layer.Reference is GitLabCiVariableRef ciRef)
        {
            // A CI file variable whose whole value is $OTHER gets its value from OTHER.
            var defined = options.Variables.FirstOrDefault(v => v.Line == ciRef.Line && v.File == ciRef.File && v.Name == ciRef.Name);
            if (GitLabTraceOptions.ReferenceTarget(defined?.Value) is not { } target)
            {
                return [];
            }

            return Chain(target, defined, diagnostics)
                .Select(c => ToStep(c, layer, $"{defined!.File} line {defined.Line} sets {defined.Name} from ${target}", inferred: false))
                .ToList();
        }

        if (layer.Reference is ExternalSecretRef or GitLabVariableRef || LayerNames.Of(layer) is not { } name)
        {
            return [];
        }

        if (options.FindMapping(name) is { } mapping)
        {
            var linked = Chain(mapping.Key, null, diagnostics);
            if (linked.Count == 0)
            {
                diagnostics.Warn($"{name} is linked to GitLab variable {mapping.Key}{(mapping.Where is null ? string.Empty : $" ({mapping.Where})")}, but no such variable applies.");
                return [];
            }

            var how = mapping.Where is null ? "linked explicitly" : $"{mapping.Where} sets {mapping.Name} from ${mapping.Key}";
            return linked.Select(c => ToStep(c, layer, how, inferred: false)).ToList();
        }

        return options.MatchByName
            ? Chain(name, null, diagnostics).Select(c => ToStep(c, layer, "matched by name", inferred: true)).ToList()
            : [];
    }

    // Lowest priority first, as GitLab documents it: CI file variables (global, then job), then instance, then each
    // group from the outermost in, then the project.
    private List<Candidate> Chain(string key, GitLabCiVariable? except, TraceDiagnostics diagnostics)
    {
        var fromFiles = options.Variables
            .Where(v => string.Equals(v.Name, key, StringComparison.Ordinal) && !ReferenceEquals(v, except))
            .OrderBy(v => v.Job is not null)
            .Select(v => new Candidate(null, v));
        var effective = variables.Effective(key, options.Environment);
        if (effective.Count == 0 && variables.Find(key) is { Count: > 0 } scoped && _scopeWarnings.Add(key))
        {
            var scopes = string.Join(", ", scoped.Select(v => v.EnvironmentScope).Distinct(StringComparer.Ordinal));
            diagnostics.Warn($"GitLab variable {key} is set only for environment scopes {scopes}, and none applies to {(options.Environment is null ? "a job with no environment" : $"environment {options.Environment}")}.");
        }

        return fromFiles.Concat(effective.Select(v => new Candidate(v, null))).ToList();
    }

    private static ProvenanceStep ToStep(Candidate candidate, ProvenanceStep layer, string how, bool inferred)
    {
        var notes = new List<string> { how };
        string? value;
        if (candidate.Variable is { } variable)
        {
            if (variable.EnvironmentScope != "*")
            {
                notes.Add($"environment scope {variable.EnvironmentScope}");
            }

            if (variable.IsHidden)
            {
                notes.Add("hidden: GitLab does not return its value");
            }
            else if (variable.IsMasked)
            {
                notes.Add(variable.Value is null ? "masked: its value was not read" : "masked");
            }

            if (variable.IsProtected)
            {
                notes.Add("protected: only pipelines on protected branches and tags get it");
            }

            if (variable.IsFile)
            {
                notes.Add("a file variable: the job gets the path of a temporary file that holds the value");
            }

            value = variable.Value;
        }
        else
        {
            value = candidate.File!.Value;
        }

        var reference = GitLabTraceOptions.ReferenceTarget(value) is not null || GitLabTraceOptions.ReferenceTarget(layer.Value) is not null;
        if (layer.Value is not null && value is not null && !reference && candidate.Variable is not { IsFile: true }
            && !string.Equals(layer.Value, value, StringComparison.Ordinal))
        {
            // Never print either value here: notes are not redacted.
            notes.Add("the GitLab value differs from the value seen here, so the deployment may be out of date or the value was transformed");
        }

        if (candidate.Variable is { } v)
        {
            return new ProvenanceStep
            {
                Kind = LayerKind.GitLabCi,
                Source = $"GitLab {GitLabVariables.Describe(v.Level)} variable {v.Key}",
                Location = Location(v),
                Value = v.Value,
                IsSensitive = IsSensitive(v),
                IsInferred = inferred,
                Note = string.Join("; ", notes),
                Reference = Reference(v),
            };
        }

        var file = candidate.File!;
        return new ProvenanceStep
        {
            Kind = LayerKind.GitLabCi,
            Source = $"GitLab CI variable {file.Name}{(file.Job is null ? string.Empty : $" (job {file.Job})")}",
            Location = $"{file.File} line {file.Line}",
            Value = file.Value,
            IsSensitive = SensitiveName().IsMatch(file.Name),
            IsInferred = inferred,
            Note = string.Join("; ", notes),
            Reference = new GitLabCiVariableRef(file.File, file.Line, file.Name),
        };
    }

    // GitLab masks a value only when asked to and cannot mask one with several lines, so a variable named like a secret
    // and a file variable, which often holds a kubeconfig or a key, are treated as secret too.
    private static bool IsSensitive(GitLabVariable variable) => variable.IsSensitive || variable.IsFile || SensitiveName().IsMatch(variable.Key);

    private static string Location(GitLabVariable variable) =>
        variable.EnvironmentScope == "*" ? variable.Owner : $"{variable.Owner}, environment {variable.EnvironmentScope}";

    private static string Detail(GitLabVariable variable)
    {
        var parts = new List<string> { $"{GitLabVariables.Describe(variable.Level)} variable", $"environment scope {variable.EnvironmentScope}" };
        parts.AddRange(new[] { (variable.IsMasked, "masked"), (variable.IsHidden, "hidden"), (variable.IsProtected, "protected"), (variable.IsFile, "file") }
            .Where(f => f.Item1)
            .Select(f => f.Item2));
        return string.Join(", ", parts);
    }

    private static GitLabVariableRef Reference(GitLabVariable variable) => new(variable.Level, variable.Owner, variable.Key, variable.EnvironmentScope);

    private sealed record Candidate(GitLabVariable? Variable, GitLabCiVariable? File);

    // The default rule a trace uses for configuration keys. A resolver is built without the trace, so it cannot see the
    // trace's own SensitiveKeyPattern.
    [GeneratedRegex(TraceOptions.DefaultSensitiveKeyPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveName();
}
