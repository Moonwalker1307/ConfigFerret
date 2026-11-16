using System.Globalization;
using System.Text.Json;
using ConfigFerret.Sources;

namespace ConfigFerret.GoogleCloud;

/// <summary>
/// Links External Secrets Operator and Secrets Store CSI references, explicit links and same-name layers to Google Cloud
/// Secret Manager secrets.
/// </summary>
internal sealed class GcpSecretManagerResolver : IOriginResolver
{
    private static readonly string[] ProviderHints = ["Secret Manager", "SecretManager", "Google", "GCP"];

    private readonly GcpSecrets _secrets;
    private readonly GcpSecretManagerTraceOptions _options;
    private readonly Dictionary<string, List<GcpSecret>> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private bool _warned;

    public GcpSecretManagerResolver(GcpSecrets secrets, GcpSecretManagerTraceOptions options)
    {
        _secrets = secrets;
        _options = options;
        foreach (var secret in secrets.Secrets)
        {
            var key = LayerNames.ToConfigurationKey(StripPrefix(secret.Id), _options.Separator);
            if (key.Length == 0)
            {
                continue;
            }

            if (!_byKey.TryGetValue(key, out var list))
            {
                _byKey[key] = list = [];
            }

            list.Add(secret);
        }
    }

    /// <summary>One entry per secret, matching the steps' references.</summary>
    public IEnumerable<SourceEntry> Entries() =>
        _secrets.Secrets.Select(s => new SourceEntry(s.Id, Reference(s), IsSensitive: true, Detail(s), _secrets.Where));

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
            if (_secrets.Find(mapping.SecretId) is not { } mapped)
            {
                diagnostics.Warn($"{name} is linked to Google Cloud secret {mapping.SecretId}, but no such secret was loaded from {_secrets.Where}.");
                return [];
            }

