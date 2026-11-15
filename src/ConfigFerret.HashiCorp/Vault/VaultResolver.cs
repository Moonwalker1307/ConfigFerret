using System.Globalization;
using ConfigFerret.Sources;

namespace ConfigFerret.HashiCorp;

/// <summary>
/// Links External Secrets Operator and Secrets Store CSI references, explicit links and same-name layers to HashiCorp
/// Vault KV secrets and the keys inside them.
/// </summary>
internal sealed class VaultResolver : IOriginResolver
{
    private readonly VaultSecrets _secrets;
    private readonly VaultTraceOptions _options;
    private readonly Dictionary<string, List<(VaultSecret Secret, string? Key)>> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Section, VaultSecret Secret)> _sections = [];
    private bool _warned;

    public VaultResolver(VaultSecrets secrets, VaultTraceOptions options)
    {
        _secrets = secrets;
        _options = options;
        foreach (var secret in secrets.Secrets)
        {
            var section = LayerNames.ToConfigurationKey(secret.RelativePath, "/");
            if (secret.Data is { Count: > 0 } data)
            {
                foreach (var key in data.Keys)
                {
                    Index(NameMatching.Combine(section, LayerNames.ToConfigurationKey(key)), secret, key);
                }
            }
            else if (section.Length > 0)
            {
                // Keys were not read: the secret may be one setting, or a section holding several.
                Index(section, secret, null);
                _sections.Add((section, secret));
            }
        }
    }

    /// <summary>The entries, one per key when the data was read and one per secret otherwise, matching the steps' references.</summary>
    public IEnumerable<SourceEntry> Entries() => _secrets.Secrets.SelectMany(secret =>
        secret.Data is { Count: > 0 } data
            ? data.Keys.Select(k => new SourceEntry($"{secret.Path}#{k}", Reference(secret, k), true, Detail(secret), EntryLocation))
            : [new SourceEntry(secret.Path, Reference(secret, null), true, Detail(secret), EntryLocation)]);

    public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!_warned)
        {
            _warned = true;
            foreach (var warning in _secrets.Warnings)
            {
                diagnostics.Warn(warning);
            }
        }

        if (layer.Reference is ExternalSecretRef external)
        {
            return ResolveExternal(external, layer, diagnostics);
        }

        if (LayerNames.Of(layer) is { } name && _options.FindMapping(name) is { } mapping)
        {
            var mapped = _secrets.Find(mapping.Path);
            if (mapped is null)
            {
                diagnostics.Warn($"{name} is linked to HashiCorp Vault secret {mapping.Path}, but no such secret was loaded.");
                return [];
            }

            return Steps(mapped, mapping.Key, layer, "linked explicitly", inferred: false, version: null, diagnostics, name);
        }

        if (!_options.MatchByName)
        {
            return [];
        }

        var candidates = NameMatching.CandidateKeys(layer, _options.StripPrefixes, "Vault");
        foreach (var key in candidates)
        {
            if (_byKey.TryGetValue(key, out var found))
            {
                return found.Select(f => ToStep(f.Secret, f.Key, layer, ["matched by name"], inferred: true)).ToList();
            }
        }

        // A secret whose keys were not read, named like a section of the key: Db for Db:Timeout.
        foreach (var key in candidates)
        {
            var section = _sections
                .Where(s => key.Length > s.Section.Length && key[s.Section.Length] == ':' && key.StartsWith(s.Section, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.Section.Length)
                .FirstOrDefault();
            if (section.Secret is not null)
            {
                var rest = key[(section.Section.Length + 1)..];
                return [ToStep(section.Secret, null, layer, [$"matched by name: the secret may hold {rest}, but its keys were not read (IncludeValues is off)"], inferred: true)];
            }
        }

        return [];
    }

    private List<ProvenanceStep> ResolveExternal(ExternalSecretRef external, ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        var explicitVault = string.Equals(external.Provider, "vault", StringComparison.OrdinalIgnoreCase);
        if (external.Provider is not null && !explicitVault)
        {
            return [];
        }

        var secret = FindExternal(external.RemoteKey);
        if (secret is null)
        {
            if (explicitVault)
            {
                diagnostics.Warn($"{layer.Source} reads HashiCorp Vault secret {external.RemoteKey}, which was not loaded from mount {_secrets.Mount}.");
            }

            return [];
        }

        var how = external.Store is null ? "read by an external secret reference" : $"read through secret store {external.Store}";
        return Steps(secret, string.IsNullOrEmpty(external.Property) ? null : external.Property, layer, how, inferred: false, external.Version, diagnostics, layer.Source);
    }

    // External Secrets Operator keys are relative to the mount; CSI secretPath values hold the mount and, for KV
    // version 2, data/: secret/data/orders/db.
    private VaultSecret? FindExternal(string remoteKey)
    {
        var path = VaultSecrets.Normalize(remoteKey);
        if (_secrets.Find(path) is { } direct)
        {
            return direct;
        }

        var mountPrefix = _secrets.Mount + "/";
        if (!path.StartsWith(mountPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        path = path[mountPrefix.Length..];
        return _secrets.Find(path) ?? (path.StartsWith("data/", StringComparison.Ordinal) ? _secrets.Find(path["data/".Length..]) : null);
    }

    private List<ProvenanceStep> Steps(VaultSecret secret, string? key, ProvenanceStep layer, string how, bool inferred, string? version, TraceDiagnostics diagnostics, string who)
    {
        var notes = new List<string> { how };
        if (version is not null && secret.Version is { } current && !string.Equals(version, current.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"pinned to version {version}, while the current version is {current}"));
        }

        if (secret.Data is { Count: > 0 } data)
        {
            if (key is null)
            {
                return data.Keys.Select(k => ToStep(secret, k, layer, notes, inferred)).ToList();
            }

            if (!data.ContainsKey(key))
            {
                diagnostics.Warn($"{who} reads key {key} of HashiCorp Vault secret {secret.Path}, but the secret has no such key.");
                return [];
            }

            return [ToStep(secret, key, layer, notes, inferred)];
        }

        if (key is not null)
        {
            notes.Add(secret.Data is null
                ? $"key {key} was not checked, because secret values were not read (IncludeValues is off)"
                : $"the secret has no keys, so key {key} is missing");
        }

        return [ToStep(secret, null, layer, notes, inferred, key)];
    }

    private ProvenanceStep ToStep(VaultSecret secret, string? key, ProvenanceStep layer, IEnumerable<string> how, bool inferred, string? wantedKey = null)
    {
        var notes = new List<string>(how);
        if (Detail(secret) is { } detail)
        {
            notes.Add(detail);
        }

        string? value = null;
        if (key is not null && secret.Data is { } data && data.TryGetValue(key, out value)
            && layer.Value is not null && value is not null && !string.Equals(layer.Value, value, StringComparison.Ordinal))
        {
            // Never print either value here: notes are not redacted.
            notes.Add("the Vault value differs from the value seen here, so the deployment may be out of date or the value was transformed");
        }

        var shown = key ?? wantedKey;
        return new ProvenanceStep
        {
            Kind = LayerKind.HashiCorpVault,
            Source = $"HashiCorp Vault secret {_secrets.Mount}/{secret.Path}{(shown is null ? string.Empty : " key " + shown)}",
            Location = _secrets.Location,
            Value = value,
            IsSensitive = true,
            IsInferred = inferred,
            Note = string.Join("; ", notes),
            Reference = Reference(secret, key),
        };
    }

    private string EntryLocation => $"mount {_secrets.Mount} at {_secrets.Location}";

    private VaultSecretRef Reference(VaultSecret secret, string? key) => new(_secrets.Mount, secret.Path, key);

    private static string? Detail(VaultSecret secret)
    {
        var parts = new List<string>();
        if (secret.Version is { } version)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"version {version}"));
        }

        if (secret.UpdatedTime is { } updated)
        {
            parts.Add("updated " + updated.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture));
        }

        if (secret.IsDeleted)
        {
            parts.Add("the current version is deleted");
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private void Index(string key, VaultSecret secret, string? dataKey)
    {
        if (key.Length == 0)
        {
            return;
        }

        if (!_byKey.TryGetValue(key, out var list))
        {
            _byKey[key] = list = [];
        }

        list.Add((secret, dataKey));
    }
}
