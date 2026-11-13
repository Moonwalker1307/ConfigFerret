namespace ConfigFerret.GitHub;

/// <summary>Links environment variables, Kubernetes env entries and ConfigMap/Secret keys to GitHub Actions variables.</summary>
internal sealed class GitHubResolver(GitHubVariables variables, GitHubTraceOptions options) : IOriginResolver
{
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

        var name = layer.Reference switch
        {
            EnvironmentVariableRef env => env.Name,
            WorkloadEnvRef env => env.Name,
            ConfigMapKeyRef cm => cm.Key,
            SecretKeyRef s => s.Key,
            _ => null,
        };
        if (name is null)
        {
            return [];
        }

        var mapping = options.FindMapping(name);
        IReadOnlyList<GitHubVariable> found;
        if (mapping is not null)
        {
            found = variables.Find(mapping.VariableName, mapping.Kind);
            if (found.Count == 0)
            {
                var what = mapping.Kind == GitHubVariableKind.Secret ? "secrets" : "vars";
                diagnostics.Warn($"{name} is linked to {what}.{mapping.VariableName}{(mapping.Where is null ? string.Empty : $" ({mapping.Where})")}, but no such GitHub {(mapping.Kind == GitHubVariableKind.Secret ? "secret" : "variable")} was loaded.");
                return [];
            }
        }
        else if (options.MatchByName)
        {
            found = variables.Find(name, GitHubVariableKind.Variable);
            if (found.Count == 0)
            {
                found = variables.Find(name, GitHubVariableKind.Secret);
            }
        }
        else
        {
            return [];
        }

        return found.Select(v => ToStep(v, layer, mapping)).ToList();
    }

    private static ProvenanceStep ToStep(GitHubVariable variable, ProvenanceStep layer, GitHubMapping? mapping)
    {
        var secret = variable.Kind == GitHubVariableKind.Secret;
        var notes = new List<string>();
        if (mapping?.Where is not null)
        {
            notes.Add($"{mapping.Where} sets {mapping.Name} from ${{{{ {(secret ? "secrets" : "vars")}.{mapping.VariableName} }}}}");
        }
        else if (mapping is null)
        {
            notes.Add("matched by name");
        }

        if (secret)
        {
            notes.Add("GitHub does not return secret values");
        }
        else if (layer.Value is not null && variable.Value is not null && !string.Equals(layer.Value, variable.Value, StringComparison.Ordinal))
        {
            // Never print either value here: notes are not redacted.
            notes.Add("the GitHub value differs from the value seen here, so the deployment may be out of date or the value was transformed");
        }

        return new ProvenanceStep
        {
            Kind = LayerKind.GitHubActions,
            Source = $"GitHub {GitHubVariables.Describe(variable.Scope)} {(secret ? "secret" : "variable")} {variable.Name}",
            Location = variable.Owner,
            Value = variable.Value,
            IsSensitive = secret,
            IsInferred = mapping is null,
            Note = string.Join("; ", notes),
            Reference = new GitHubVariableRef(variable.Kind, variable.Scope, variable.Owner, variable.Name),
        };
    }
}
