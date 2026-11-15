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

/// <summary>
/// A kind of configuration source the scan found in the repository, such as a Helm chart, in the Sources drop-down.
/// Turning one off traces again without it. App settings are the starting point of a trace and stay on.
/// </summary>
public sealed partial class SourceToggle : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private readonly Action<SourceToggle>? _changed;

    /// <summary>Creates the entry.</summary>
    /// <param name="css">The style class that colours its icon.</param>
    /// <param name="icon">The icon resource key.</param>
    /// <param name="title">What it is, for example "Helm chart".</param>
    /// <param name="detail">Where, for example deploy/chart.</param>
    /// <param name="used">True when the current trace uses it.</param>
    /// <param name="isOn">False when the user turned it off.</param>
    /// <param name="changed">Called when it is turned on or off; null when it can't be turned off.</param>
    public SourceToggle(string css, string icon, string title, string detail, bool used, bool isOn = true, Action<SourceToggle>? changed = null)
    {
        Css = css;
        Icon = icon;
        Title = title;
        Detail = detail;
        Used = used;
        IsOn = isOn;

        // Set after IsOn, so building the entry doesn't count as turning it on.
        _changed = changed;
    }

    /// <summary>The style class that colours its icon.</summary>
    public string Css { get; }

    /// <summary>The icon resource key.</summary>
    public string Icon { get; }

    /// <summary>What it is, for example "Helm chart".</summary>
    public string Title { get; }

    /// <summary>Where, for example deploy/chart.</summary>
    public string Detail { get; }

    /// <summary>True when the current trace uses it.</summary>
    public bool Used { get; }

    /// <summary>True when it can be turned off. App settings can't.</summary>
    public bool CanToggle => _changed is not null;

    /// <summary>False when the user turned it off, so the trace leaves it out.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(State))]
    public partial bool IsOn { get; set; }

    /// <summary>"in use", "found" or "off".</summary>
    public string State => !IsOn ? "off" : Used ? "in use" : "found";

    partial void OnIsOnChanged(bool value) => _changed?.Invoke(this);
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

/// <summary>A line in the problems list.</summary>
/// <param name="Severity">"error", "warning" or "info".</param>
/// <param name="Text">The message.</param>
public sealed record ProblemRow(string Severity, string Text);
