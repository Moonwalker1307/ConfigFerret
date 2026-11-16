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
        // Found by fuzzing: YamlDotNet 18.1.0 reads input through a 1024-character look-ahead buffer, and some
        // text whose length is an exact multiple of 1024 makes its scanner spin for seconds. A trailing line break
        // moves the end off the boundary and changes nothing for config YAML.
        if (text.Length > 0 && text.Length % 1024 == 0)
        {
            text += "\n";
        }

        var stream = new YamlStream();
        try
        {
            RejectUnsafeAliasesAndNesting(text);
            using var reader = new StringReader(text);
            stream.Load(reader);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Found by fuzzing: YamlDotNet throws these, not YamlException, for some malformed input.
            throw new YamlException($"Malformed YAML: {ex.Message}", ex);
        }

        var roots = stream.Documents.Select(d => d.RootNode).ToList();
        RejectAliasExpansion(roots, text.Length);
        return roots;
    }

    /// <summary>The most text a document may expand to through aliases, unless the file itself is bigger.</summary>
    internal const long MaxExpandedSize = 10_000_000;

    /// <summary>
    /// Rejects documents whose aliases expand to far more than the text holds (a "billion laughs" document).
    /// Every reader of the nodes walks aliases as copies, so 330 bytes of nested aliases can turn into gigabytes.
    /// The size counts each scalar's length plus one per node, as if every alias were written out in full.
    /// </summary>
    private static void RejectAliasExpansion(List<YamlNode> roots, int textLength)
    {
        var limit = Math.Max(MaxExpandedSize, 10L * textLength);
        var sizes = new Dictionary<YamlNode, long>(ReferenceEqualityComparer.Instance);
        long total = 0;
        foreach (var root in roots)
        {
            total += ExpandedSize(root, sizes, limit);
            if (total > limit)
            {
                throw new YamlException(root.Start, root.End, string.Create(CultureInfo.InvariantCulture, $"The aliases in this YAML expand to more than {limit:N0} characters."));
            }
        }
    }

    private static long ExpandedSize(YamlNode node, Dictionary<YamlNode, long> sizes, long limit)
    {
        if (sizes.TryGetValue(node, out var known))
        {
            return known;
        }

        long size = 1;
        switch (node)
        {
            case YamlScalarNode scalar:
                size += scalar.Value?.Length ?? 0;
                break;
            case YamlSequenceNode seq:
                foreach (var child in seq.Children)
                {
                    size = Math.Min(size + ExpandedSize(child, sizes, limit), limit + 1);
                }

                break;
            case YamlMappingNode map:
                foreach (var (key, value) in map.Children)
                {
                    size = Math.Min(size + ExpandedSize(key, sizes, limit) + ExpandedSize(value, sizes, limit), limit + 1);
                }

                break;
        }

        sizes[node] = size;
        return size;
    }

    /// <summary>
    /// The most levels collections may nest, counting what an alias stands for as if it were written out. Loading
    /// and every walk over the nodes recurse once per level, and a stack overflow ends the process. A level takes
    /// about half a kilobyte of stack, so 256 levels fit well inside a 512 KB stack, which is smaller than the
    /// default thread stack on Windows, Linux and macOS. Kubernetes manifests and Helm values rarely nest more than
    /// 20 levels.
    /// </summary>
    internal const int MaxNestingDepth = 256;

    /// <summary>
    /// Rejects an alias that comes before its anchor, an alias inside the node its anchor is on, and collections
    /// nested more than <see cref="MaxNestingDepth"/> levels deep.
    /// YAML does not allow the first. All three make a stack overflow, which ends the process, so they have to be
    /// caught before the document is loaded (the first two found by fuzzing): YamlDotNet's representation model
    /// overflows on a forward alias used as a mapping key and on deep nesting, and walking a node that contains
    /// itself never ends.
    /// </summary>
    private static void RejectUnsafeAliasesAndNesting(string text)
    {
        using var reader = new StringReader(text);
        var parser = new Parser(reader);
        var current = new Dictionary<AnchorName, int>(); // the latest definition of each anchor
        var open = new Stack<int>(); // definitions of the collections being read, 0 for none
        var deepest = new Stack<int>(); // the deepest level reached inside each collection being read
        var heights = new Dictionary<int, int>(); // the levels each anchored collection spans, aliases written out
        var nextId = 1;
        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case DocumentStart:
                    current.Clear();
                    break;
                case AnchorAlias alias when !current.ContainsKey(alias.Value):
                    throw new YamlException(alias.Start, alias.End, $"The alias at {Position(alias)} is used before its anchor is defined.");
                case AnchorAlias alias when open.Contains(current[alias.Value]):
                    throw new YamlException(alias.Start, alias.End, $"The alias at {Position(alias)} is inside the node its anchor is on, so the node would contain itself.");
                case AnchorAlias alias when deepest.Count > 0:
                    var level = open.Count + heights.GetValueOrDefault(current[alias.Value]);
                    if (level > MaxNestingDepth)
                    {
                        throw new YamlException(alias.Start, alias.End, string.Create(CultureInfo.InvariantCulture, $"The alias at {Position(alias)} makes the collections in this YAML nest more than {MaxNestingDepth} levels deep."));
                    }

                    deepest.Push(Math.Max(deepest.Pop(), level));
                    break;
                case MappingStart or SequenceStart:
                    var start = (NodeEvent)parser.Current;
                    var definition = 0;
                    if (!start.Anchor.IsEmpty)
                    {
                        definition = nextId++;
                        current[start.Anchor] = definition;
                    }

                    open.Push(definition);
                    if (open.Count > MaxNestingDepth)
                    {
                        throw new YamlException(start.Start, start.End, string.Create(CultureInfo.InvariantCulture, $"The collections in this YAML nest more than {MaxNestingDepth} levels deep."));
                    }

                    deepest.Push(open.Count);
                    break;
                case MappingEnd or SequenceEnd:
                    var reached = deepest.Pop();
                    var ended = open.Pop();
                    if (ended != 0)
                    {
                        heights[ended] = reached - open.Count;
                    }

                    if (deepest.Count > 0)
                    {
                        deepest.Push(Math.Max(deepest.Pop(), reached));
                    }

                    break;
                case NodeEvent { Anchor.IsEmpty: false } scalar:
                    current[scalar.Anchor] = nextId++;
                    break;
            }
        }
    }

    // An unquoted value that starts with * is read as an alias, so the alias name may be a secret value. Messages
    // reach warnings that are not redacted, so they say where the alias is instead of naming it.
    private static string Position(AnchorAlias alias) =>
        string.Create(CultureInfo.InvariantCulture, $"line {alias.Start.Line}, column {alias.Start.Column}");

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
