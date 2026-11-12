using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.DotNet;

/// <summary>Finds the line of every leaf value in a JSON configuration file, keyed like .NET configuration keys.</summary>
internal static class JsonLineLocator
{
    private static readonly Dictionary<string, int> Empty = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, int> TryLocate(string path, TraceDiagnostics diagnostics)
    {
        try
        {
            return File.Exists(path) ? Locate(File.ReadAllBytes(path)) : Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            diagnostics.Warn($"Could not read line numbers from {path}: {ex.Message}");
            return Empty;
        }
    }

    public static Dictionary<string, int> Locate(ReadOnlySpan<byte> json)
    {
        // Found by fuzzing: Utf8JsonReader rejects a byte order mark, which editors such as Visual Studio
        // often write, while the .NET JSON provider reads the file fine.
        if (json.StartsWith("\uFEFF"u8))
        {
            json = json[3..];
        }

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var path = new List<string>();
        var arrayIndex = new Stack<int>(); // -1 for objects
        var openLines = new Stack<(int Line, bool HasChildren)>(); // where each open container starts
        string? property = null;
        var line = 1;
        long counted = 0;

        while (reader.Read())
        {
            var start = reader.TokenStartIndex;
            for (var i = counted; i < start; i++)
            {
                if (json[(int)i] == (byte)'\n')
                {
                    line++;
                }
            }

            counted = start;

            if (reader.TokenType is not (JsonTokenType.EndObject or JsonTokenType.EndArray) && openLines.TryPop(out var parent))
            {
                openLines.Push(parent with { HasChildren = true });
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    property = ReadPropertyName(ref reader);
                    continue;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    arrayIndex.Pop();
                    var (openLine, hasChildren) = openLines.Pop();
                    if (path.Count > 0)
                    {
                        // .NET configuration keeps an empty object or array as a key with an empty value.
                        if (!hasChildren)
                        {
                            result.TryAdd(ConfigurationPath.Combine(path), openLine);
                        }

                        path.RemoveAt(path.Count - 1);
                    }

                    continue;
            }

            string? segment = null;
            if (arrayIndex.Count > 0)
            {
                if (arrayIndex.Peek() >= 0)
                {
                    var index = arrayIndex.Pop();
                    segment = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    arrayIndex.Push(index + 1);
                }
                else
                {
                    segment = property;
                }
            }

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                if (segment is not null)
                {
                    path.Add(segment);
                }

                arrayIndex.Push(reader.TokenType == JsonTokenType.StartArray ? 0 : -1);
                openLines.Push((line, false));
            }
            else if (segment is not null)
            {
                var key = path.Count == 0 ? segment : ConfigurationPath.Combine([.. path, segment]);
                result.TryAdd(key, line);
            }
        }

        return result;
    }

    private static string? ReadPropertyName(ref Utf8JsonReader reader)
    {
        try
        {
            return reader.GetString();
        }
        catch (InvalidOperationException)
        {
            // Invalid UTF-8 in a property name. The .NET JSON provider still loads the file, so read the name
            // the same lenient way rather than failing the whole file: replace the bad bytes, then unescape.
            var lenient = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(reader.ValueSpan));
            var quoted = new byte[lenient.Length + 2];
            quoted[0] = quoted[^1] = (byte)'"';
            lenient.CopyTo(quoted, 1);
            var inner = new Utf8JsonReader(quoted);
            inner.Read();
            return inner.GetString();
        }
    }
}
