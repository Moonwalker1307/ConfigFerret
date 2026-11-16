using System.Text.RegularExpressions;
using ConfigFerret.Sources;

namespace ConfigFerret.Terraform;

/// <summary>Links environment variables, Kubernetes env entries and ConfigMap/Secret keys to Terraform outputs.</summary>
internal sealed partial class TerraformResolver(TerraformOutputs outputs, TerraformTraceOptions options) : IOriginResolver
{
    private bool _warned;

    public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!_warned)
        {
            _warned = true;
            foreach (var warning in outputs.Warnings)
            {
                diagnostics.Warn(warning);
            }
        }

        var name = layer.Reference is ExternalSecretRef ? null : LayerNames.Of(layer);
        if (name is null)
        {
            return [];
        }

        if (options.Links.TryGetValue(name, out var link) && !string.IsNullOrWhiteSpace(link))
        {
            var linked = outputs.Find(link.Trim());
            if (linked is null)
            {
                diagnostics.Warn($"{name} is linked to Terraform output {link.Trim()}, but no such output was loaded.");
                return [];
            }

            return [ToStep(linked, layer, inferred: false)];
        }

        var found = options.MatchByName ? Match(name) : null;
        return found is null ? [] : [ToStep(found, layer, inferred: true)];
    }

    /// <summary>The output whose name matches: exactly, then ignoring case, then with any separator.</summary>
    internal TerraformOutput? Match(string name)
    {
        var values = outputs.Values;
        var loose = Loose(name);
        return values.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.Ordinal) || string.Equals(Colon(v.Name), name, StringComparison.Ordinal))
            ?? values.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(Colon(v.Name), name, StringComparison.OrdinalIgnoreCase))
            ?? values.FirstOrDefault(v => string.Equals(Loose(v.Name), loose, StringComparison.Ordinal));
    }

    private static string Colon(string name) => name.Replace('.', ':');

    // db_timeout, DB__TIMEOUT, Db:Timeout and db.timeout all become "db:timeout".
    private static string Loose(string name) =>
        name.ToLowerInvariant().Replace("__", ":", StringComparison.Ordinal).Replace('_', ':').Replace('.', ':').Replace('-', ':');

    private ProvenanceStep ToStep(TerraformOutput output, ProvenanceStep layer, bool inferred)
    {
        var notes = new List<string>();
        if (inferred)
        {
            notes.Add("matched by name");
        }

        var definition = outputs.Definitions.GetValueOrDefault(output.Output);
        // Notes are not redacted, and whether the key is secret by its name is only decided after the resolvers ran,
        // so the expression is shown only when it holds no literal, which could be the value itself.
        if (definition?.Expression is { } expression && !output.IsSensitive && !Literal().IsMatch(expression))
        {
            notes.Add($"value = {expression}");
        }

        if (output.IsSensitive)
        {
            notes.Add("sensitive output");
        }

        if (layer.Value is not null && output.Value is not null && !string.Equals(layer.Value, output.Value, StringComparison.Ordinal))
        {
            // Never print either value here: notes are not redacted.
            notes.Add("the Terraform value differs from the value seen here, so the deployment may be out of date or the value was transformed");
        }

        return new ProvenanceStep
        {
            Kind = LayerKind.Terraform,
            Source = $"Terraform output {output.Name}",
            Location = definition is null ? output.Source : $"{definition.File}:{definition.Line}",
            Value = output.Value,
            IsSensitive = output.IsSensitive,
            IsInferred = inferred,
            Note = notes.Count == 0 ? null : string.Join("; ", notes),
            Reference = new TerraformOutputRef(output.Output, output.Path),
        };
    }

    // A string or a number. A digit in a name or in an index such as [0] or .0 is not one.
    [GeneratedRegex(@"[""]|(?<![\w.\[])\d", RegexOptions.CultureInvariant)]
    private static partial Regex Literal();
}
