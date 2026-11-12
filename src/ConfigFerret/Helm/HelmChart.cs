using System.Text.RegularExpressions;
using ConfigFerret.Internal;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ConfigFerret.Helm;

/// <summary>
/// A Helm chart's values, merged the way <c>helm install</c> merges them (chart <c>values.yaml</c>, then each
/// <c>-f</c> file in order, then <c>--set</c>), with the history of every value kept.
/// Templates are scanned to link env entries and ConfigMap keys to the values they render.
/// </summary>
public sealed partial class HelmChart
{
    private readonly Dictionary<string, List<HelmValueStep>> _history = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<HelmTemplateUse>> _uses = new(StringComparer.Ordinal);

    private HelmChart(string? chartDirectory) => ChartDirectory = chartDirectory;

    /// <summary>The chart directory, when one was given.</summary>
    public string? ChartDirectory { get; }

    /// <summary>Every values path that was set, with its history, lowest priority first.</summary>
    public IReadOnlyDictionary<string, List<HelmValueStep>> History => _history;

    /// <summary>
    /// Loads a chart directory and the values that override it.
    /// </summary>
    /// <param name="chartDirectory">The chart directory, holding <c>Chart.yaml</c>, <c>values.yaml</c> and <c>templates/</c>.</param>
    /// <param name="valuesFiles">Values files passed with <c>-f</c>/<c>--values</c>, in the same order.</param>
    /// <param name="setValues">Values passed with <c>--set</c>, for example <c>db.timeout=42</c>, in the same order.</param>
    public static HelmChart Load(string chartDirectory, IEnumerable<string>? valuesFiles = null, IEnumerable<string>? setValues = null)
    {
        ArgumentNullException.ThrowIfNull(chartDirectory);
        var chart = new HelmChart(chartDirectory);
        var defaults = Path.Combine(chartDirectory, "values.yaml");
        if (File.Exists(defaults))
        {
            chart.AddValuesFile(defaults, "values.yaml (chart default)");
        }

        foreach (var file in valuesFiles ?? [])
        {
            chart.AddValuesFile(file, Path.GetFileName(file));
        }

        foreach (var set in setValues ?? [])
        {
            chart.AddSet(set);
        }

        var templates = Path.Combine(chartDirectory, "templates");
        if (Directory.Exists(templates))
        {
            foreach (var file in Directory.EnumerateFiles(templates, "*.*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(chartDirectory, file).Replace('\\', '/');
                chart.AddTemplate(relative, File.ReadAllText(file));
            }
        }

        return chart;
    }

    /// <summary>Builds a chart from values text and template text, without touching the disk.</summary>
    /// <param name="values">Values documents in priority order, as (display name, YAML text).</param>
    /// <param name="templates">Templates as (relative path, text).</param>
    public static HelmChart FromText(IEnumerable<(string Name, string Yaml)> values, IEnumerable<(string Path, string Text)>? templates = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        var chart = new HelmChart(null);
        foreach (var (name, yaml) in values)
        {
            chart.AddValues(name, yaml);
        }

        foreach (var (path, text) in templates ?? [])
        {
            chart.AddTemplate(path, text);
        }

        return chart;
    }

    /// <summary>The effective value at <paramref name="path"/>, or null when it is unset or removed.</summary>
    public string? GetValue(string path) =>
        _history.TryGetValue(path, out var steps) && steps.Count > 0 && !steps[^1].IsRemoval ? steps[^1].Value : null;

    /// <summary>The template lines that render values into an env entry or data key named <paramref name="name"/>.</summary>
    public IReadOnlyList<HelmTemplateUse> FindUses(string name) =>
        _uses.TryGetValue(name, out var uses) ? uses : [];

    private void AddValuesFile(string path, string displayName) => AddValues(displayName, File.ReadAllText(path));

    private void AddValues(string displayName, string yaml)
    {
        List<YamlNode> docs;
        try
        {
            docs = Yaml.LoadDocuments(yaml);
        }
        catch (YamlException ex)
        {
            throw new FormatException($"Could not parse Helm values in {displayName}: {ex.Message}", ex);
        }

        foreach (var doc in docs)
        {
            Walk(doc, prefix: null, displayName);
        }
    }

    private void Walk(YamlNode node, string? prefix, string file)
    {
        foreach (var (key, value) in node.Pairs())
        {
            var path = prefix is null ? key : prefix + "." + key;
            if (value.IsNull())
            {
                Remove(path, file, value.Line());
            }
            else if (value is YamlMappingNode)
            {
                RemoveLeaf(path, file, value.Line());
                Walk(value, path, file);
            }
            else
            {
                RemoveDescendants(path, file, value.Line());
                Record(path, new HelmValueStep(file, value.Line(), Yaml.Render(value), IsRemoval: false));
            }
        }
    }

    private void AddSet(string expression)
    {
        foreach (var part in SplitSet(expression))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var path = part[..eq];
            var value = SetList(part[(eq + 1)..]);
            var source = $"--set {path}";
            if (value == "null")
            {
                Remove(path, source, 0);
            }
            else
            {
                RemoveDescendants(path, source, 0);
                Record(path, new HelmValueStep(source, 0, value, IsRemoval: false));
            }
        }
    }

