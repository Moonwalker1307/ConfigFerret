using ConfigFerret.Desktop.Services;

namespace ConfigFerret.Desktop.ViewModels;

/// <summary>A repository in the list.</summary>
public sealed class RepositoryRow(RepositoryInfo info)
{
    /// <summary>The repository.</summary>
    public RepositoryInfo Info { get; } = info ?? throw new ArgumentNullException(nameof(info));

    /// <summary>The repository name.</summary>
    public string Name => Info.Name;

    /// <summary>The owner.</summary>
    public string Owner => Info.Owner;

    /// <summary>owner/name.</summary>
    public string FullName => Info.FullName;

    /// <summary>The description, or empty.</summary>
    public string Description => Info.Description;

    /// <summary>True when there is a description.</summary>
    public bool HasDescription => Info.Description.Length > 0;

    /// <summary>True for a private repository.</summary>
    public bool IsPrivate => Info.IsPrivate;

    /// <summary>The main language, or empty.</summary>
    public string Language => Info.Language;

    /// <summary>True when the language is known.</summary>
    public bool HasLanguage => Info.Language.Length > 0;

    /// <summary>"pushed 3 days ago" and the like.</summary>
    public string Pushed => Info.PushedAt is { } when ? "updated " + Ago(DateTimeOffset.UtcNow - when) : "empty";

    /// <summary>The first letter of the name, for the avatar.</summary>
    public string Initial => Info.Name.Length == 0 ? "?" : char.ToUpperInvariant(Info.Name[0]).ToString();

    /// <summary>True when the row matches the search text.</summary>
    public bool Matches(string? filter) =>
        string.IsNullOrWhiteSpace(filter)
        || Info.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Info.Description.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static string Ago(TimeSpan span) => span.TotalMinutes < 60 ? "just now"
        : span.TotalHours < 24 ? $"{(int)span.TotalHours}h ago"
        : span.TotalDays < 60 ? $"{(int)span.TotalDays}d ago"
        : span.TotalDays < 730 ? $"{(int)(span.TotalDays / 30)}mo ago"
        : $"{(int)(span.TotalDays / 365)}y ago";
}

/// <summary>Something the scan found in the repository, shown as a card on the overview.</summary>
/// <param name="Css">The style class that colours the card's icon.</param>
/// <param name="Icon">The icon resource key.</param>
/// <param name="Title">What it is, for example "Helm chart".</param>
/// <param name="Detail">Where, for example deploy/chart.</param>
/// <param name="Used">True when the current trace uses it.</param>
public sealed record DiscoveryCard(string Css, string Icon, string Title, string Detail, bool Used)
{
    /// <summary>"in use" or "found".</summary>
    public string State => Used ? "in use" : "found";
}

/// <summary>A row in the secrets audit.</summary>
public sealed class SecretRow(SecretFinding finding)
{
    /// <summary>The finding.</summary>
    public SecretFinding Finding { get; } = finding ?? throw new ArgumentNullException(nameof(finding));

    /// <summary>The configuration key.</summary>
    public string Key => Finding.Key;

    /// <summary>Why it got its rating.</summary>
    public string Reason => Finding.Reason;

    /// <summary>The layer the rating is about.</summary>
    public string Where => Finding.Where;

    /// <summary>The style class: good, check or bad.</summary>
    public string Css => Finding.Rating switch
    {
        SecretRating.Protected => "good",
        SecretRating.Check => "check",
        SecretRating.Exposed => "bad",
        _ => "none",
    };

    /// <summary>A short label for the rating.</summary>
    public string Label => Finding.Rating switch
    {
        SecretRating.Protected => "Secret store",
        SecretRating.Check => "Check",
        SecretRating.Exposed => "Exposed",
        _ => "Not secret",
    };

    /// <summary>Sorts red first, then orange, then green.</summary>
    public int Order => Finding.Rating switch
    {
        SecretRating.Exposed => 0,
        SecretRating.Check => 1,
        SecretRating.Protected => 2,
        _ => 3,
    };
}

/// <summary>A dead variable, or a value that never wins, for the Unused tab.</summary>
/// <param name="Css">The style class for the chip.</param>
/// <param name="Kind">What it is: "Setting", "Helm value", "Overridden" or the tool name.</param>
/// <param name="Name">The key or variable name.</param>
/// <param name="Where">The layer or variable scope.</param>
/// <param name="Detail">What wins instead, or why it counts as unused.</param>
/// <param name="IsDead">True for a variable nothing reads: dead, rather than overridden.</param>
public sealed record UnusedRow(string Css, string Kind, string Name, string Where, string Detail, bool IsDead = false)
{
    /// <summary>True when Show can open it in the Configuration tab: a setting, not a Helm value or a deployment variable.</summary>
    public bool CanShow { get; init; } = true;
}

/// <summary>A line in the problems list.</summary>
/// <param name="Severity">"error", "warning" or "info".</param>
/// <param name="Text">The message.</param>
public sealed record ProblemRow(string Severity, string Text);

/// <summary>A choice in the layer filter: any layer, or one kind.</summary>
/// <param name="Kind">The kind, or null for any.</param>
public sealed record LayerFilter(LayerKind? Kind)
{
    /// <inheritdoc />
    public override string ToString() => Kind is { } kind ? "Through " + LayerKinds.ShortName(kind) : "Any layer";
}
