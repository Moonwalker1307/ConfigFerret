using ConfigFerret.Desktop.Model;

namespace ConfigFerret.Desktop.ViewModels;

/// <summary>One row of the variables table: an effective setting and where it came from.</summary>
public sealed class SettingRow
{
    /// <summary>Creates a row for <paramref name="setting"/>.</summary>
    public SettingRow(TracedSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        Setting = setting;
        var effective = setting.EffectiveStep;
        var origin = effective is null ? null : DeepestEffective(effective);
        EffectiveSource = effective?.Source ?? "(not set)";
        OriginSource = origin is null || ReferenceEquals(origin, effective) ? string.Empty : origin.Source;
        OriginKind = origin?.Kind ?? effective?.Kind ?? LayerKind.DotNetProvider;
        // Every key has a .NET layer, so the chips show only what lies beyond it.
        var kinds = AllSteps(setting.Steps).Select(s => s.Kind).Distinct().Order().ToList();
        AllKinds = kinds;
        Kinds = (kinds.Count > 1 ? kinds.Where(k => k != LayerKind.DotNetProvider) : kinds).Select(k => new KindBadge(k)).ToList();
        LayerCount = AllSteps(setting.Steps).Count();
        IsOverridden = setting.Steps.Count > 1;
        IsInferred = AllSteps(setting.Steps).Any(s => s.IsInferred && s.IsEffective);
        var colon = setting.Key.IndexOf(':', StringComparison.Ordinal);
        Section = colon > 0 ? setting.Key[..colon] : setting.Key.StartsWith('/') ? "Mounted files" : "(top level)";
    }

    /// <summary>The traced setting.</summary>
    public TracedSetting Setting { get; }

    /// <summary>The configuration key or environment variable name.</summary>
    public string Key => Setting.Key;

    /// <summary>The effective value, redacted when secret.</summary>
    public string Value => Setting.Value is null ? "(no value)" : Setting.Value.ReplaceLineEndings(" ⏎ ");

    /// <summary>True when the value is treated as secret.</summary>
    public bool IsSensitive => Setting.IsSensitive;

    /// <summary>The .NET layer (or workload layer) that won.</summary>
    public string EffectiveSource { get; }

    /// <summary>The furthest-upstream layer that set the winning value, such as a Helm values file or an Octopus variable. Empty when there is none beyond the winning layer.</summary>
    public string OriginSource { get; }

    /// <summary>Where the winning value really comes from: the furthest-upstream layer, or the winning layer itself.</summary>
    public string ComesFrom => OriginSource.Length > 0 ? OriginSource : EffectiveSource;

    /// <summary>The kind of <see cref="OriginSource"/>, or of the winning layer when there is no origin.</summary>
    public LayerKind OriginKind { get; }

    /// <summary>Every kind of layer in the history, for the chips in the table.</summary>
    public IReadOnlyList<KindBadge> Kinds { get; }

    /// <summary>Every kind of layer in the history, .NET included.</summary>
    public IReadOnlyList<LayerKind> AllKinds { get; }

    /// <summary>How many layers the history holds, upstream ones included.</summary>
    public int LayerCount { get; }

    /// <summary>True when more than one .NET layer sets the key.</summary>
    public bool IsOverridden { get; }

    /// <summary>True when a link in the winning chain is a best guess.</summary>
    public bool IsInferred { get; }

    /// <summary>The first key segment, for example Db for Db:Timeout.</summary>
    public string Section { get; }

    /// <summary>The one-sentence history.</summary>
    public string Story => Setting.Story;

    /// <summary>True when the row matches the search text (key, value or any source in the history).</summary>
    public bool Matches(string? filter) =>
        string.IsNullOrWhiteSpace(filter)
        || Setting.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Setting.Key.Replace(":", "__", StringComparison.Ordinal).Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (Setting.Value?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || AllSteps(Setting.Steps).Any(s => s.Source.Contains(filter, StringComparison.OrdinalIgnoreCase));

    internal static IEnumerable<ProvenanceStep> AllSteps(IEnumerable<ProvenanceStep> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var origin in AllSteps(step.Origins))
            {
                yield return origin;
            }
        }
    }

    private static ProvenanceStep DeepestEffective(ProvenanceStep step)
    {
        var next = step.Origins.LastOrDefault(o => o.IsEffective);
        return next is null ? step : DeepestEffective(next);
    }
}

/// <summary>A layer kind shown as a small coloured chip.</summary>
public sealed record KindBadge(LayerKind Kind)
{
    /// <summary>A short label, for example Helm.</summary>
    public string Label => LayerKinds.ShortName(Kind);

    /// <summary>The style class that colours the chip.</summary>
    public string Css => LayerKinds.CssClass(Kind);
}

/// <summary>Names and colours for layer kinds.</summary>
public static class LayerKinds
{
    /// <summary>A short label for a layer kind.</summary>
    public static string ShortName(LayerKind kind) => kind switch
    {
        LayerKind.DotNetProvider => ".NET",
        LayerKind.KubernetesWorkload => "Workload",
        LayerKind.KubernetesConfigMap => "ConfigMap",
        LayerKind.KubernetesSecret => "Secret",
        LayerKind.KubernetesField => "Field",
        LayerKind.Helm => "Helm",
        LayerKind.Kustomize => "Kustomize",
        LayerKind.GitHubActions => "GitHub",
        LayerKind.OctopusDeploy => "Octopus",
        _ => kind.ToString(),
    };

    /// <summary>The style class for a layer kind; the theme gives each family its own colour.</summary>
    public static string CssClass(LayerKind kind) => kind switch
    {
        LayerKind.DotNetProvider => "dotnet",
        LayerKind.KubernetesWorkload or LayerKind.KubernetesConfigMap or LayerKind.KubernetesField => "k8s",
        LayerKind.KubernetesSecret => "secret",
        LayerKind.Helm => "helm",
        LayerKind.Kustomize => "kustomize",
        LayerKind.GitHubActions => "github",
        LayerKind.OctopusDeploy => "octopus",
        _ => "dotnet",
    };
}
