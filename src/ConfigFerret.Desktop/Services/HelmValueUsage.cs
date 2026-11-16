using System.Buffers;
using System.Globalization;
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
/// A template that a dependency defines, such as one of a library chart, reads the values of the chart that includes it, so
/// the templates of the charts under <c>charts/</c> count too when the chart includes one of theirs. When a template calls
/// <c>tpl</c>, references in the values files and the chart's other files count too. When the chart includes a template
/// that isn't there, the scan can't know what it reads, and no value is dead.
/// </summary>
/// <remarks>
/// The chart comes from a repository someone else wrote. Links are not followed, at most 2 MB of each file is read, and
/// at most 2,000 files each of the chart's templates, its subcharts' templates and the chart files tpl renders. Every
/// pattern takes time linear in the text it reads, and a values file gives at most 100,000 values with paths of at most
/// 256 characters. A pattern that still times out gives no dead values at all,
/// because the scan no longer knows what is read.
/// </remarks>
public static partial class HelmValueUsage
{
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxPathLength = 256;
    private const int MaxLeaves = 100_000;
    private const int MaxFiles = 2000;
    private const int MaxChartDepth = 8;
    private const int MatchTimeoutMilliseconds = 5000;

    // Links are skipped: one out of the chart reads other files.
    private static readonly EnumerationOptions AllFiles = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
    private static readonly EnumerationOptions TopFiles = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    /// <summary>The values in the chart's values.yaml and <paramref name="valuesFiles"/> that no template reads.</summary>
    public static IReadOnlyList<DeadHelmValue> FindDead(string chartFolder, IEnumerable<string> valuesFiles) => FindDead(chartFolder, valuesFiles, string.Empty, out _);

    /// <summary>
    /// The values in the chart's values.yaml and <paramref name="valuesFiles"/> that no template reads, and
    /// <paramref name="notes"/> on why there are none when the scan can't know what the templates read. The notes name
    /// the chart <paramref name="source"/>, or "Helm chart" and its folder's name when that is empty.
    /// </summary>
    internal static IReadOnlyList<DeadHelmValue> FindDead(string chartFolder, IEnumerable<string> valuesFiles, string source, out IReadOnlyList<string> notes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chartFolder);
        ArgumentNullException.ThrowIfNull(valuesFiles);
        notes = [];
        var templates = Path.Combine(chartFolder, "templates");
        if (!Directory.Exists(templates) || IsLink(templates))
        {
            return [];
        }

        var texts = ReadAll(Directory.EnumerateFiles(templates, "*", AllFiles));

        // The texts of template files, the chart's own and its subcharts'. A note quotes only a name found in one of
        // them, never one from a values file or another text that tpl renders.
        var templateTexts = new HashSet<string>(texts, ReferenceEqualityComparer.Instance);

        var files = new List<string>();
        var defaults = Path.Combine(chartFolder, "values.yaml");
        if (File.Exists(defaults))
        {
            files.Add(defaults);
        }

        files.AddRange(valuesFiles.Where(File.Exists));
        var values = new List<(string Path, string File)>();
        var valuesTexts = new List<string>();
        foreach (var file in files.Distinct(StringComparer.Ordinal))
        {
            try
            {
                var text = ReadSmall(file) ?? string.Empty;
                valuesTexts.Add(text);
                foreach (var path in Leaves(text))
                {
                    values.Add((path, Display(chartFolder, file)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlException)
            {
            }
        }

        var chart = Printable(string.IsNullOrWhiteSpace(source) ? "Helm chart " + Path.GetFileName(Path.TrimEndingDirectorySeparator(chartFolder)) : source);
        try
        {
            if (Subcharts(chartFolder) is not { } subcharts)
            {
                notes = [$"The dependencies of {chart} in Chart.yaml, requirements.yaml or charts/ can't be read, so no Helm value is marked dead: without them a subchart's values can't be told from the chart's own."];
                return [];
            }

            // tpl renders text as a template: a string in the values files, or a file of the chart read with .Files.
            // That text can include templates too, so add it before looking for them.
            var tpl = false;
            var chartFiles = false;
            void AddWhatTplRenders()
            {
                if (!tpl && texts.Any(t => TplCall().IsMatch(t)))
                {
                    tpl = true;
                    texts.AddRange(valuesTexts);
                }

                if (tpl && !chartFiles && texts.Any(t => t.Contains(".Files.", StringComparison.Ordinal)))
                {
                    chartFiles = true;
                    texts.AddRange(ReadAll(OtherChartFiles(chartFolder)));
                }
            }

            AddWhatTplRenders();

            // A template a dependency defines, such as one of a library chart, reads the values of the chart that
            // includes it. Read the dependencies' templates only when the chart includes one of theirs.
            if (Undefined(texts, templateTexts) is not null)
            {
                var subchartTemplates = ReadAll(SubchartTemplates(chartFolder, 0));
                templateTexts.UnionWith(subchartTemplates);
                texts.AddRange(subchartTemplates);
                AddWhatTplRenders();
            }

            if (Undefined(texts, templateTexts) is { } undefined)
            {
                notes = [undefined.Missing
                    ? $"The templates of {chart} include {undefined.What}, so no Helm value is marked dead: that template may read any of them. Put the dependency that defines it under charts/, unpacked, to check them."
                    : $"The templates of {chart} include {undefined.What}, so no Helm value is marked dead: that template may read any of them."];
                return [];
            }

            return Dead(References(texts), values, subcharts);
        }
        catch (RegexMatchTimeoutException)
        {
            notes = [$"Reading {chart} for the values its templates read took too long, so no Helm value is marked dead."];
            return [];
        }
    }

    // A name for a note without control or format characters, which could hide or reorder the text around it.
    private static string Printable(string name) =>
        string.Concat(name.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) is not (UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)));

