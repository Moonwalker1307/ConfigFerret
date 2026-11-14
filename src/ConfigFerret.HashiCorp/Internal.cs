using System.Globalization;
using System.Text.Json;
using ConfigFerret.Sources;

namespace ConfigFerret.HashiCorp;

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
            // Another property's name is not valid UTF-16 (a lone surrogate escape), which TryGetProperty cannot compare.
            return null;
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

/// <summary>Turns layers into the names a store would know them by.</summary>
internal static class NameMatching
{
    /// <summary>
    /// The configuration keys <paramref name="layer"/> could set, used to match a store by name: its name (environment
    /// variable, env entry, ConfigMap/Secret key) as a configuration key, with and without each of
    /// <paramref name="stripPrefixes"/>, and, for a .NET provider named after the store, its configuration key.
    /// </summary>
    public static List<string> CandidateKeys(ProvenanceStep layer, IEnumerable<string> stripPrefixes, string providerHint)
    {
        var keys = new List<string>();
        // An external secret reference names a path in a store, not a configuration key.
        if (layer.Reference is not ExternalSecretRef && LayerNames.Of(layer) is { } name)
        {
            Add(keys, name);
            foreach (var prefix in stripPrefixes.Where(p => !string.IsNullOrEmpty(p)))
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    Add(keys, name[prefix.Length..]);
                }
            }
        }
        else if (layer.Reference is null
            && layer.ConfigurationKey is { } key
            && layer.Source.Contains(providerHint, StringComparison.OrdinalIgnoreCase))
        {
            // A configuration provider that reads the store directly, such as a Vault or Consul provider package.
            Add(keys, key);
        }

        return keys;
    }

    private static void Add(List<string> keys, string name)
    {
        var key = LayerNames.ToConfigurationKey(name);
        if (key.Length > 0 && !keys.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            keys.Add(key);
        }
    }

    /// <summary>Joins configuration key parts, skipping empty ones.</summary>
    public static string Combine(string first, string second) =>
        first.Length == 0 ? second : second.Length == 0 ? first : first + ":" + second;
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
