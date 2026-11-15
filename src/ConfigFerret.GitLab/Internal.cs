using System.Globalization;
using System.Text.Json;
using ConfigFerret.Sources;

namespace ConfigFerret.GitLab;

/// <summary>Reads JSON from servers that may send any shape, without throwing for a shape it does not expect.</summary>
internal static class JsonRead
{
    public static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return element.TryGetProperty(name, out var value) ? value : null;
        }
        catch (InvalidOperationException)
        {
            // Another property's name is not valid UTF-16 (a lone surrogate escape), which TryGetProperty cannot compare,
            // so compare the names that can be read one by one.
            return Properties(element).Where(p => p.Name == name).Select(p => (JsonElement?)p.Value).FirstOrDefault();
        }
    }

    public static JsonElement? Object(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Object } value ? value : null;

    /// <summary>The string, or null when it is not a string or not valid UTF-16 (a lone surrogate escape).</summary>
    public static string? String(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.String } value)
        {
            return null;
        }

        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static long? Number(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var n) ? n : null;

    public static DateTimeOffset? Date(JsonElement? element) =>
        String(element) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    /// <summary>The properties whose names are valid strings, in document order.</summary>
    public static IEnumerable<(string Name, JsonElement Value)> Properties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in element.EnumerateObject())
        {
            string name;
            try
            {
                name = property.Name;
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            yield return (name, property.Value);
        }
    }

    /// <summary>
    /// A value as the .NET JSON configuration provider would store it: strings as they are, other primitives as their
    /// text (<c>True</c>, <c>42</c>), null for null, and objects and arrays as their raw JSON.
    /// </summary>
    public static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => String(value),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
        _ => value.ToString(),
    };
}

/// <summary>Parses the plain string settings a host passes to an <see cref="IConfigSourceProvider"/>.</summary>
internal static class SettingValues
{
    public static string? Text(IReadOnlyDictionary<string, string?> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    public static string Required(IReadOnlyDictionary<string, string?> values, string name) =>
        Text(values, name) ?? throw new ArgumentException($"{name} is required.", nameof(values));

    public static Uri Url(IReadOnlyDictionary<string, string?> values, string name, string? defaultValue = null)
    {
        var text = Text(values, name) ?? defaultValue ?? throw new ArgumentException($"{name} is required.", nameof(values));
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            ? uri
            : throw new ArgumentException($"{name} must be an absolute URL.", nameof(values));
    }

    public static bool Boolean(IReadOnlyDictionary<string, string?> values, string name, bool defaultValue) =>
        Text(values, name) is not { } text ? defaultValue
        : bool.TryParse(text, out var value) ? value
        : throw new ArgumentException($"{name} must be true or false.", nameof(values));

    public static IEnumerable<string> Lines(IReadOnlyDictionary<string, string?> values, string name) =>
        (Text(values, name) ?? string.Empty)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>Reads single lines of YAML without a full parser, so any text can be read without throwing.</summary>
internal static class YamlLine
{
    /// <summary>The number of leading spaces.</summary>
    public static int Indent(string line)
    {
        var i = 0;
        while (i < line.Length && line[i] == ' ')
        {
            i++;
        }

        return i;
    }

    /// <summary>True for an empty line or a comment.</summary>
    public static bool IsBlank(string line)
    {
        var text = line.TrimStart();
        return text.Length == 0 || text[0] == '#';
    }

    /// <summary>
    /// A plain, single-quoted or double-quoted scalar as its text: quotes removed, <c>''</c> and simple backslash escapes
    /// undone, and a trailing <c># comment</c> dropped from a plain one. Null for an empty value or a block indicator.
    /// </summary>
    public static string? Scalar(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text[0] == '\'')
        {
            var end = text.IndexOf('\'', 1);
            while (end > 0 && end + 1 < text.Length && text[end + 1] == '\'')
            {
                end = text.IndexOf('\'', end + 2);
            }

            return (end < 0 ? text[1..] : text[1..end]).Replace("''", "'", StringComparison.Ordinal);
        }

        if (text[0] == '"')
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 1; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"')
                {
                    break;
                }

                if (c == '\\' && i + 1 < text.Length)
                {
                    i++;
                    sb.Append(text[i] switch { 'n' => '\n', 't' => '\t', _ => text[i] });
                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        var comment = text.IndexOf(" #", StringComparison.Ordinal);
        if (comment >= 0)
        {
            text = text[..comment].TrimEnd();
        }

        return text is "|" or ">" or "|-" or ">-" or "|+" or ">+" ? null : text;
    }
}