            return [Step(mapped, mapping.Property, layer, "linked explicitly", inferred: false, version: null, diagnostics, name)];
        }

        if (!_options.MatchByName)
        {
            return [];
        }

        foreach (var key in CandidateKeys(layer))
        {
            if (_byKey.TryGetValue(key, out var found))
            {
                return found.Select(s => Step(s, null, layer, "matched by name", inferred: true, version: null, diagnostics, key)).ToList();
            }
        }

        return [];
    }

    private List<ProvenanceStep> ResolveExternal(ExternalSecretRef external, ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        var isGcp = external.Provider is { } provider
            && (provider.Equals("gcpsm", StringComparison.OrdinalIgnoreCase) || provider.Equals("gcp", StringComparison.OrdinalIgnoreCase));
        if (external.Provider is not null && !isGcp)
        {
            return [];
        }

        if (!TryParseRemoteKey(external.RemoteKey, out var resource))
        {
            if (isGcp)
            {
                diagnostics.Warn($"{layer.Source} reads Google Cloud secret {external.RemoteKey}, which is not a secret id or a projects/.../secrets/... name.");
            }

            return [];
        }

        var secret = _secrets.Find(resource.SecretId);
        if (secret is not null && resource.Project is not null
            && !(string.Equals(resource.Project, _secrets.ProjectId, StringComparison.OrdinalIgnoreCase) && string.Equals(resource.Location, _secrets.Location, StringComparison.OrdinalIgnoreCase))
            && !(string.Equals(resource.Project, secret.Project, StringComparison.OrdinalIgnoreCase) && string.Equals(resource.Location, secret.Location, StringComparison.OrdinalIgnoreCase)))
        {
            // The same id in another project or location is another secret.
            secret = null;
        }

        if (secret is null)
        {
            if (isGcp)
            {
                diagnostics.Warn($"{layer.Source} reads Google Cloud secret {external.RemoteKey}, which was not loaded from {_secrets.Where}.");
            }

            return [];
        }

        var how = external.Store is null ? "read by an external secret reference" : $"read through secret store {external.Store}";
        var property = string.IsNullOrEmpty(external.Property) ? null : external.Property;
        var version = string.IsNullOrEmpty(external.Version) ? resource.Version : external.Version;
        return [Step(secret, property, layer, how, inferred: false, version, diagnostics, layer.Source)];
    }

    /// <summary>
    /// Reads a remote key: a secret id, or a resource name <c>projects/P/[locations/L/]secrets/S[/versions/V]</c> as
    /// Secrets Store CSI <c>resourceName</c> values hold.
    /// </summary>
    internal static bool TryParseRemoteKey(string remoteKey, out (string? Project, string? Location, string SecretId, string? Version) resource)
    {
        var key = remoteKey.Trim();
        resource = default;
        if (!key.Contains('/', StringComparison.Ordinal))
        {
            resource = (null, null, key, null);
            return key.Length > 0;
        }

        string[] parts = key.Split('/');
        if (parts.Any(p => p.Length == 0))
        {
            return false;
        }

        resource = parts switch
        {
            ["projects", var p, "secrets", var s] => (p, null, s, null),
            ["projects", var p, "secrets", var s, "versions", var v] => (p, null, s, v),
            ["projects", var p, "locations", var l, "secrets", var s] => (p, l, s, null),
            ["projects", var p, "locations", var l, "secrets", var s, "versions", var v] => (p, l, s, v),
            _ => default,
        };
        return resource.SecretId is not null;
    }

    private List<string> CandidateKeys(ProvenanceStep layer)
    {
        var keys = new List<string>();
        if (LayerNames.Of(layer) is { } name)
        {
            Add(keys, name);
            foreach (var prefix in _options.StripPrefixes.Where(p => !string.IsNullOrEmpty(p) && name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                Add(keys, name[prefix.Length..]);
            }
        }
        else if (layer.Reference is null
            && layer.ConfigurationKey is { } key
            && ProviderHints.Any(h => layer.Source.Contains(h, StringComparison.OrdinalIgnoreCase)))
        {
            // A configuration provider that reads Secret Manager directly sets the configuration key itself.
            Add(keys, key);
        }

        return keys;
    }

    private static void Add(List<string> keys, string name)
    {
        var key = LayerNames.ToConfigurationKey(name);
        if (key.Length > 0 && !keys.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            keys.Add(key);
        }
    }

    private string StripPrefix(string id) =>
        _options.StripSecretPrefix && _secrets.Prefix is { } prefix && id.Length > prefix.Length && id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? id[prefix.Length..]
            : id;

    private ProvenanceStep Step(GcpSecret secret, string? property, ProvenanceStep layer, string how, bool inferred, string? version, TraceDiagnostics diagnostics, string who)
    {
        var notes = new List<string> { how };
        var pinned = version is null || version.Equals("latest", StringComparison.OrdinalIgnoreCase) ? null : version;
        if (pinned is not null)
        {
            notes.Add(DescribePin(secret, pinned));
        }

        var applies = secret.ValueVersion is not null && (pinned is null || pinned == secret.ValueVersion);
        string? value = null;
        if (property is not null)
        {
            if (secret.ValueVersion is null)
            {
                notes.Add($"property {property} was not checked, because secret values were not read (IncludeValues is off)");
            }
            else if (!applies)
            {
                notes.Add($"property {property} was not checked, because only version {secret.ValueVersion} was read");
            }
            else if (secret.Value is null)
            {
                notes.Add($"property {property} was not checked, because the value is not text");
            }
            else if (TryReadProperty(secret.Value, property, out var found))
            {
                value = found;
            }
            else
            {
                diagnostics.Warn($"{who} reads property {property} of Google Cloud secret {secret.Id}, but its value is not a JSON object with that property.");
                notes.Add($"the value has no property {property}");
            }
        }
        else if (applies)
        {
            value = secret.Value;
            if (secret.IsBinary)
            {
                notes.Add("the value is not text, so it was not compared");
            }
        }

        if (layer.Value is not null && value is not null && !string.Equals(layer.Value, value, StringComparison.Ordinal))
        {
            // Never print either value here: notes are not redacted.
            notes.Add("the Secret Manager value differs from the value seen here, so the deployment may be out of date or the value was transformed");
        }

        if (Detail(secret) is { } detail)
        {
            notes.Add(detail);
        }

        return new ProvenanceStep
        {
            Kind = LayerKind.GcpSecretManager,
            Source = $"Google Cloud Secret Manager secret {secret.Id}{(property is null ? string.Empty : " property " + property)} ({_secrets.ProjectId}{(_secrets.Location is null ? string.Empty : "/" + _secrets.Location)})",
            Location = secret.Name,
            Value = value,
            IsSensitive = true,
            IsInferred = inferred,
            Note = string.Join("; ", notes),
            Reference = Reference(secret),
        };
    }

    private static string DescribePin(GcpSecret secret, string pinned)
    {
        if (secret.Versions is null)
        {
            return $"pinned to version {pinned}";
        }

        var found = secret.Versions.FirstOrDefault(v => v.Id == pinned);
        if (found is null)
        {
            return $"pinned to version {pinned}, which was not among the versions read";
        }

        if (found.State != GcpSecretVersionState.Enabled)
        {
            return $"pinned to version {pinned}, which is {found.State.ToString().ToLowerInvariant()}";
        }

        return secret.LatestEnabledVersion is { } latest && latest != pinned
            ? $"pinned to version {pinned}, while the latest enabled version is {latest}"
            : $"pinned to version {pinned}, the latest enabled version";
    }

    // A property of a JSON object value: a top-level name first, then a dotted path (a.b), as External Secrets Operator reads it.
    private static bool TryReadProperty(string value, string property, out string? text)
    {
        text = null;
        try
        {
            using var doc = JsonDocument.Parse(value);
            var found = JsonRead.Property(doc.RootElement, property);
            if (found is null)
            {
                JsonElement? current = doc.RootElement;
                foreach (var part in property.Split('.'))
                {
                    current = current is { } c ? JsonRead.Property(c, part) : null;
                }

                found = current;
            }

            if (found is not { } element)
            {
                return false;
            }

            // A boolean reaches the Kubernetes Secret as its JSON text (true), not as the .NET text (True).
            text = element.ValueKind is JsonValueKind.True or JsonValueKind.False ? element.GetRawText() : JsonRead.Text(element);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static GcpSecretRef Reference(GcpSecret secret) => new(secret.Project, secret.Location, secret.Id);

    private static string? Detail(GcpSecret secret)
    {
        var parts = new List<string>();
        if (secret.Versions is not null)
        {
            parts.Add(secret.LatestEnabledVersion is { } latest ? $"latest enabled version {latest}" : "no enabled version");
        }

        if (secret.CreateTime is { } created)
        {
            parts.Add("created " + created.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture));
        }

        if (secret.Replication is { } replication)
        {
            parts.Add("replication " + replication);
        }

        if (secret.Labels.Count > 0)
        {
            parts.Add("labels " + string.Join(", ", secret.Labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"{l.Key}={l.Value}")));
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
