using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ConfigFerret.Desktop.Views;

/// <summary>
/// Lets a binding set style classes, for example <c>ui:DynamicClasses.Classes="{Binding Css}"</c>, so theme styles
/// (and their dynamic resources) colour an element by what it shows.
/// </summary>
public static class DynamicClasses
{
    /// <summary>Space-separated class names to add; the previous value's classes are removed.</summary>
    public static readonly AttachedProperty<string?> ClassesProperty =
        AvaloniaProperty.RegisterAttached<StyledElement, string?>("Classes", typeof(DynamicClasses));

    static DynamicClasses() =>
        ClassesProperty.Changed.AddClassHandler<StyledElement>((element, args) =>
        {
            foreach (var old in Split(args.OldValue as string))
            {
                element.Classes.Remove(old);
            }

            foreach (var added in Split(args.NewValue as string))
            {
                element.Classes.Add(added);
            }
        });

    /// <summary>Gets the classes.</summary>
    public static string? GetClasses(StyledElement element) => element?.GetValue(ClassesProperty);

    /// <summary>Sets the classes.</summary>
    public static void SetClasses(StyledElement element, string? value) => element?.SetValue(ClassesProperty, value);

    private static string[] Split(string? value) => value?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
}

/// <summary>Converters used by the views.</summary>
public static class Converters
{
    /// <summary>Turns an icon resource key, such as IconAnchor, into its geometry.</summary>
    public static readonly IValueConverter IconGeometry = new FuncValueConverter<string?, Geometry?>(key =>
        key is not null && Application.Current?.TryFindResource(key, out var value) == true ? value as Geometry : null);

    /// <summary>The icon for a problem severity.</summary>
    public static readonly IValueConverter SeverityIcon = new FuncValueConverter<string?, Geometry?>(severity =>
        Application.Current?.TryFindResource(severity switch { "error" => "IconAlert", "warning" => "IconAlert", _ => "IconInfo" }, out var value) == true ? value as Geometry : null);

    /// <summary>The arrow beside a sorted column's header: down for Z to A, up for A to Z.</summary>
    public static readonly IValueConverter SortIcon = new FuncValueConverter<bool, Geometry?>(descending =>
        Application.Current?.TryFindResource(descending ? "IconArrowDown" : "IconArrowUp", out var value) == true ? value as Geometry : null);

    /// <summary>True when a number is greater than zero.</summary>
    public static readonly IValueConverter Positive = new FuncValueConverter<int, bool>(n => n > 0);

    /// <summary>Formats a number with thousands separators.</summary>
    public static readonly IValueConverter Number = new FuncValueConverter<int, string>(n => n.ToString("N0", CultureInfo.CurrentCulture));
}