    /// <summary>The values in <paramref name="values"/> that none of <paramref name="templates"/> reads.</summary>
    public static IReadOnlyList<DeadHelmValue> FindDead(IEnumerable<string> templates, IEnumerable<(string Path, string File)> values, IEnumerable<string> subcharts)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(subcharts);

        try
        {
            return Dead(References(templates), values, subcharts);
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }
    }

    // The value paths the templates read; an empty one for a bare .Values.
    private static HashSet<string> References(IEnumerable<string> templates)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
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

        return references;
    }

    private static List<DeadHelmValue> Dead(HashSet<string> references, IEnumerable<(string Path, string File)> values, IEnumerable<string> subcharts)
    {
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

    // The first template the texts include that none of them defines, for a note: its name in quotes when a template
    // file includes it, else words for it; Missing is false when its name is worked out at render time. Null when the
    // texts define every template they include.
    private static (string What, bool Missing)? Undefined(List<string> texts, HashSet<string> templateTexts)
    {
        var defined = texts.SelectMany(t => TemplateDefinition().Matches(t)).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
        foreach (var text in texts)
        {
            foreach (Match m in TemplateCall().Matches(text))
            {
                if (m.Groups["dynamic"].Success)
                {
                    return ("a template whose name is worked out when the chart is rendered", false);
                }

                if (m.Groups["name"].Success && !defined.Contains(m.Groups["name"].Value))
                {
                    return (templateTexts.Contains(text) ? $"\"{Printable(m.Groups["name"].Value)}\", which isn't in the chart" : "a template that isn't in the chart", true);
                }
            }
        }

        return null;
    }

    // The files in the templates folders of the charts under charts/, and of the charts under theirs, at most
    // MaxChartDepth deep. Only those folders are walked; links are not followed.
    private static IEnumerable<string> SubchartTemplates(string chartFolder, int depth)
    {
        var charts = Path.Combine(chartFolder, "charts");
        if (depth >= MaxChartDepth || !Directory.Exists(charts) || IsLink(charts))
        {
            return [];
        }

        return Directory.EnumerateDirectories(charts, "*", TopFiles).SelectMany(chart =>
        {
            var templates = Path.Combine(chart, "templates");
            var own = Directory.Exists(templates) && !IsLink(templates) ? Directory.EnumerateFiles(templates, "*", AllFiles) : [];
            return own.Concat(SubchartTemplates(chart, depth + 1));
        });
    }

    // The chart's files outside templates/, charts/ and .git, which a template can read with .Files.
    private static IEnumerable<string> OtherChartFiles(string chartFolder) =>
        Directory.EnumerateFiles(chartFolder, "*", TopFiles).Concat(
            Directory.EnumerateDirectories(chartFolder, "*", TopFiles)
                .Where(d => Path.GetFileName(d) is not "templates" and not "charts" and not ".git")
                .SelectMany(d => Directory.EnumerateFiles(d, "*", AllFiles)));

    // The texts of the first MaxFiles files, leaving out those bigger than MaxFileBytes or that can't be read.
    private static List<string> ReadAll(IEnumerable<string> files)
    {
        var texts = new List<string>();
        foreach (var file in files.Take(MaxFiles))
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

        return texts;
    }

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

    // {{ define "name" }} and {{ block "name" . }}
    [GeneratedRegex(@"\{\{-?\s*(?:define|block)\s+""(?<name>[^""\r\n]{0,256})""", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex TemplateDefinition();

    // include "name" and {{ template "name" }}. include (print $.Template.BasePath "/x.yaml") renders a template file of
    // the chart itself; any other name in brackets, a variable or a raw string is worked out at render time.
    [GeneratedRegex(@"(?:\binclude|\{\{-?\s*template)\s+(?:""(?<name>[^""\r\n]{0,256})""|\(\s*print\s+\$?\.Template\.BasePath\b|(?<dynamic>[($.`""]))", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex TemplateCall();

    // tpl .Values.x . or (tpl (.Files.Get "f") $), but not the word in _helpers.tpl.
    [GeneratedRegex(@"(?<![\w.])tpl\s", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex TplCall();
}
