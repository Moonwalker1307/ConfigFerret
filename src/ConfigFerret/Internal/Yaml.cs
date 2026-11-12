using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace ConfigFerret.Internal;

/// <summary>Small helpers over the YamlDotNet representation model.</summary>
internal static class Yaml
{
    /// <summary>Parses every YAML document in <paramref name="text"/>.</summary>
    /// <exception cref="YamlException">The text is not valid YAML.</exception>
    public static List<YamlNode> LoadDocuments(string text)
    {
        var stream = new YamlStream();
        try
        {
            RejectForwardAliases(text);
            using var reader = new StringReader(text);
            stream.Load(reader);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Found by fuzzing: YamlDotNet throws these, not YamlException, for some malformed input.
            throw new YamlException($"Malformed YAML: {ex.Message}", ex);
        }

        return stream.Documents.Select(d => d.RootNode).ToList();
    }

    /// <summary>
    /// Rejects an alias that comes before its anchor. YAML does not allow it, and YamlDotNet's representation
    /// model accepts it but then overflows the stack when the alias is a mapping key (found by fuzzing).
    /// A stack overflow ends the process, so this has to be caught before the document is loaded.
    /// </summary>
    private static void RejectForwardAliases(string text)
    {
        using var reader = new StringReader(text);
        var parser = new Parser(reader);
        var anchors = new HashSet<AnchorName>();
        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case DocumentStart:
                    anchors.Clear();
                    break;
                case AnchorAlias alias when !anchors.Contains(alias.Value):
                    throw new YamlException(alias.Start, alias.End, $"The alias *{alias.Value} is used before its anchor &{alias.Value} is defined.");
                case NodeEvent { Anchor.IsEmpty: false } node:
                    anchors.Add(node.Anchor);
                    break;
            }
        }
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
        var map = node as YamlMappingNode;
        if (map is null)
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
