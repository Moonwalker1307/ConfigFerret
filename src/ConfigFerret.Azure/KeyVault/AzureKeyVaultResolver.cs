using ConfigFerret.Sources;

namespace ConfigFerret.Azure;

/// <summary>
/// Links environment variables, Kubernetes env entries, ConfigMap/Secret keys, External Secrets references, App
/// Configuration Key Vault references and Key Vault configuration provider values to Azure Key Vault secrets.
/// </summary>
internal sealed class AzureKeyVaultResolver(AzureKeyVaultSecrets secrets, AzureKeyVaultTraceOptions options) : IOriginResolver
{
    private bool _warned;

    public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!_warned)
        {
            _warned = true;
            foreach (var warning in secrets.Warnings)
            {
                diagnostics.Warn(warning);
            }
        }

        var steps = layer.Reference switch
        {
            ExternalSecretRef external => FromExternalSecret(external, layer),
            AzureAppConfigurationSettingRef { SecretId: { } secretId } => FromSecretId(secretId, layer),
            AzureAppConfigurationSettingRef => [],
            null when layer.Kind == LayerKind.DotNetProvider => FromConfigurationProvider(layer),
            _ => FromName(layer, diagnostics),
        };
        foreach (var (secret, _) in steps)
        {
            if (secret is not null)
            {
                WarnIfUnusable(secret, layer, diagnostics);
            }
        }

        return steps.Select(s => s.Step).ToList();
    }

    // External Secrets Operator: remoteRef.key is the secret name, optionally as "secret/<name>". "key/" and "cert/" read
    // keys and certificates, which are not secrets.
    private List<(AzureKeyVaultSecret? Secret, ProvenanceStep Step)> FromExternalSecret(ExternalSecretRef external, ProvenanceStep layer)
    {
        if (external.Provider is not null && !string.Equals(external.Provider, "azurekv", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var name = external.RemoteKey.Trim();
        if (name.StartsWith("secret/", StringComparison.OrdinalIgnoreCase))
        {
            name = name["secret/".Length..];
        }

        if (secrets.Find(name) is not { } secret)
        {
            return [];
        }

        var notes = new List<string> { external.Store is null ? "named by an external secret reference" : $"named by an external secret reference through store {external.Store}" };
        if (external.Property is not null)
        {
            notes.Add($"reads property {external.Property} of the secret's JSON value");
        }

        if (external.Version is not null && secret.Version is not null && !string.Equals(external.Version, secret.Version, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add($"pinned to version {external.Version}, but the current version is {secret.Version}");
        }

        return [(secret, ToStep(secret, external.Property is null ? layer : null, inferred: false, notes))];
    }

    // An App Configuration Key Vault reference: https://<vault>.vault.azure.net/secrets/<name>[/<version>].
    private List<(AzureKeyVaultSecret? Secret, ProvenanceStep Step)> FromSecretId(Uri secretId, ProvenanceStep layer)
    {
        if (!secretId.IsAbsoluteUri || !string.Equals(AzureAuth.ShortName(secretId), secrets.Vault, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var segments = secretId.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is < 2 or > 3 || !string.Equals(segments[0], "secrets", StringComparison.OrdinalIgnoreCase)
            || secrets.Find(Uri.UnescapeDataString(segments[1])) is not { } secret)
        {
            return [];
        }

        var notes = new List<string> { "named by an App Configuration Key Vault reference" };
        if (segments.Length == 3 && secret.Version is not null && !string.Equals(segments[2], secret.Version, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add($"the reference pins version {segments[2]}, but the current version is {secret.Version}");
        }

        return [(secret, ToStep(secret, layer, inferred: false, notes))];
    }

    // The Key Vault configuration provider in the app: its ToString() is the type name, AzureKeyVaultConfigurationProvider.
    // Every value it sets is a secret. The core already hides the provider types it knows; a layer it does not hide gets a
    // step without a value whenever no secret is linked, so hiding never depends on MatchConfigurationProvider.
    private List<(AzureKeyVaultSecret? Secret, ProvenanceStep Step)> FromConfigurationProvider(ProvenanceStep layer)
    {
        if (layer.ConfigurationKey is not { } key || !layer.Source.Contains("AzureKeyVault", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (!options.MatchConfigurationProvider)
        {
            return layer.IsSensitive ? [] : [Hidden("Azure Key Vault secret, not linked", null, "read by the Key Vault configuration provider; MatchConfigurationProvider is off, so no secret is linked", inferred: true)];
        }

        var found = secrets.FindByConfigurationKey(key);
        if (found.Count == 0)
        {
            // The secret is missing from the snapshot, for example because SecretNames left it out, it is in another vault,
            // or a KeyVaultSecretManager renamed it. With several vaults traced, a step here would hide the one that has it.
            return layer.IsSensitive ? [] : [Hidden($"Azure Key Vault secret not in the snapshot of {secrets.Vault}", null, "read by the Key Vault configuration provider, but no loaded secret maps to this key", inferred: true)];
        }

        return [.. found.Select(s => (s, ToStep(s, layer, inferred: true, ["read by the Key Vault configuration provider (matched by key)"])))];
    }

    private List<(AzureKeyVaultSecret? Secret, ProvenanceStep Step)> FromName(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        if (LayerNames.Of(layer) is not { } name)
        {
            return [];
        }

        if (options.Links.TryGetValue(name, out var link) && !string.IsNullOrWhiteSpace(link))
        {
            if (secrets.Find(link.Trim()) is { } linked)
            {
                return [(linked, ToStep(linked, layer, inferred: false, []))];
            }

            diagnostics.Warn($"{name} is linked to Azure Key Vault secret {link.Trim()}, but no such secret was loaded from {secrets.Vault}.");
            return [Hidden($"Azure Key Vault secret {link.Trim()} ({secrets.Vault})", new AzureKeyVaultSecretRef(secrets.Vault, link.Trim()), "linked explicitly, but not in the snapshot of the vault", inferred: false)];
        }

        if (!options.MatchByName)
        {
            return [];
        }

        if (secrets.Find(name) is { } exact)
        {
            return [(exact, ToStep(exact, layer, inferred: true, ["matched by name"]))];
        }

        return [.. secrets.FindByConfigurationKey(LayerNames.ToConfigurationKey(name))
            .Select(s => (s, ToStep(s, layer, inferred: true, ["matched by configuration key"])))];
    }

    private void WarnIfUnusable(AzureKeyVaultSecret secret, ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        var who = layer.ConfigurationKey ?? LayerNames.Of(layer) ?? layer.Source;
        var now = options.Clock.GetUtcNow();
        if (!secret.Enabled)
        {
            diagnostics.Warn($"Azure Key Vault secret {secret.Name} ({secret.Vault}), linked to {who}, is disabled, so it cannot be read.");
        }
        else if (secret.ExpiresOn is { } expires && expires <= now)
        {
            diagnostics.Warn($"Azure Key Vault secret {secret.Name} ({secret.Vault}), linked to {who}, expired on {expires:yyyy-MM-dd}, so it cannot be read.");
        }
        else if (secret.NotBefore is { } notBefore && notBefore > now)
        {
            diagnostics.Warn($"Azure Key Vault secret {secret.Name} ({secret.Vault}), linked to {who}, is not valid until {notBefore:yyyy-MM-dd}, so it cannot be read yet.");
        }
    }

    // Stands for a secret that is not in the snapshot or not linked, so the layer it set is hidden. It has no value to show.
    private static (AzureKeyVaultSecret? Secret, ProvenanceStep Step) Hidden(string source, AzureKeyVaultSecretRef? reference, string note, bool inferred) =>
        (null, new ProvenanceStep
        {
            Kind = LayerKind.AzureKeyVault,
            Source = source,
            IsSensitive = true,
            IsInferred = inferred,
            Note = note,
            Reference = reference,
        });

    // compareWith is the layer whose value should equal the secret's, or null when they are not comparable.
    private static ProvenanceStep ToStep(AzureKeyVaultSecret secret, ProvenanceStep? compareWith, bool inferred, List<string> notes)
    {
        if (!secret.Enabled)
        {
            notes.Add("disabled");
        }

        if (secret.Value is null)
        {
            notes.Add("value not read");
        }
        else if (compareWith?.Value is { } seen && !string.Equals(seen, secret.Value, StringComparison.Ordinal))
        {
            // Never print either value here: notes are not redacted.
            notes.Add("the vault's current value differs from the value seen here, so the app may need a restart or a reload");
        }

        return new ProvenanceStep
        {
            Kind = LayerKind.AzureKeyVault,
            Source = $"Azure Key Vault secret {secret.Name} ({secret.Vault})",
            Location = secret.Version is null ? null : $"version {secret.Version}",
            Value = secret.Value,
            IsSensitive = true,
            IsInferred = inferred,
            Note = string.Join("; ", notes),
            Reference = new AzureKeyVaultSecretRef(secret.Vault, secret.Name),
        };
    }
}
