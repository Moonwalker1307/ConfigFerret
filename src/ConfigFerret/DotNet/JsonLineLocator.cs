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
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var path = new List<string>();
        var arrayIndex = new Stack<int>(); // -1 for objects
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

            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    property = reader.GetString();
                    continue;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    arrayIndex.Pop();
                    if (path.Count > 0)
                    {
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
            }
            else if (segment is not null)
            {
                var key = path.Count == 0 ? segment : ConfigurationPath.Combine([.. path, segment]);
                result.TryAdd(key, line);
            }
        }

        return result;
    }
}
