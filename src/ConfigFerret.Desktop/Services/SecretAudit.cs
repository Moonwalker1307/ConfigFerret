using System.Text.RegularExpressions;

namespace ConfigFerret.Desktop.Services;

/// <summary>How well a secret-looking value is kept.</summary>
public enum SecretRating
{
    /// <summary>The value does not look like a secret.</summary>
    NotSecret,

    /// <summary>Green: the winning value comes from a secret store, and no plain layer holds a real value.</summary>
    Protected,

    /// <summary>Orange: in doubt, for example a development value or a guessed link. Check it.</summary>
    Check,

    /// <summary>Red: a secret value sits in plain configuration, such as a committed appsettings file, a ConfigMap, a Helm values file, or a Secret manifest or secretGenerator that holds the value.</summary>
    Exposed,
}

/// <summary>Which tool rendered the Kubernetes manifests a trace read.</summary>
public enum RenderedManifests
{
    /// <summary>None: the manifests are files in the repository.</summary>
    None,

    /// <summary><c>helm template</c> rendered them from a chart.</summary>
    Helm,

    /// <summary><c>kubectl kustomize</c> or <c>kustomize build</c> built them from an overlay.</summary>
    Kustomize,
}

/// <summary>The audit of one setting.</summary>
/// <param name="Key">The configuration key.</param>
/// <param name="Rating">The rating.</param>
/// <param name="Reason">Why, in one sentence.</param>
/// <param name="Where">The layer the rating is about, for example appsettings.json (line 4).</param>
public sealed record SecretFinding(string Key, SecretRating Rating, string Reason, string Where);

