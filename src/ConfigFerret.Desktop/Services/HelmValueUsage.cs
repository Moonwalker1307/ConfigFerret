using System.Text.RegularExpressions;
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
public static partial class HelmValueUsage
{
    private const int MaxTemplateBytes = 2 * 1024 * 1024;

    /// <summary>The values in the chart's values.yaml and <paramref name="valuesFiles"/> that no template reads.</summary>
    public static IReadOnlyList<DeadHelmValue> FindDead(string chartFolder, IEnumerable<string> valuesFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chartFolder);
        ArgumentNullException.ThrowIfNull(valuesFiles);
        var templates = Path.Combine(chartFolder, "templates");
        if (!Directory.Exists(templates))
        {
            return [];
        }

        var texts = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(templates, "*", options).Take(2000))
        {
            try
            {
                if (new FileInfo(file).Length <= MaxTemplateBytes)
                {
                    texts.Add(File.ReadAllText(file));
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
                foreach (var path in Leaves(File.ReadAllText(file)))
                {
                    values.Add((path, Display(chartFolder, file)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlException)
            {
            }
        }

        return FindDead(texts, values, Subcharts(chartFolder));
    }

    /// <summary>The values in <paramref name="values"/> that none of <paramref name="templates"/> reads.</summary>
    public static IReadOnlyList<DeadHelmValue> FindDead(IEnumerable<string> templates, IEnumerable<(string Path, string File)> values, IEnumerable<string> subcharts)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(subcharts);

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

    // Every leaf of the values document: scalars and sequences, by their dotted path.
    internal static IEnumerable<string> Leaves(string yaml)
    {
        var stream = new YamlStream();
        using (var reader = new StringReader(yaml))
        {
            stream.Load(reader);
        }

        var result = new List<string>();
        foreach (var document in stream.Documents)
        {
            if (document.RootNode is YamlMappingNode root)
            {
                Walk(root, string.Empty, result, 0);
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

            var path = prefix.Length == 0 ? name : prefix + "." + name;
            if (value is YamlMappingNode { Children.Count: > 0 } child && depth < 32)
            {
                Walk(child, path, result, depth + 1);
            }
            else
            {
                result.Add(path);
            }
        }
    }

    private static IEnumerable<string> Subcharts(string chartFolder)
    {
        var names = new List<string>();
        var charts = Path.Combine(chartFolder, "charts");
        if (Directory.Exists(charts))
        {
            names.AddRange(Directory.EnumerateFileSystemEntries(charts).Select(p => Path.GetFileName(p)).Select(n => SubchartName().Match(n).Groups["name"].Value));
        }

        var chartFile = Path.Combine(chartFolder, "Chart.yaml");
        if (File.Exists(chartFile))
        {
            try
            {
                var text = File.ReadAllText(chartFile);
                names.AddRange(DependencyName().Matches(text).Select(m => m.Groups["name"].Value));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return names.Where(n => n.Length > 0);
    }

    private static string Display(string chartFolder, string file)
    {
        var relative = Path.GetRelativePath(chartFolder, file);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? Path.GetFileName(file) : relative.Replace('\\', '/');
    }

    // .Values.a.b, $.Values.a.b, and a bare .Values.
    [GeneratedRegex(@"\.Values(?<path>(?:\.[A-Za-z_][\w-]*)*)", RegexOptions.CultureInvariant)]
    private static partial Regex ValuesReference();

    // index .Values "a" "b"
    [GeneratedRegex(@"index\s+\$?\.Values(?<prefix>(?:\.[A-Za-z_][\w-]*)*)(?:\s+""(?<key>[^""]+)"")+", RegexOptions.CultureInvariant)]
    private static partial Regex IndexReference();

    [GeneratedRegex(@"^(?<name>.+?)(?:-\d+\.\d+\.\d+.*)?(?:\.tgz)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SubchartName();

    [GeneratedRegex(@"(?m)^\s*-?\s*(?:name|alias):\s*""?(?<name>[\w-]+)""?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DependencyName();
}
