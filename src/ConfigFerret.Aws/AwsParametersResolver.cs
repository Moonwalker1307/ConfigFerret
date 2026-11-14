using System.Globalization;
using ConfigFerret.Sources;

namespace ConfigFerret.Aws;

/// <summary>
/// Links External Secrets Operator references, environment variables, Kubernetes env entries, ConfigMap/Secret keys and
/// keys set by <c>AddSystemsManager</c> to Parameter Store parameters.
/// </summary>
internal sealed class AwsParametersResolver : IOriginResolver
{
    private readonly AwsParameters _parameters;
    private readonly AwsParametersTraceOptions _options;
    private readonly Dictionary<string, List<(AwsParameter Parameter, int? Item)>> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private bool _warned;

    public AwsParametersResolver(AwsParameters parameters, AwsParametersTraceOptions options)
    {
        _parameters = parameters;
        _options = options;
        foreach (var parameter in parameters.Entries)
        {
            var key = parameters.ToConfigurationKey(parameter);
            if (key.Length == 0)
            {
                continue;
            }

            Add(key, parameter, null);
            var items = parameter.Items;
            for (var i = 0; i < items.Count; i++)
            {
                Add(string.Create(CultureInfo.InvariantCulture, $"{key}:{i}"), parameter, i);
            }
        }
    }

    public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!_warned)
        {
            _warned = true;
            foreach (var warning in _parameters.Warnings)
            {
                diagnostics.Warn(warning);
            }
        }

        if (layer.Reference is ExternalSecretRef external)
        {
            return ResolveExternal(external, layer, diagnostics);
        }

        var name = LayerNames.Of(layer);
        var providerKey = FromSystemsManager(layer) ? layer.ConfigurationKey : null;
        if ((_options.FindLink(name) ?? _options.FindLink(providerKey)) is { } link)
        {
            if (_parameters.Find(link) is not { } linked)
            {
                diagnostics.Warn($"{name ?? providerKey} is linked to AWS parameter {link}, but no such parameter was loaded.");
                return [];
            }

            return [ToStep(linked, null, null, layer, "linked explicitly", inferred: false)];
        }

        if (name is not null && _options.MatchByName)
        {
            if (_parameters.Find(name) is { } same)
            {
                return [ToStep(same, null, null, layer, "matched by name", inferred: true)];
            }

            if (ByKey(LayerNames.ToConfigurationKey(name), layer, "matched by .NET key") is { Count: > 0 } byKey)
            {
                return byKey;
            }
        }

        if (providerKey is not null && _options.MatchConfigurationKeys)
        {
            return ByKey(providerKey, layer, $"the key AddSystemsManager(\"{_parameters.Path}\") gives this parameter");
        }

        return [];
    }

    private static bool FromSystemsManager(ProvenanceStep layer) =>
        layer.Reference is null
        && layer.ConfigurationKey is not null
        && layer.Source.Contains("SystemsManager", StringComparison.OrdinalIgnoreCase);

    private void Add(string key, AwsParameter parameter, int? item)
    {
        if (!_byKey.TryGetValue(key, out var list))
        {
            _byKey[key] = list = [];
        }

        list.Add((parameter, item));
    }

    private List<ProvenanceStep> ByKey(string key, ProvenanceStep layer, string how) =>
        _byKey.TryGetValue(key, out var found)
            ? found.Select(f => ToStep(f.Parameter, f.Item, null, layer, how, inferred: true)).ToList()
            : [];

    private IReadOnlyList<ProvenanceStep> ResolveExternal(ExternalSecretRef external, ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        var isAws = string.Equals(external.Provider, "aws", StringComparison.OrdinalIgnoreCase);
        var key = external.RemoteKey;
        if ((!isAws && external.Provider is not null) || !(key.StartsWith('/') || key.StartsWith("arn:aws:ssm:", StringComparison.Ordinal)))
        {
            return [];
        }

        if (_parameters.Find(key) is not { } parameter)
        {
            if (isAws && key.StartsWith(_parameters.Path, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Warn($"An external secret reference{(external.Store is null ? string.Empty : $" (store {external.Store})")} points to AWS parameter {key}, but no such parameter was loaded.");
            }

            return [];
        }

        var how = external.Store is null ? "external secret reference" : $"external secret reference from store {external.Store}";
        if (!string.IsNullOrEmpty(external.Version))
        {
            how += $"; pinned to version {external.Version}, while ConfigFerret read the current version";
        }

        return [ToStep(parameter, null, string.IsNullOrEmpty(external.Property) ? null : external.Property, layer, how, inferred: false)];
    }

    private ProvenanceStep ToStep(AwsParameter parameter, int? item, string? property, ProvenanceStep layer, string how, bool inferred)
    {
        var notes = new List<string> { how };
        var value = parameter.Value;
        var source = $"AWS Parameter Store {Describe(parameter.Type)} {parameter.Name}";
        if (item is { } i)
        {
            value = parameter.Items[i];
            source += string.Create(CultureInfo.InvariantCulture, $", item {i}");
        }
        else if (property is not null)
        {
            var properties = JsonSecret.Flatten(value);
            var found = JsonSecret.FindProperty(properties, property);
            value = found is null ? null : properties![found];
            source += $", property {found ?? property}";
            if (found is null && parameter.Value is not null)
            {
                notes.Add($"the value has no JSON property {property}");
            }
        }

        if (parameter.IsSensitive && parameter.Value is null)
        {
            notes.Add("the value was not decrypted (turn on IncludeValues to read it)");
        }
        else if (layer.Value is not null && value is not null && !string.Equals(layer.Value, value, StringComparison.Ordinal))
        {
            // Never print either value here: notes are not redacted.
            notes.Add("the AWS value differs from the value seen here, so the deployment may be out of date or the value was transformed");
        }

        return new ProvenanceStep
        {
            Kind = LayerKind.AwsParameterStore,
            Source = source,
            Location = parameter.Location ?? _parameters.Location,
            Value = value,
            IsSensitive = parameter.IsSensitive,
            IsInferred = inferred,
            Note = string.Join("; ", notes),
            Reference = new AwsParameterRef(parameter.Name),
        };
    }

    private static string Describe(AwsParameterType type) => type switch
    {
        AwsParameterType.SecureString => "SecureString parameter",
        AwsParameterType.StringList => "StringList parameter",
        AwsParameterType.String => "parameter",
        _ => "parameter (unknown type)",
    };
}