/// <summary>
/// Rates each setting that looks like a secret: green when it comes from a secret store, red when a real value sits in
/// plain configuration or in a Secret manifest or secretGenerator, orange when in doubt. Works on a trace run with
/// secret values revealed, so it can look at the values; the findings never hold a value.
/// </summary>
public static partial class SecretAudit
{
    private static readonly Regex SensitiveKey = new(TraceOptions.DefaultSensitiveKeyPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Rates every setting in <paramref name="revealed"/>, a report traced with <see cref="TraceOptions.RevealSensitiveValues"/> on.</summary>
    /// <param name="revealed">The report.</param>
    /// <param name="rendered">Which tool rendered the Kubernetes manifests the report read, if any.</param>
    public static IReadOnlyList<SecretFinding> Audit(ProvenanceReport revealed, RenderedManifests rendered = RenderedManifests.None)
    {
        ArgumentNullException.ThrowIfNull(revealed);
        return revealed.Settings.Select(s => Rate(s, rendered)).ToList();
    }

    /// <summary>Rates one setting.</summary>
    /// <param name="setting">The setting, traced with secret values revealed.</param>
    /// <param name="rendered">Which tool rendered the Kubernetes manifests the trace read, if any.</param>
    public static SecretFinding Rate(TracedSetting setting, RenderedManifests rendered = RenderedManifests.None)
    {
        ArgumentNullException.ThrowIfNull(setting);
        var layers = AllLayers(setting.Steps).ToList();
        var keyLooksSecret = SensitiveKey.IsMatch(setting.Key) || layers.Any(l => l.Kind != LayerKind.DotNetProvider && SensitiveKey.IsMatch(LayerName(l)));
        var winning = setting.EffectiveStep is null ? null : Deepest(setting.EffectiveStep);
        var valueLooksSecret = layers.Any(l => LooksLikeCredential(l.Value));
        var storeBacked = layers.Any(l => l.IsSensitive && l.IsEffective && IsSecretStore(l));

        if (!keyLooksSecret && !valueLooksSecret && !storeBacked)
        {
            return new SecretFinding(setting.Key, SecretRating.NotSecret, "Does not look like a secret.", winning?.Source ?? string.Empty);
        }

        // A real value in a plain layer is exposed, even when a later layer overrides it: it is still committed somewhere.
        var plainWithValue = layers
            .Where(l => !l.IsSensitive && !l.IsRemoval && !IsPlaceholder(l.Value) && IsPlainStorage(l))
            .ToList();
        var exposed = plainWithValue.FirstOrDefault(l => !IsDevelopmentOnly(l) && (LooksLikeCredential(l.Value) || keyLooksSecret));
        if (exposed is not null)
        {
            var reason = exposed.IsEffective || ReferenceEquals(exposed, winning)
                ? $"The value is kept in plain text in {Describe(exposed)}. Move it to a secret store (a Kubernetes Secret, a GitHub secret, a sensitive Octopus variable or a key vault)."
                : $"A later layer overrides it, but {Describe(exposed)} still holds a plain-text value. Remove it from there.";
            return new SecretFinding(setting.Key, SecretRating.Exposed, reason, Where(exposed));
        }

        // A Secret manifest or a secretGenerator keeps the value in plain text or base64, which protects nothing, so it is
        // exposed like a plain layer, even when a later layer overrides it.
        var (layer, committed) = layers.Select(l => (l, CommittedSecret(l))).FirstOrDefault(c => c.Item2 is not null);
        if (committed is not null)
        {
            // A chart may write the value in its template, or generate it as it renders (randAlphaNum, lookup).
            if (rendered == RenderedManifests.Helm && committed.Kind == LayerKind.KubernetesSecret)
            {
                return new SecretFinding(setting.Key, SecretRating.Check, $"Comes from {committed.Source}, which the chart renders without a values file that sets it. Check the chart's template: a value written there is kept in the repository, one it generates as it renders (such as with randAlphaNum or lookup) is not.", SecretKey(committed));
            }

            var (place, form, reader) = Kept(committed, rendered);
            var reason = OnWinningChain(setting, layer!)
                ? $"The value is kept in {form} in {place}, so anyone who can read {reader} can decode it. Fill the Secret from a secret store instead (an ExternalSecret or the Secrets Store CSI driver), or encrypt it (a SealedSecret or SOPS)."
                : $"A later layer overrides it, but {place} still holds the value in {form}. Remove it from there.";

            // The output of a build is not a file to look in; the Secret's name and key are.
            var where = committed.Kind == LayerKind.KubernetesSecret && rendered != RenderedManifests.None ? SecretKey(committed) : Where(committed);
            return new SecretFinding(setting.Key, SecretRating.Exposed, reason, where);
        }

        var development = plainWithValue.FirstOrDefault(IsDevelopmentOnly);
        if (development is not null)
        {
            return new SecretFinding(setting.Key, SecretRating.Check, $"A development value is kept in plain text in {Describe(development)}. Fine for a local placeholder; check it isn't a real credential.", Where(development));
        }

        if (storeBacked && winning is not null)
        {
            var store = layers.Last(l => l.IsSensitive && l.IsEffective && IsSecretStore(l));
            if (layers.Any(l => l.IsInferred && l.IsEffective))
            {
                return new SecretFinding(setting.Key, SecretRating.Check, $"Comes from {Describe(store)}, but the link was matched by name. Check it's the right one.", Where(store));
            }

            return new SecretFinding(setting.Key, SecretRating.Protected, $"Kept in {Describe(store)}.", Where(store));
        }

        if (winning is not null && winning.Kind == LayerKind.DotNetProvider && winning.Reference is EnvironmentVariableRef && winning.Origins.Count == 0)
        {
            return new SecretFinding(setting.Key, SecretRating.Check, "Set by an environment variable whose origin isn't in the repository. Check that the deployment fills it from a secret store.", Where(winning));
        }

        if (layers.All(l => IsPlaceholder(l.Value)))
        {
            return new SecretFinding(setting.Key, SecretRating.Check, "Looks like a secret but only has an empty or placeholder value here. Check that the deployment fills it from a secret store.", winning is null ? string.Empty : Where(winning));
        }

        return new SecretFinding(setting.Key, SecretRating.Check, "Looks like a secret, but where it comes from is unclear. Check it.", winning is null ? string.Empty : Where(winning));
    }

    /// <summary>True when <paramref name="value"/> looks like a credential on its own: a known token format, a connection string with a password, or a long random string.</summary>
    public static bool LooksLikeCredential(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || IsPlaceholder(value))
        {
            return false;
        }

        value = value.Trim();
        if (KnownTokenFormat().IsMatch(value) || CredentialInConnectionString().IsMatch(value))
        {
            return true;
        }

        if (value.Length < 24 || value.Length > 512 || value.Contains(' ', StringComparison.Ordinal) || value.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        var classes = (value.Any(char.IsAsciiLetterUpper) ? 1 : 0) + (value.Any(char.IsAsciiLetterLower) ? 1 : 0) + (value.Any(char.IsAsciiDigit) ? 1 : 0);
        return classes == 3 && Entropy(value) >= 4.0;
    }

    /// <summary>True for an empty value, *** or a substitution such as #{Var}, ${VAR}, ${{ secrets.X }}, {{ .Values.x }} or __TOKEN__.</summary>
    public static bool IsPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        value = value.Trim();
        return value is "***" or "<secret>" or "changeme-in-vault"
            || Placeholder().IsMatch(value);
    }

