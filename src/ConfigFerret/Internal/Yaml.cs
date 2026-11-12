using System.Globalization;
using YamlDotNet.RepresentationModel;

namespace ConfigFerret.Internal;

/// <summary>Small helpers over the YamlDotNet representation model.</summary>
internal static class Yaml
{
    public static List<YamlNode> LoadDocuments(string text)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(text);
        stream.Load(reader);
        return stream.Documents.Select(d => d.RootNode).ToList();
    }

    public static YamlNode? Get(this YamlNode? node, string key) =>
        node is YamlMappingNode map && map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    public static string? Str(this YamlNode? node, string key) => node.Get(key).Scalar();

    public static string? Scalar(this YamlNode? node) =>
        node is YamlScalarNode { Value: var v } scalar && !IsNull(scalar) ? v : null;

    public static bool IsNull(this YamlNode? node) =>
        node is null
        || node is YamlScalarNode { Style: not (YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted) } s
            && (s.Value is null || s.Value is "" or "~" or "null" or "Null" or "NULL");

    public static IEnumerable<YamlNode> Items(this YamlNode? node) =>
        node is YamlSequenceNode seq ? seq.Children : [];

    public static IEnumerable<KeyValuePair<string, YamlNode>> Pairs(this YamlNode? node)
    {
        if (node is not YamlMappingNode map)
        {
            yield break;
        }

        foreach (var (k, v) in map.Children)
        {
            if (k is YamlScalarNode { Value: { } key })
            {
                yield return new(key, v);
            }
        }
    }

    public static int Line(this YamlNode node) => (int)node.Start.Line;

    /// <summary>Renders a node as a compact, flow-style string (scalars as-is).</summary>
    public static string? Render(YamlNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case YamlScalarNode s:
                return s.IsNull() ? null : s.Value;
            case YamlSequenceNode seq:
                return "[" + string.Join(", ", seq.Children.Select(c => Render(c) ?? "null")) + "]";
            case YamlMappingNode map:
                return "{" + string.Join(", ", map.Children.Select(p => $"{Render(p.Key)}: {Render(p.Value) ?? "null"}")) + "}";
            default:
                return node.ToString();
        }
    }

    public static string Location(string file, YamlNode node) =>
        string.Create(CultureInfo.InvariantCulture, $"{file}:{node.Line()}");
}
