using System.Buffers;
using System.Globalization;
using System.IO.Enumeration;
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
/// The chart comes from a repository someone else wrote. Links are not followed, at most 2 MB of each file and at most
/// 2,000 files and 32 MB in all are read, every pattern takes time linear in the text it reads, and a values file gives
/// at most 100,000 values with paths of at most 256 characters. When part of the chart is not read, because of a link,
/// a limit or a folder that can't be listed, or a pattern still times out, there are no dead values at all, because the
/// scan no longer knows what is read.
/// </remarks>
public static partial class HelmValueUsage
{
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxPathLength = 256;
    private const int MaxLeaves = 100_000;
    private const int MaxFiles = 2000;
    private const long MaxScanBytes = 32 * 1024 * 1024;
    private const int MaxChartDepth = 8;
    private const int MatchTimeoutMilliseconds = 5000;

    // Every entry, so that a folder that can't be listed or a link is seen rather than left out.
    private static readonly EnumerationOptions Entries = new() { IgnoreInaccessible = false, AttributesToSkip = 0 };

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
        if (!Directory.Exists(templates))
        {
            return [];
        }

        var chart = Printable(string.IsNullOrWhiteSpace(source) ? "Helm chart " + Path.GetFileName(Path.TrimEndingDirectorySeparator(chartFolder)) : source);
        var budget = new Budget();
        try
        {
            if (IsLink(templates))
            {
                throw new PartlyReadException("templates/ is a link");
            }

            var texts = ReadAll(Files(templates), chartFolder, budget);

            // The texts of template files, the chart's own and its subcharts'. A note quotes only a name found in one
            // of them, never one from a values file or another text that tpl renders.
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
                var text = Read(file, chartFolder, budget);
                valuesTexts.Add(text);
                try
                {
                    foreach (var path in Leaves(text))
                    {
                        values.Add((path, Display(chartFolder, file)));
                    }
                }
                catch (YamlException)
                {
                }
            }

