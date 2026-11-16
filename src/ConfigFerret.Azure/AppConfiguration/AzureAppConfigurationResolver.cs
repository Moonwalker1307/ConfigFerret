using System.Text.Json;
using ConfigFerret.Sources;

namespace ConfigFerret.Azure;

/// <summary>
/// Links environment variables, Kubernetes env entries, ConfigMap/Secret keys and App Configuration provider values to
/// Azure App Configuration key-values.
/// </summary>
internal sealed class AzureAppConfigurationResolver(AzureAppConfigurationSettings settings, AzureAppConfigurationTraceOptions options) : IOriginResolver
{
    private const string FeatureManagement = "FeatureManagement:";

    private bool _warned;

    public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!_warned)
        {
            _warned = true;
            foreach (var warning in settings.Warnings)
            {
                diagnostics.Warn(warning);
            }
        }

        return layer.Reference switch
        {
            // External secret references point to secret stores; App Configuration is not one.
            ExternalSecretRef => [],
            null when layer.Kind == LayerKind.DotNetProvider => FromConfigurationProvider(layer),
            _ => FromName(layer, diagnostics),
        };
    }

    // The App Configuration provider in the app: its ToString() is the type name, AzureAppConfigurationProvider.
    private List<ProvenanceStep> FromConfigurationProvider(ProvenanceStep layer)
    {
        if (!options.MatchConfigurationProvider || layer.ConfigurationKey is not { } key
            || !layer.Source.Contains("AzureAppConfiguration", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var found = settings.FindByConfigurationKey(key);
        if (found.Count == 0)
        {
            found = FlagAbove(key);
        }

        return found.Select(s => ToStep(s, layer, inferred: true, "read by the App Configuration provider (matched by key)")).ToList();
    }

    private List<ProvenanceStep> FromName(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        if (LayerNames.Of(layer) is not { } name)
        {
            return [];
        }

        if (options.Links.TryGetValue(name, out var link) && !string.IsNullOrWhiteSpace(link))
        {
            var linked = settings.Find(link.Trim());
            if (linked.Count == 0)
            {
                diagnostics.Warn($"{name} is linked to Azure App Configuration key {link.Trim()}, but no such key-value was loaded from {settings.Store}.");
            }

            return linked.Select(s => ToStep(s, layer, inferred: false, null)).ToList();
        }

        if (!options.MatchByName)
        {
            return [];
        }

        var exact = settings.Find(name);
        if (exact.Count > 0)
        {
            return exact.Select(s => ToStep(s, layer, inferred: true, "matched by name")).ToList();
        }

        // A JSON key-value is found by the keys it sets and by its own key, so a variable may hold the whole JSON.
        return settings.FindByConfigurationKeyOrOwnKey(LayerNames.ToConfigurationKey(name))
            .Select(s => ToStep(s, layer, inferred: true, "matched by configuration key"))
            .ToList();
    }

    // A feature flag that is on and has client filters sets FeatureManagement:<name>:EnabledFor:<i>:Name,
    // :EnabledFor:<i>:Parameters:... and :RequirementType, without FeatureManagement: when a prefix trims it. Only those
    // keys lead to such a flag: the shortest key above key that one sets and that is followed by one of them. Only the
    // lengths of such flags' keys are tried, shortest first and from 1, so a flag key that starts with ':' is found, and
    // the key is never copied, so the walk takes time in line with the key's length and the number of flag key lengths.
    // A key that ends in ':' names no flag.
    private List<AzureAppConfigurationSetting> FlagAbove(string key)
    {
        foreach (var length in settings.FlagKeyLengths)
        {
            if (length >= key.Length)
            {
                break;
            }

            if (length == 0 || key[length] != ':' || key[length - 1] == ':' || !IsFlagSubkey(key.AsSpan(length + 1)))
            {
                continue;
            }

            var flags = settings.FindFlagsWithFilters(key.AsSpan(0, length));
            if (flags.Count > 0)
            {
                return flags;
            }
        }

        return [];
    }

    // rest is what follows the flag's key and its ':': EnabledFor:<digits>:Name, EnabledFor:<digits>:Parameters:..., or
    // RequirementType, case ignored.
    private static bool IsFlagSubkey(ReadOnlySpan<char> rest)
    {
        if (rest.Equals("RequirementType", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const string EnabledFor = "EnabledFor:";
        if (!rest.StartsWith(EnabledFor, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var index = rest[EnabledFor.Length..];
        var digits = index.IndexOfAnyExceptInRange('0', '9');
        if (digits <= 0 || index[digits] != ':')
        {
            return false;
        }

        var name = index[(digits + 1)..];
        return name.Equals("Name", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Parameters:", StringComparison.OrdinalIgnoreCase);
    }

    private ProvenanceStep ToStep(AzureAppConfigurationSetting setting, ProvenanceStep layer, bool inferred, string? how)
    {
        var notes = new List<string>();
        if (how is not null)
        {
            notes.Add(how);
        }

        var secret = setting.Kind == AzureAppConfigurationSettingKind.KeyVaultReference;
        string source;
        switch (setting.Kind)
        {
            case AzureAppConfigurationSettingKind.KeyVaultReference:
                source = $"Azure App Configuration Key Vault reference {setting.Key}";
                notes.Add(setting.SecretId is null
                    ? "Key Vault reference with no valid secret URI"
                    : $"Key Vault reference to {setting.SecretId}; the secret is not read here");
                break;
            case AzureAppConfigurationSettingKind.FeatureFlag:
                // The name comes from the stored key, which no trimmed prefix changes: what follows the prefix, or the whole
                // key when nothing does. A snapshot built by hand may give a flag any key and configuration key.
                var prefix = AzureAppConfigurationSettings.FeatureFlagPrefix;
                var name = setting.Key.StartsWith(prefix, StringComparison.Ordinal)
                    ? setting.Key.Length > prefix.Length ? setting.Key[prefix.Length..] : setting.Key
                    : setting.ConfigurationKey.StartsWith(FeatureManagement, StringComparison.OrdinalIgnoreCase)
                        ? setting.ConfigurationKey[FeatureManagement.Length..]
                        : setting.ConfigurationKey;
                source = $"Azure App Configuration feature flag {name}";
                notes.Add(DescribeFlag(setting.Enabled, setting.Filters));
                break;
            default:
                source = $"Azure App Configuration key {setting.Key}";
                break;
        }

        if (!secret && setting.Kind == AzureAppConfigurationSettingKind.KeyValue && layer.Value is not null && setting.Value is not null
            && !SameValue(setting, layer.Value))
        {
            // Never print either value here: notes are not redacted.
            notes.Add("the store's value differs from the value seen here, so the app may need a refresh or the value was transformed");
        }

        return new ProvenanceStep
        {
            Kind = LayerKind.AzureAppConfiguration,
            Source = $"{source} ({setting.Store})",
            Location = setting.LabelText,
            Value = secret ? null : setting.Value,
            IsSensitive = secret,
            IsInferred = inferred,
            Note = notes.Count == 0 ? null : string.Join("; ", notes),
            Reference = AzureAppConfigurationSettings.Reference(setting),
        };
    }

    // The same text, or, for a stored key-value with a JSON content type, the same JSON: other spacing or member order, or
    // a number written another way, is not a difference. A text that does not parse within the depth the provider reads
    // is compared as text. A value flattened from JSON is compared as text, as the provider writes it as text.
    private bool SameValue(AzureAppConfigurationSetting setting, string seen)
    {
        var stored = setting.Value!;
        if (string.Equals(seen, stored, StringComparison.Ordinal))
        {
            return true;
        }

        if (!AzureAppConfigurationSettings.IsJson(setting.ContentType) || !settings.Find(setting.Key).Any(s => ReferenceEquals(s, setting)))
        {
            return false;
        }

        try
        {
            using var storedJson = JsonDocument.Parse(stored, AzureAppConfigurationSettings.JsonOptions);
            using var seenJson = JsonDocument.Parse(seen, AzureAppConfigurationSettings.JsonOptions);
            return JsonElement.DeepEquals(storedJson.RootElement, seenJson.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string DescribeFlag(bool? enabled, IReadOnlyCollection<string> filters)
    {
        if (enabled is null)
        {
            return "could not be read";
        }

        if (!enabled.Value)
        {
            return "off";
        }

        return filters.Count > 0 ? $"on for filters {string.Join(", ", filters)}" : "on";
    }
}
