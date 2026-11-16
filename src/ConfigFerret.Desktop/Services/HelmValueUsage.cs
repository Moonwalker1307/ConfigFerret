using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using ConfigFerret.Internal;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ConfigFerret.Desktop.Services;

/// <summary>A Helm value that no template in the chart reads.</summary>
/// <param name="Path">The value's path, such as <c>features.recommendations</c>.</param>
/// <param name="File">The values file that sets it, relative to the chart folder when inside it.</param>
public sealed record DeadHelmValue(string Path, string File);

/// <summary>
/// Finds Helm values that no template reads: a value is read when a template uses <c>.Values</c> with its path, a path
/// above it (such as <c>toYaml .Values.env</c>), or a path below it. Values for subcharts and <c>global</c> always count as read.
/// </summary>
/// <remarks>
/// The chart comes from a repository someone else wrote. Links are not followed, at most 2 MB of each file and at most
/// 2,000 templates are read, every pattern takes time linear in the text it reads, and a values file gives at most
/// 100,000 values with paths of at most 256 characters. A pattern that still times out gives no dead values at all,
/// because the scan no longer knows what is read.
/// </remarks>
public static partial class HelmValueUsage
{
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxPathLength = 256;
    private const int MaxLeaves = 100_000;
    private const int MatchTimeoutMilliseconds = 5000;

    /// <summary>The values in the chart's values.yaml and <paramref name="valuesFiles"/> that no template reads.</summary>
    public static IReadOnlyList<DeadHelmValue> FindDead(string chartFolder, IEnumerable<string> valuesFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chartFolder);
        ArgumentNullException.ThrowIfNull(valuesFiles);
        var templates = Path.Combine(chartFolder, "templates");
        if (!Directory.Exists(templates) || IsLink(templates))
        {
            return [];
        }