    private static bool IsSecretStore(ProvenanceStep step) => step.Kind switch
    {
        LayerKind.KubernetesSecret => true,
        LayerKind.GitHubActions => step.IsSensitive,
        LayerKind.OctopusDeploy => step.IsSensitive,
        LayerKind.DotNetProvider => step.IsSensitive, // user secrets, key-per-file mounts
        _ => false,
    };

    // Places a value is written down in plain text: files in the repository and plain variables in deployment tools.
    private static bool IsPlainStorage(ProvenanceStep step) => step.Kind switch
    {
        LayerKind.DotNetProvider => step.Reference is FileRef || step.Source.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || step.Source.Contains(".json ", StringComparison.OrdinalIgnoreCase),
        LayerKind.KubernetesConfigMap or LayerKind.KubernetesWorkload or LayerKind.Helm or LayerKind.Kustomize => true,
        LayerKind.GitHubActions or LayerKind.OctopusDeploy => !step.IsSensitive,
        _ => false,
    };

    // The layer that holds a Secret's value in plain text or base64: a Secret manifest with the value in data or
    // stringData, a secretGenerator literal or env file, or the secretGenerator a built overlay made the Secret from (a
    // files entry carries no value of its own, but the built Secret does). Null when the value is not written down: a
    // Secret that was not loaded or that an ExternalSecret or SecretProviderClass creates (it has no value), a
    // placeholder, a value SOPS encrypted, or a Secret that another layer fills, such as a secret store or a Helm template
    // (whose values are plain layers).
    private static ProvenanceStep? CommittedSecret(ProvenanceStep step)
    {
        if (!step.IsSensitive || step.IsRemoval || IsPlaceholder(step.Value) || IsSopsEncrypted(step.Value))
        {
            return null;
        }

        var origins = step.Origins.Where(o => o.IsEffective).ToList();
        return step.Kind switch
        {
            LayerKind.Kustomize => step,
            LayerKind.KubernetesSecret when origins.Count == 0 => step,
            LayerKind.KubernetesSecret => origins.LastOrDefault(o => o.Kind == LayerKind.Kustomize),
            _ => null,
        };
    }

    // A value SOPS encrypted, such as ENC[AES256_GCM,data:...,iv:...,tag:...,type:str].
    private static bool IsSopsEncrypted(string? value) =>
        value is not null && value.StartsWith("ENC[", StringComparison.Ordinal) && value.EndsWith(']') && value.Contains(",data:", StringComparison.Ordinal);

    // Where a committed Secret's value is written, how, and who can read it, for "kept in {form} in {place}, so anyone who
    // can read {reader} can decode it". A secretGenerator's env file or file is named from the step's note, which holds
    // only the path ("create (env file secrets.env:3)", "create (file password.txt)"); a literal's note holds no text of it.
    private static (string Place, string Form, string Reader) Kept(ProvenanceStep step, RenderedManifests rendered)
    {
        if (step.Kind == LayerKind.Kustomize)
        {
            var generator = $"the secretGenerator of {step.Source}";
            var input = GeneratorInput().Match(step.Note ?? string.Empty);
            var place = !input.Success ? generator
                : input.Groups["line"].Success ? $"line {input.Groups["line"].Value} of the env file {input.Groups["path"].Value} named by {generator}"
                : $"the file {input.Groups["path"].Value} named by {generator}";
            return (place, "plain text", "that file");
        }

        return rendered == RenderedManifests.Kustomize
            ? ($"{step.Source} from the overlay's resources", "plain text or base64", "the overlay's files")
            : ($"the manifest of {step.Source}", "plain text or base64", "that file");
    }

    // A Secret by its name and key, for a Secret whose manifest is the output of a render rather than a file to look in.
    private static string SecretKey(ProvenanceStep step) => step.Reference is SecretKeyRef r ? $"{step.Source} (key {r.Key})" : step.Source;

