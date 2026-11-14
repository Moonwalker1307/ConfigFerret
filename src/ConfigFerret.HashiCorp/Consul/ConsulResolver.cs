using System.Globalization;
using ConfigFerret.Sources;

namespace ConfigFerret.HashiCorp;

/// <summary>Links explicit links and same-name layers to Consul KV keys.</summary>
internal sealed class ConsulResolver(ConsulKeyValues values, ConsulTraceOptions options) : IOriginResolver
{
    private bool _warned;

    /// <summary>One entry per key (or per JSON leaf), matching the steps' references.</summary>
    public IEnumerable<SourceEntry> Entries() => values.Entries.Select(e => new SourceEntry(
        e.JsonPath is null ? e.Key : $"{e.Key}#{e.JsonPath}",
        Reference(e),
        values.IsSensitive,
        $"configuration key {e.ConfigurationKey}",
        values.Location));

    public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!_warned)
        {
            _warned = true;
            foreach (var warning in values.Warnings)
            {
                diagnostics.Warn(warning);
            }
        }

        if (layer.Reference is ExternalSecretRef)
        {
            return [];
        }

        if (LayerNames.Of(layer) is { } name && options.FindMapping(name) is { } mapping)
        {
            var linked = values.Entries
                .Where(e => (string.Equals(e.Key, mapping.Key, StringComparison.Ordinal) || string.Equals(e.Key, values.Prefix + mapping.Key, StringComparison.Ordinal))
                    && (mapping.JsonPath is null || string.Equals(e.JsonPath, mapping.JsonPath, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (linked.Count == 0)
            {
                diagnostics.Warn($"{name} is linked to Consul key {mapping.Key}{(mapping.JsonPath is null ? string.Empty : "#" + mapping.JsonPath)}, but no such key was loaded.");
                return [];
            }

            return linked.Select(e => ToStep(e, layer, "linked explicitly", inferred: false)).ToList();
        }

        if (!options.MatchByName)
        {
            return [];
        }

        var found = NameMatching.CandidateKeys(layer, options.StripPrefixes, "Consul")
            .Select(values.Find)
            .FirstOrDefault(f => f.Count > 0);
        return found is null ? [] : found.Select(e => ToStep(e, layer, "matched by name", inferred: true)).ToList();
    }

    private ProvenanceStep ToStep(ConsulEntry entry, ProvenanceStep layer, string how, bool inferred)
    {
        var notes = new List<string> { how };
        if (entry.JsonPath is not null)
        {
            notes.Add($"from {entry.JsonPath} in the key's JSON value");
        }

        if (entry.ModifyIndex is { } index)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"modify index {index}"));
        }

        if (layer.Value is not null && entry.Value is not null && !string.Equals(layer.Value, entry.Value, StringComparison.Ordinal))
        {
            // Never print either value here: notes are not redacted.
            notes.Add("the Consul value differs from the value seen here, so the deployment may be out of date or the value was transformed");
        }

        return new ProvenanceStep
        {
            Kind = LayerKind.Consul,
            Source = $"Consul key {entry.Key}",
            Location = values.Location,
            Value = entry.Value,
            IsSensitive = values.IsSensitive,
            IsInferred = inferred,
            Note = string.Join("; ", notes),
            Reference = Reference(entry),
        };
    }

    private ConsulKeyRef Reference(ConsulEntry entry) => new(values.Datacenter, entry.Key, entry.JsonPath);
}
