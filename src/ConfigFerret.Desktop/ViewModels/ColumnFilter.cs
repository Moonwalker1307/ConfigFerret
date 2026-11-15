using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ConfigFerret.Desktop.ViewModels;

/// <summary>The table columns that can be sorted and filtered.</summary>
public enum TableColumn
{
    /// <summary>The key or variable name.</summary>
    Key,

    /// <summary>The effective value.</summary>
    Value,

    /// <summary>Where the value comes from.</summary>
    ComesFrom,

    /// <summary>The kinds of layer in the history.</summary>
    Layers,
}

/// <summary>
/// The filter behind a column header, like a spreadsheet's: sort the table by this column, tick the values to show, and
/// search. The tick list holds the column's distinct values with how many rows have each; the search narrows both the
/// list and the rows.
/// </summary>
public sealed partial class ColumnFilter : ObservableObject
{
    private readonly Func<SettingRow, IEnumerable<string>> _choices;
    private readonly Func<SettingRow, IEnumerable<string>> _searchable;
    private readonly Action<ColumnFilter> _changed;
    private readonly Action<TableColumn, bool?> _sort;
    private List<FilterChoice> _all = [];
    private bool _quiet;

    /// <summary>Creates the filter.</summary>
    /// <param name="column">The column.</param>
    /// <param name="header">The header text, for example KEY.</param>
    /// <param name="choicesLabel">What the tick list holds, for example "Sections".</param>
    /// <param name="choices">The values a row has for the tick list. A row shows when any of them is ticked.</param>
    /// <param name="searchable">The text the search looks in.</param>
    /// <param name="changed">Called when the ticks or the search change.</param>
    /// <param name="sort">Sorts the table: true descending, false ascending, null back to the trace's order.</param>
    public ColumnFilter(
        TableColumn column,
        string header,
        string choicesLabel,
        Func<SettingRow, IEnumerable<string>> choices,
        Func<SettingRow, IEnumerable<string>> searchable,
        Action<ColumnFilter> changed,
        Action<TableColumn, bool?> sort)
    {
        Column = column;
        Header = header;
        ChoicesLabel = choicesLabel;
        _choices = choices;
        _searchable = searchable;
        _changed = changed;
        _sort = sort;
    }

    /// <summary>The column.</summary>
    public TableColumn Column { get; }

    /// <summary>The header text.</summary>
    public string Header { get; }

    /// <summary>What the tick list holds, for example "Sections".</summary>
    public string ChoicesLabel { get; }

    /// <summary>The tick list entries that match the search.</summary>
    public ObservableCollection<FilterChoice> Choices { get; } = [];

    /// <summary>Every tick list entry, matching the search or not.</summary>
    public IReadOnlyList<FilterChoice> AllChoices => _all;

    /// <summary>Text the column must contain.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    public partial string? Search { get; set; }

    /// <summary>True when the table is sorted by this column.</summary>
    [ObservableProperty]
    public partial bool IsSorted { get; set; }

    /// <summary>True when sorted by this column, Z to A.</summary>
    [ObservableProperty]
    public partial bool IsDescending { get; set; }

    /// <summary>True when the search or the ticks hide rows.</summary>
    public bool IsActive => !string.IsNullOrWhiteSpace(Search) || _all.Any(c => !c.IsChecked);

    /// <summary>A line such as "12 of 40 ticked".</summary>
    public string TickedText => string.Create(CultureInfo.CurrentCulture, $"{_all.Count(c => c.IsChecked)} of {_all.Count} ticked");

    /// <summary>Fills the tick list from <paramref name="rows"/>. Values unticked before stay unticked.</summary>
    public void Load(IEnumerable<SettingRow> rows)
    {
        var unticked = _all.Where(c => !c.IsChecked).Select(c => c.Text).ToHashSet(StringComparer.Ordinal);
        _all = rows.SelectMany(r => _choices(r).Distinct(StringComparer.Ordinal))
            .GroupBy(v => v, StringComparer.Ordinal)
            .Select(g => new FilterChoice(this, g.Key, g.Count(), !unticked.Contains(g.Key)))
            .OrderBy(c => c.Text, StringComparer.OrdinalIgnoreCase)
            .ToList();
        RefreshChoices();
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(TickedText));
    }

    /// <summary>True when <paramref name="row"/> passes this column's search and ticks.</summary>
    public bool Matches(SettingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!string.IsNullOrWhiteSpace(Search) && !_searchable(row).Any(v => v.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (_all.All(c => c.IsChecked))
        {
            return true;
        }

        var values = _choices(row).ToList();
        return values.Count == 0 || values.Any(v => _all.Any(c => c.IsChecked && string.Equals(c.Text, v, StringComparison.Ordinal)));
    }

    /// <summary>Sorts A to Z by this column.</summary>
    [RelayCommand]
    private void SortAscending() => _sort(Column, false);

    /// <summary>Sorts Z to A by this column.</summary>
    [RelayCommand]
    private void SortDescending() => _sort(Column, true);

    /// <summary>Ticks every value that matches the search, or every value when there is no search.</summary>
    [RelayCommand]
    private void SelectAll() => SetTicks(Choices, true);

    /// <summary>Unticks every value that matches the search.</summary>
    [RelayCommand]
    private void SelectNone() => SetTicks(Choices, false);

    /// <summary>Clears the search and ticks everything.</summary>
    [RelayCommand]
    public void Clear()
    {
        _quiet = true;
        Search = null;
        foreach (var choice in _all)
        {
            choice.IsChecked = true;
        }

        _quiet = false;
        RefreshChoices();
        Changed();
    }

    /// <summary>Ticks only <paramref name="text"/>, for a filter set from elsewhere.</summary>
    public void Only(string text)
    {
        _quiet = true;
        foreach (var choice in _all)
        {
            choice.IsChecked = string.Equals(choice.Text, text, StringComparison.Ordinal);
        }

        _quiet = false;
        Changed();
    }

    partial void OnSearchChanged(string? value)
    {
        RefreshChoices();
        Changed();
    }

    internal void OnChoiceChanged()
    {
        if (!_quiet)
        {
            Changed();
        }
    }

    private void SetTicks(IEnumerable<FilterChoice> choices, bool value)
    {
        _quiet = true;
        foreach (var choice in choices)
        {
            choice.IsChecked = value;
        }

        _quiet = false;
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(TickedText));
        if (!_quiet)
        {
            _changed(this);
        }
    }

    private void RefreshChoices()
    {
        Choices.Clear();
        foreach (var choice in _all.Where(c => string.IsNullOrWhiteSpace(Search) || c.Text.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            Choices.Add(choice);
        }
    }
}

/// <summary>A value in a column filter's tick list.</summary>
public sealed partial class FilterChoice : ObservableObject
{
    private readonly ColumnFilter _owner;
    private readonly bool _ready;

    internal FilterChoice(ColumnFilter owner, string text, int count, bool isChecked)
    {
        _owner = owner;
        Text = text;
        Count = count;
        IsChecked = isChecked;
        _ready = true;
    }

    /// <summary>The value.</summary>
    public string Text { get; }

    /// <summary>How many rows have it.</summary>
    public int Count { get; }

    /// <summary>True when rows with this value show.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value)
    {
        if (_ready)
        {
            _owner.OnChoiceChanged();
        }
    }
}