    /// <summary>Shows a <c>--set</c> list such as <c>{a,b}</c> the way a values file list is shown, <c>[a, b]</c>.</summary>
    private static string SetList(string value) =>
        value.Length >= 2 && value[0] == '{' && value[^1] == '}'
            ? "[" + string.Join(", ", value[1..^1].Split(',', StringSplitOptions.TrimEntries)) + "]"
            : value;

    private static IEnumerable<string> SplitSet(string expression)
    {
        var current = new System.Text.StringBuilder();
        var braces = 0; // commas inside a {a,b} list belong to the list
        for (var i = 0; i < expression.Length; i++)
        {
            var c = expression[i];
            if (c == '\\' && i + 1 < expression.Length)
            {
                current.Append(expression[++i]);
            }
            else if (c == ',' && braces == 0)
            {
                yield return current.ToString();
                current.Clear();
            }
            else
            {
                braces += c == '{' ? 1 : c == '}' && braces > 0 ? -1 : 0;
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private void Record(string path, HelmValueStep step)
    {
        if (!_history.TryGetValue(path, out var list))
        {
            _history[path] = list = [];
        }

        list.Add(step);
    }

    private void Remove(string path, string file, int line)
    {
        RemoveLeaf(path, file, line);
        RemoveDescendants(path, file, line);
    }

    private void RemoveLeaf(string path, string file, int line)
    {
        if (_history.TryGetValue(path, out var list) && list.Count > 0 && !list[^1].IsRemoval)
        {
            list.Add(new HelmValueStep(file, line, null, IsRemoval: true));
        }
    }

    private void RemoveDescendants(string path, string file, int line)
    {
        var prefix = path + ".";
        foreach (var (key, list) in _history)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) && list.Count > 0 && !list[^1].IsRemoval)
            {
                list.Add(new HelmValueStep(file, line, null, IsRemoval: true));
            }
        }
    }

    private void AddTemplate(string relativePath, string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            foreach (Match m in ValuesReference().Matches(line))
            {
                var valuesPath = m.Groups[1].Value;
                var keyMatch = YamlKey().Match(line);
                if (!keyMatch.Success)
                {
                    continue;
                }

                var key = keyMatch.Groups[1].Value;
                string? name = null;
                if (key == "value")
                {
                    for (var back = i; back >= Math.Max(0, i - 4); back--)
                    {
                        var envName = EnvName().Match(lines[back]);
                        if (envName.Success)
                        {
                            name = envName.Groups[1].Value;
                            break;
                        }
                    }
                }
                else if (key != "name")
                {
                    name = key;
                }

                if (name is null || name.Contains("{{", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!_uses.TryGetValue(name, out var list))
                {
                    _uses[name] = list = [];
                }

                list.Add(new HelmTemplateUse(relativePath, i + 1, valuesPath));
            }
        }
    }

    [GeneratedRegex(@"\.Values\.([A-Za-z_][A-Za-z0-9_\-]*(?:\.[A-Za-z_][A-Za-z0-9_\-]*)*)", RegexOptions.CultureInvariant)]
    private static partial Regex ValuesReference();

    [GeneratedRegex(@"^\s*(?:-\s+)?[""']?([A-Za-z0-9_.\-]+)[""']?\s*:(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex YamlKey();

    [GeneratedRegex(@"^\s*-\s*name:\s*[""']?([A-Za-z0-9_.\-]+)[""']?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvName();
}

/// <summary>One values file (or <c>--set</c>) that set or removed a Helm values path.</summary>
/// <param name="Source">The values file name, or <c>--set path</c>.</param>
/// <param name="Line">The line in the file, or 0 for <c>--set</c>.</param>
/// <param name="Value">The value set, or null for a removal.</param>
/// <param name="IsRemoval">True when the source removed the value (with <c>null</c>, or by replacing its parent).</param>
public sealed record HelmValueStep(string Source, int Line, string? Value, bool IsRemoval);

/// <summary>A template line that renders a values path into an env entry or data key.</summary>
public sealed record HelmTemplateUse(string Template, int Line, string ValuesPath);