    // True when the winning value passes through step: the setting's effective step, or an effective origin below it.
    // Each resolver adds its own effective origin, such as the manifests and a GitHub variable of the same name, so every
    // one of them counts, not only the last.
    private static bool OnWinningChain(TracedSetting setting, ProvenanceStep step)
    {
        return setting.EffectiveStep is { } effective && Holds(effective);

        bool Holds(ProvenanceStep on) => ReferenceEquals(on, step) || on.Origins.Any(o => o.IsEffective && Holds(o));
    }

    private static bool IsDevelopmentOnly(ProvenanceStep step) =>
        step.Source.Contains(".Development.", StringComparison.OrdinalIgnoreCase)
        || step.Source.Contains(".Local.", StringComparison.OrdinalIgnoreCase)
        || (step.Value is { } value && (value.Contains("localhost", StringComparison.OrdinalIgnoreCase) || value.Contains("127.0.0.1", StringComparison.Ordinal)) && !CredentialInConnectionString().IsMatch(value));

    private static string Describe(ProvenanceStep step) => step.Kind switch
    {
        LayerKind.KubernetesSecret => step.Source,
        LayerKind.GitHubActions => step.Source,
        LayerKind.OctopusDeploy => (step.IsSensitive ? "sensitive " : string.Empty) + step.Source,
        LayerKind.DotNetProvider when step.IsSensitive => step.Source + " (not committed)",
        _ => step.Source,
    };

    private static string Where(ProvenanceStep step) => step.Location is null ? step.Source : $"{step.Source} ({step.Location})";

    // The name a layer gives the value: a ConfigMap or Secret key, an env entry, a Helm values path or a variable name.
    private static string LayerName(ProvenanceStep step) => step.Reference switch
    {
        ConfigMapKeyRef r => r.Key,
        SecretKeyRef r => r.Key,
        WorkloadEnvRef r => r.Name,
        EnvironmentVariableRef r => r.Name,
        HelmValueRef r => r.Path,
        GitHub.GitHubVariableRef r => r.Name,
        Octopus.OctopusVariableRef r => r.Name,
        _ => string.Empty,
    };

    private static ProvenanceStep Deepest(ProvenanceStep step)
    {
        var next = step.Origins.LastOrDefault(o => o.IsEffective);
        return next is null ? step : Deepest(next);
    }

    private static IEnumerable<ProvenanceStep> AllLayers(IEnumerable<ProvenanceStep> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var origin in AllLayers(step.Origins))
            {
                yield return origin;
            }
        }
    }

    private static double Entropy(string value)
    {
        var counts = new Dictionary<char, int>();
        foreach (var c in value)
        {
            counts[c] = counts.GetValueOrDefault(c) + 1;
        }

        return -counts.Values.Sum(n => (double)n / value.Length * Math.Log2((double)n / value.Length));
    }

    // GitHub, Slack, AWS, OpenAI-style, Stripe, Octopus API keys, JWTs, PEM keys, Azure storage keys.
    [GeneratedRegex(
        @"^(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|sk-[A-Za-z0-9_-]{20,}|sk_live_[A-Za-z0-9]{16,}|API-[A-Z0-9]{20,}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{5,})$|-----BEGIN [A-Z ]*PRIVATE KEY-----",
        RegexOptions.CultureInvariant)]
    private static partial Regex KnownTokenFormat();

    // The note of a secretGenerator step read from an env file or a file, such as "create (env file secrets.env:3)".
    [GeneratedRegex(@"^[a-z]+ \((?:env file (?<path>.+):(?<line>[0-9]+)|file (?<path>.+))\)$", RegexOptions.CultureInvariant)]
    private static partial Regex GeneratorInput();

    [GeneratedRegex(@"(?:^|;)\s*(password|pwd|accountkey|sharedaccesskey|sharedaccesssignature|sig)\s*=\s*[^;#{$\s][^;]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialInConnectionString();

    [GeneratedRegex(@"^(#\{[^}]+\}|\$\{\{?[^}]+\}?\}|\{\{[^}]+\}\}|__[A-Za-z0-9_]+__|%[A-Za-z0-9_]+%|<[^>]+>|\$\([^)]+\)|\*+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