        var texts = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(templates, "*", options).Take(2000))
        {
            try
            {
                if (ReadSmall(file) is { } text)
                {
                    texts.Add(text);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var files = new List<string>();
        var defaults = Path.Combine(chartFolder, "values.yaml");
        if (File.Exists(defaults))
        {
            files.Add(defaults);
        }

        files.AddRange(valuesFiles.Where(File.Exists));
        var values = new List<(string Path, string File)>();
        foreach (var file in files.Distinct(StringComparer.Ordinal))
        {
            try
            {
                foreach (var path in Leaves(ReadSmall(file) ?? string.Empty))
                {
                    values.Add((path, Display(chartFolder, file)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlException)
            {
            }
        }

        // Without the dependencies the scan can't tell a subchart's values from the chart's own.
        return Subcharts(chartFolder) is { } subcharts ? FindDead(texts, values, subcharts) : [];
    }

    /// <summary>The values in <paramref name="values"/> that none of <paramref name="templates"/> reads.</summary>
    public static IReadOnlyList<DeadHelmValue> FindDead(IEnumerable<string> templates, IEnumerable<(string Path, string File)> values, IEnumerable<string> subcharts)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(subcharts);

        var references = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var template in templates)
            {
                // index .Values.a "b" "c" reads a.b.c. Take those out first, so their .Values isn't read as a bare one.
                var text = IndexReference().Replace(template, m =>
                {
                    var parts = m.Groups["prefix"].Value.Split('.', StringSplitOptions.RemoveEmptyEntries).Concat(m.Groups["key"].Captures.Select(c => c.Value));
                    references.Add(string.Join('.', parts));
                    return " ";
                });

                foreach (Match m in ValuesReference().Matches(text))
                {
                    references.Add(m.Groups["path"].Value.TrimStart('.'));
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }

        // A bare .Values, for example toYaml .Values or a dynamic index, reads everything.
        if (references.Contains(string.Empty))
        {
            return [];
        }

        var skip = new HashSet<string>(subcharts, StringComparer.Ordinal) { "global" };
        var dead = new List<DeadHelmValue>();
        foreach (var (path, file) in values)
        {
            if (skip.Contains(path.Split('.')[0]) || references.Any(r => Related(r, path)))
            {
                continue;
            }

            if (!dead.Any(d => d.Path == path && d.File == file))
            {
                dead.Add(new DeadHelmValue(path, file));
            }
        }

        return dead;
    }

    private static bool Related(string reference, string path) =>
        reference == path
        || path.StartsWith(reference + ".", StringComparison.Ordinal)
        || reference.StartsWith(path + ".", StringComparison.Ordinal);

    // Every leaf of the values document: scalars and sequences, by their dotted path. A path longer than MaxPathLength is
    // left out: one long key above many values would otherwise make the paths far bigger than the file. More than
    // MaxLeaves values, which aliases can give from a small file, is a YamlException, so the file is skipped.
    internal static IEnumerable<string> Leaves(string yaml)
    {
        // Yaml.LoadDocuments also rejects the recursive anchors and alias bombs that overflow the stack in YamlDotNet.
        var result = new List<string>();
        foreach (var root in Yaml.LoadDocuments(yaml))
        {
            if (root is YamlMappingNode mapping)
            {
                Walk(mapping, string.Empty, result, 0);
            }
        }

        return result;
    }

    private static void Walk(YamlMappingNode node, string prefix, List<string> result, int depth)
    {
        foreach (var (key, value) in node.Children)
        {
            if (key is not YamlScalarNode { Value: { Length: > 0 } name })
            {
                continue;
            }

            if ((prefix.Length == 0 ? 0 : prefix.Length + 1) + name.Length > MaxPathLength)
            {
                continue;
            }

            var path = prefix.Length == 0 ? name : prefix + "." + name;

            if (value is YamlMappingNode { Children.Count: > 0 } child && depth < 32)
            {
                Walk(child, path, result, depth + 1);
            }
            else if (result.Count < MaxLeaves)
            {
                result.Add(path);
            }
            else
            {
                throw new YamlException($"The values hold more than {MaxLeaves:N0} values.");
            }
        }
    }

    // The names of the chart's subcharts, from charts/ and the dependencies in Chart.yaml or requirements.yaml (apiVersion
    // v1). Null when one of them is a link, can't be read, is too big or isn't YAML, or a pattern timed out.
    private static List<string>? Subcharts(string chartFolder)
    {
        var names = new List<string>();
        try
        {
            var charts = Path.Combine(chartFolder, "charts");
            if (Directory.Exists(charts))
            {
                if (IsLink(charts))
                {
                    return null;
                }

                names.AddRange(Directory.EnumerateFileSystemEntries(charts).Select(p => Path.GetFileName(p)).Select(n => SubchartName().Match(n).Groups["name"].Value));
            }

            foreach (var chartFile in new[] { Path.Combine(chartFolder, "Chart.yaml"), Path.Combine(chartFolder, "requirements.yaml") })
            {
                if (File.Exists(chartFile))
                {
                    if (ReadSmall(chartFile) is not { } text)
                    {
                        return null;
                    }

                    if (DependencyNames(text) is not { } dependencies)
                    {
                        return null;
                    }

                    names.AddRange(dependencies);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException or YamlException)
        {
            return null;
        }

        return names.Where(n => n.Length > 0).ToList();
    }

    // The names and aliases of the dependencies in Chart.yaml or requirements.yaml, in any YAML form, or null when
    // dependencies is something other than a list of mappings. Yaml.LoadDocuments limits what aliases and nesting cost.
    private static List<string>? DependencyNames(string yaml)
    {
        var names = new List<string>();
        foreach (var root in Yaml.LoadDocuments(yaml))
        {
            if (root is not YamlMappingNode chart || !chart.Children.TryGetValue(new YamlScalarNode("dependencies"), out var dependencies)
                || dependencies is YamlScalarNode { Value: null or "" or "~" or "null" })
            {
                continue;
            }

            if (dependencies is not YamlSequenceNode list || list.Children.Any(d => d is not YamlMappingNode))
            {
                return null;
            }

            foreach (var dependency in list.Children.Cast<YamlMappingNode>())
            {
                foreach (var key in new[] { "name", "alias" })
                {
                    if (dependency.Children.TryGetValue(new YamlScalarNode(key), out var name) && name is YamlScalarNode { Value: { } value })
                    {
                        names.Add(value);
                    }
                }
            }
        }

        return names;
    }

    private static bool IsLink(string folder) => new DirectoryInfo(folder).LinkTarget is not null;

    // The text of a file, or null when it is a link or holds more than MaxFileBytes. At most MaxFileBytes + 1 bytes are
    // read whatever length the file gives: an empty one is not opened, as a device or a pipe says 0 and may never end.
    private static string? ReadSmall(string file)
    {
        var info = new FileInfo(file);
        if (info.LinkTarget is not null || info.Length > MaxFileBytes)
        {
            return null;
        }

        if (info.Length == 0)
        {
            return string.Empty;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(MaxFileBytes + 1);
        try
        {
            int count;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1))
            {
                count = stream.ReadAtLeast(buffer.AsSpan(0, MaxFileBytes + 1), MaxFileBytes + 1, throwOnEndOfStream: false);
            }

            if (count > MaxFileBytes)
            {
                return null;
            }

            using var reader = new StreamReader(new MemoryStream(buffer, 0, count), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string Display(string chartFolder, string file)
    {
        var relative = Path.GetRelativePath(chartFolder, file);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? Path.GetFileName(file) : relative.Replace('\\', '/');
    }

    // Each pattern reads a run of one kind of character in one way only, or reads only a short text, so it can't try a
    // long text in many ways. Each also has a timeout, in case one still can't keep up.

    // .Values.a.b, $.Values.a.b, and a bare .Values.
    [GeneratedRegex(@"\.Values(?<path>(?:\.[A-Za-z_][\w-]*)*)", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex ValuesReference();

    // index .Values "a" "b"
    [GeneratedRegex(@"index\s+\$?\.Values(?<prefix>(?:\.[A-Za-z_][\w-]*)*)(?:\s+""(?<key>[^""]+)"")+", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex IndexReference();

    // A file name in charts/, which is at most a few hundred characters.
    [GeneratedRegex(@"^(?<name>.+?)(?:-\d+\.\d+\.\d+.*)?(?:\.tgz)?$", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex SubchartName();
}
