using ConfigFerret.Desktop.Model;
using ConfigFerret.Desktop.Services;

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

    /// <summary>
    /// A row for something that is not an app setting: a GitHub or Octopus variable no setting picks up, or a Helm value
    /// no template reads. It has one layer, so the details pane shows where it is defined.
    /// </summary>
    /// <param name="kind">The tool it lives in.</param>
    /// <param name="what">What it is, for example "GitHub Actions secret", shown as the details caption.</param>
    /// <param name="name">The variable name or values path.</param>
    /// <param name="value">The value, or null when unknown or secret.</param>
    /// <param name="isSecret">True for a secret or sensitive variable.</param>
    /// <param name="source">Where it is defined, for example "GitHub Actions repository secret (contoso/web)".</param>
    /// <param name="location">Where in the source, when known.</param>
    /// <param name="deadReason">Why nothing uses it, when nothing does.</param>
    /// <param name="workflowUse">Where a workflow uses it, when only a workflow does.</param>
    public static SettingRow ForVariable(
        LayerKind kind,
        string what,
        string name,
        string? value,
        bool isSecret,
        string source,
        string? location = null,
        string? deadReason = null,
        string? workflowUse = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        var shown = isSecret && value is null ? "***" : value;
        var step = new ProvenanceStep
        {
            Kind = kind,
            Source = source,
            Location = location,
            Value = shown,
            IsSensitive = isSecret,
            IsEffective = true,
        };
        return new SettingRow(new TracedSetting(name, shown, [step], isSecret)) { IsSetting = false, What = what, DeadReason = deadReason, WorkflowUse = workflowUse };
    }

    /// <summary>The traced setting, or a stand-in with one layer for a row that is not an app setting.</summary>
    public TracedSetting Setting { get; }

    /// <summary>False for a GitHub or Octopus variable or a Helm value that no app setting picks up.</summary>
    public bool IsSetting { get; private init; } = true;

    /// <summary>What the row is, for example "Setting" or "GitHub Actions secret".</summary>
    public string What { get; private init; } = "Setting";

    /// <summary>The caption above the key in the details pane, for example SETTING.</summary>
    public string Caption => What.ToUpperInvariant();

    /// <summary>Where a workflow uses this variable, for example "deploy.yml line 12", when only a workflow uses it.</summary>
    public string? WorkflowUse { get; init; }

    /// <summary>How many rows share this row's name, counting <c>:</c> and <c>__</c> as the same and ignoring case. 1 when the name is unique.</summary>
    public int NameCount { get; internal set; } = 1;

    /// <summary>True when another row has the same name, such as a GitHub secret set both for the repository and an environment.</summary>
    public bool IsDuplicate => NameCount > 1;

    /// <summary>A tooltip for the duplicate chip.</summary>
    public string DuplicateText => $"{NameCount} rows have this name";

    /// <summary>The name used to find duplicates: <c>:</c> written as <c>__</c>, upper case.</summary>
    public string NormalizedName => Key.Replace(":", "__", StringComparison.Ordinal).ToUpperInvariant();

    /// <summary>True for a GitHub variable a workflow uses directly while no app setting does.</summary>
    public bool IsWorkflowOnly => WorkflowUse is not null;

    /// <summary>A line for the details pane about the workflow that uses it.</summary>
    public string? WorkflowText => WorkflowUse is null ? null : $"Used by {WorkflowUse}, not by an app setting.";

    /// <summary>The secrets audit rating, or null when it does not look like a secret.</summary>
    public SecretRating? Rating { get; init; }

    /// <summary>The label on the dead badge in the details pane.</summary>
    public string DeadLabel => IsSetting ? "Dead: not read by code" : "Dead: nothing uses it";

    /// <summary>Why no code reads this setting, or null when code reads it or no code was scanned.</summary>
    public string? DeadReason { get; init; }

    /// <summary>True when the setting is configured but no code reads it.</summary>
    public bool IsDead => DeadReason is not null;

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

    /// <summary>
    /// True when the row matches the search text: the key, the value, or any source or location in the history. A GitHub
    /// or Octopus variable is found by its name, whether it is a row of its own or a layer of a setting.
    /// </summary>
    public bool Matches(string? filter) =>
        string.IsNullOrWhiteSpace(filter)
        || Setting.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Setting.Key.Replace(":", "__", StringComparison.Ordinal).Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (Setting.Value?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || AllSteps(Setting.Steps).Any(s => s.Source.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (s.Location?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));

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