            if (Subcharts(chartFolder, budget) is not { } subcharts)
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
                    texts.AddRange(ReadAll(OtherChartFiles(chartFolder), chartFolder, budget));
                }
            }

            AddWhatTplRenders();

            // A template a dependency defines, such as one of a library chart, reads the values of the chart that
            // includes it. Read the dependencies' templates only when the chart includes one of theirs.
            if (Undefined(texts, templateTexts) is not null)
            {
                var subchartTemplates = ReadAll(SubchartTemplates(chartFolder, 0), chartFolder, budget);
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
        catch (PartlyReadException ex)
        {
            notes = [$"{chart} wasn't read in full: {Printable(ex.Message)}. So no Helm value is marked dead, as the part not read may read any of them."];
            return [];
        }
        catch (RegexMatchTimeoutException)
        {
            notes = [$"Reading {chart} for the values its templates read took too long, so no Helm value is marked dead."];
            return [];
        }
    }

    // Thrown when part of the chart can't be read, so the scan can't know what the templates read.
    private sealed class PartlyReadException(string reason) : Exception(reason);

    // What the scan has read so far, to keep the whole scan within MaxFiles files and MaxScanBytes bytes.
    private sealed class Budget
    {
        public int Files { get; set; }

        public long Bytes { get; set; }
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

        var all = values.ToList();
        var skip = new HashSet<string>(subcharts, StringComparer.Ordinal) { "global" };

        // The values above a reference: those whose path is the text before one of its dots. The text is looked up as a
        // span, so no string is built for it, and only up to the length of the longest value.
        var longest = all.Count == 0 ? 0 : all.Max(v => v.Path.Length);
        var paths = all.Select(v => v.Path).ToHashSet(StringComparer.Ordinal).GetAlternateLookup<ReadOnlySpan<char>>();
        var above = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            for (var dot = reference.IndexOf('.'); dot >= 0 && dot <= longest; dot = reference.IndexOf('.', dot + 1))
            {
                if (paths.TryGetValue(reference.AsSpan(0, dot), out var path))
                {
                    above.Add(path);
                }
            }
        }

        var read = references.GetAlternateLookup<ReadOnlySpan<char>>();
        var seen = new HashSet<(string Path, string File)>();
        var dead = new List<DeadHelmValue>();
        foreach (var (path, file) in all)
        {
            if (!skip.Contains(path.Split('.')[0]) && !IsRead(path, read, above) && seen.Add((path, file)))
            {
                dead.Add(new DeadHelmValue(path, file));
            }
        }

        return dead;
    }

    // Whether a reference is the path or a path above it, or the path is above a reference.
    private static bool IsRead(string path, HashSet<string>.AlternateLookup<ReadOnlySpan<char>> references, HashSet<string> above)
    {
        if (references.Contains(path) || above.Contains(path))
        {
            return true;
        }

        for (var dot = path.IndexOf('.'); dot >= 0; dot = path.IndexOf('.', dot + 1))
        {
            if (references.Contains(path.AsSpan(0, dot)))
            {
                return true;
            }
        }

        return false;
    }

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
    // v1). Null when charts/ is a link or can't be listed, a dependency list isn't YAML, or a pattern timed out. Chart.yaml
    // and requirements.yaml are read with Read, so they count toward the budget and throw PartlyReadException as it does.
    private static List<string>? Subcharts(string chartFolder, Budget budget)
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
                    if (DependencyNames(Read(chartFile, chartFolder, budget)) is not { } dependencies)
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

    // Whether the folder is a link. When that can't be told, it is taken for one.
    private static bool IsLink(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

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
    // MaxChartDepth deep. Only those folders are walked; a link among them is given back for Read to refuse.
    private static IEnumerable<string> SubchartTemplates(string chartFolder, int depth)
    {
        var charts = Path.Combine(chartFolder, "charts");
        if (!Directory.Exists(charts))
        {
            return [];
        }

        if (IsLink(charts))
        {
            return [charts];
        }

        if (depth >= MaxChartDepth)
        {
            throw new PartlyReadException($"its charts are nested more than {MaxChartDepth} deep");
        }

        return Directory.EnumerateDirectories(charts, "*", Entries).SelectMany(chart =>
        {
            if (IsLink(chart))
            {
                return [chart];
            }

            var templates = Path.Combine(chart, "templates");
            IEnumerable<string> own = !Directory.Exists(templates) ? [] : IsLink(templates) ? [templates] : Files(templates);
            return own.Concat(SubchartTemplates(chart, depth + 1));
        });
    }

    // The chart's files outside templates/, charts/ and .git, which a template can read with .Files.
    private static FileSystemEnumerable<string> OtherChartFiles(string chartFolder) =>
        Files(chartFolder, name => name is "templates" or "charts" or ".git");

    // The files under a folder, and the links in it, which Read refuses: links are not followed. A folder directly in it
    // that skip names is left out.
    private static FileSystemEnumerable<string> Files(string folder, Func<string, bool>? skip = null) =>
        new FileSystemEnumerable<string>(folder, (ref FileSystemEntry entry) => entry.ToFullPath(), new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory || (IsLinkEntry(ref entry) && !Skipped(ref entry, skip)),
            ShouldRecursePredicate = (ref FileSystemEntry entry) => !IsLinkEntry(ref entry) && !Skipped(ref entry, skip),
        };

    // A link has a reparse point, but so may a folder that isn't one, such as a cloud-file folder on Windows: only an
    // entry with a link target is a link. When the target can't be read, the entry is taken for a link.
    private static bool IsLinkEntry(ref FileSystemEntry entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return false;
        }

        try
        {
            return entry.ToFileSystemInfo().LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool Skipped(ref FileSystemEntry entry, Func<string, bool>? skip) =>
        skip is not null && entry.Directory.SequenceEqual(entry.RootDirectory) && skip(entry.FileName.ToString());

    // The texts of the files. Throws PartlyReadException when a folder can't be listed or Read refuses a file.
    private static List<string> ReadAll(IEnumerable<string> files, string chartFolder, Budget budget)
    {
        var texts = new List<string>();
        try
        {
            foreach (var file in files)
            {
                texts.Add(Read(file, chartFolder, budget));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PartlyReadException("a folder of it can't be listed");
        }

        return texts;
    }

    // The text of a file of the chart. Throws PartlyReadException when it is a link, is bigger than MaxFileBytes or
    // can't be read, or when the scan would read more than MaxFiles files or MaxScanBytes bytes.
    private static string Read(string file, string chartFolder, Budget budget)
    {
        if (++budget.Files > MaxFiles)
        {
            throw new PartlyReadException(string.Create(CultureInfo.InvariantCulture, $"it holds more than {MaxFiles:N0} files to read"));
        }

        try
        {
            var info = new FileInfo(file);
            if (info.LinkTarget is not null)
            {
                throw new PartlyReadException($"{Display(chartFolder, file)} is a link");
            }

            budget.Bytes += info.Length;
            if (budget.Bytes > MaxScanBytes)
            {
                throw new PartlyReadException($"it holds more than {MaxScanBytes / 1024 / 1024} MB to read");
            }

            return ReadSmall(file) ?? throw new PartlyReadException($"{Display(chartFolder, file)} is bigger than 2 MB");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PartlyReadException($"{Display(chartFolder, file)} can't be read");
        }
    }

    // The text of a file, or null when it is a link or holds more than MaxFileBytes. At most MaxFileBytes + 1 bytes are
    // read whatever length the file gives: an empty one is not opened, as a device or a pipe says 0 and may never end.
    // The buffer fits the length the file gives and grows only when the file holds more.
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

        var buffer = ArrayPool<byte>.Shared.Rent((int)info.Length + 1);
        try
        {
            var count = 0;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1))
            {
                while (true)
                {
                    var read = stream.Read(buffer.AsSpan(count, Math.Min(buffer.Length, MaxFileBytes + 1) - count));
                    if (read == 0)
                    {
                        break;
                    }

                    count += read;
                    if (count > MaxFileBytes)
                    {
                        return null;
                    }

                    if (count == buffer.Length)
                    {
                        var bigger = ArrayPool<byte>.Shared.Rent((int)Math.Min(2L * buffer.Length, MaxFileBytes + 1));
                        buffer.AsSpan(0, count).CopyTo(bigger);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = bigger;
                    }
                }
            }

            using var reader = new StreamReader(new MemoryStream(buffer, 0, count), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Only the system's own separator is replaced: on Linux and macOS a "\" is part of a file name.
    private static string Display(string chartFolder, string file)
    {
        var relative = Path.GetRelativePath(chartFolder, file);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? Path.GetFileName(file) : relative.Replace(Path.DirectorySeparatorChar, '/');
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
