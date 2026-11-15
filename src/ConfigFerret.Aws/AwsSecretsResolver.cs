using ConfigFerret.Sources;

namespace ConfigFerret.Aws;

/// <summary>
/// Links External Secrets Operator references, environment variables, Kubernetes env entries, ConfigMap/Secret keys and
/// keys set by a Secrets Manager configuration provider to AWS Secrets Manager secrets and their JSON properties.
/// </summary>
internal sealed class AwsSecretsResolver : IOriginResolver
{
    private readonly AwsSecrets _secrets;
    private readonly AwsSecretsTraceOptions _options;
    private readonly Dictionary<string, List<(AwsSecret Secret, string? Property)>> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private bool _warned;

    public AwsSecretsResolver(AwsSecrets secrets, AwsSecretsTraceOptions options)
    {
        _secrets = secrets;
        _options = options;
        var prefix = string.IsNullOrEmpty(options.KeyPrefix) ? secrets.NamePrefix : options.KeyPrefix;
        foreach (var secret in secrets.Entries)
        {
            if (ToKey(secret.Name, prefix) is not { } key)
            {
                continue;
            }

            Add(key, secret, null);
            foreach (var property in secret.Properties?.Keys ?? [])
            {
                Add(key.Length == 0 ? property : $"{key}:{property}", secret, property);
            }
        }
    }

    /// <summary>
    /// The .NET key a secret name maps to: the prefix is removed, and <c>/</c> and <c>__</c> become <c>:</c>.
    /// Null when the name does not start with the prefix.
    /// </summary>
    internal static string? ToKey(string name, string? prefix)
    {
        if (!string.IsNullOrEmpty(prefix))
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            name = name[prefix.Length..];
        }

        return LayerNames.ToConfigurationKey(name, "/");
    }

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

        var name = LayerNames.Of(layer);
        var providerKey = FromAwsProvider(layer) ? layer.ConfigurationKey : null;
        var link = _options.FindLink(name) ?? _options.FindLink(providerKey);
        if (link is var (secretName, property))
        {
            var secret = _secrets.Find(secretName);
            if (secret is null)
            {
                diagnostics.Warn($"{name ?? providerKey} is linked to AWS secret {secretName}, but no such secret was loaded.");
                return [];
            }

            return [ToStep(secret, property, layer, "linked explicitly", inferred: false, diagnostics)];
        }

        if (name is not null && _options.MatchByName)
        {
            if (_secrets.Find(name) is { } same)
            {
                return [ToStep(same, null, layer, "matched by name", inferred: true, diagnostics)];
            }

            if (ByKey(LayerNames.ToConfigurationKey(name), layer, "matched by .NET key", diagnostics) is { Count: > 0 } byKey)
            {
                return byKey;
            }
        }

        if (providerKey is not null && _options.MatchConfigurationKeys)
        {
            return ByKey(providerKey, layer, "the key this secret maps to in the Secrets Manager configuration provider", diagnostics);
        }

        return [];
    }

    private void Add(string key, AwsSecret secret, string? property)
    {
        if (!_byKey.TryGetValue(key, out var list))
        {
            _byKey[key] = list = [];
        }

        list.Add((secret, property));
    }

    // Kralizek's SecretsManagerConfigurationProvider, or Amazon's SystemsManagerConfigurationProvider, which also reads
    // secrets through /aws/reference/secretsmanager/.
    private static bool FromAwsProvider(ProvenanceStep layer) =>
        layer.Reference is null
        && layer.ConfigurationKey is not null
        && (layer.Source.Contains("SecretsManager", StringComparison.OrdinalIgnoreCase)
            || layer.Source.Contains("SystemsManager", StringComparison.OrdinalIgnoreCase));

    private List<ProvenanceStep> ByKey(string key, ProvenanceStep layer, string how, TraceDiagnostics diagnostics) =>
        _byKey.TryGetValue(key, out var found)
            ? found.Select(f => ToStep(f.Secret, f.Property, layer, how, inferred: true, diagnostics)).ToList()
            : [];

    private IReadOnlyList<ProvenanceStep> ResolveExternal(ExternalSecretRef external, ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        var isAws = string.Equals(external.Provider, "aws", StringComparison.OrdinalIgnoreCase);
        if (!isAws && external.Provider is not null)
        {
            return [];
        }

        var secret = _secrets.Find(external.RemoteKey);
        if (secret is null)
        {
            // A key starting with / or an ssm ARN is a Parameter Store parameter, which the Parameter Store resolver links.
            if (isAws && !external.RemoteKey.StartsWith('/') && !external.RemoteKey.StartsWith("arn:aws:ssm:", StringComparison.Ordinal))
            {
                diagnostics.Warn($"An external secret reference{(external.Store is null ? string.Empty : $" (store {external.Store})")} points to AWS secret {external.RemoteKey}, but no such secret was loaded.");
            }

            return [];
        }

        var how = external.Store is null ? "external secret reference" : $"external secret reference from store {external.Store}";
        if (!string.IsNullOrEmpty(external.Version))
        {
            how += $"; pinned to version {external.Version}, while ConfigFerret read the current version";
        }

        return [ToStep(secret, string.IsNullOrEmpty(external.Property) ? null : external.Property, layer, how, inferred: false, diagnostics)];
    }

    private ProvenanceStep ToStep(AwsSecret secret, string? property, ProvenanceStep layer, string how, bool inferred, TraceDiagnostics diagnostics)
    {
        var notes = new List<string> { how };
        string? value;
        if (property is null)
        {
            value = secret.Value;
        }
        else if (JsonSecret.FindProperty(secret.Properties, property) is { } found)
        {
            property = found;
            value = secret.Properties![found];
        }
        else
        {
            value = null;
            if (secret.Value is not null)
            {
                // Property names are not secret, but the value is: only name the property.
                notes.Add($"the secret has no JSON property {property}");
                diagnostics.Warn($"AWS secret {secret.Name} has no JSON property {property}.");
            }
        }

        if (secret.IsBinary)
        {
            notes.Add("binary secret, its value is not shown");
        }
        else if (secret.Value is null && !secret.ValueRead)
        {
            notes.Add("the value was not read (turn on IncludeValues to read it)");
        }
        else if (layer.Value is not null && value is not null && !string.Equals(layer.Value, value, StringComparison.Ordinal))
        {
            // Never print either value here: notes are not redacted.
            notes.Add("the AWS value differs from the value seen here, so the deployment may be out of date or the value was transformed");
        }

        return new ProvenanceStep
        {
            Kind = LayerKind.AwsSecretsManager,
            Source = property is null ? $"AWS Secrets Manager secret {secret.Name}" : $"AWS Secrets Manager secret {secret.Name}, property {property}",
            Location = secret.Location ?? _secrets.Location,
            Value = value,
            IsSensitive = true,
            IsInferred = inferred,
            Note = string.Join("; ", notes),
            Reference = new AwsSecretRef(secret.Name, property),
        };
    }
}
