using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ConfigFerret.Desktop.Services;
using YamlDotNet.Core;

namespace ConfigFerret.Fuzz;

/// <summary>Fuzz targets for the desktop app's code and Helm usage scanners.</summary>
internal static partial class Targets
{
    private static readonly string[] FixedKeys =
    [
        "Logging:LogLevel:Default", "ConnectionStrings:Default", "Db:Host", "Db:Timeout", "Features:0", "Kestrel:Endpoints:Http:Url", "A", "", ":", "a::b",
    ];

    [GeneratedRegex("\"([^\"\\n]{1,80})\"")]
    private static partial Regex StringLiteral();

    private static void CodeReads(ReadOnlySpan<byte> data)
    {
        var text = Encoding.UTF8.GetString(data);
        var watch = Stopwatch.StartNew();
        var sources = text.Split("%%").Take(8).Select((s, i) => ($"File{i}.cs", s)).ToList();
        var scanner = CodeReadScanner.FromSources(sources);

        var keys = new List<string>(FixedKeys);
        foreach (Match m in StringLiteral().Matches(text).Take(64))
        {
            keys.Add(m.Groups[1].Value);
            keys.Add(m.Groups[1].Value.Replace('.', ':').Replace("__", ":", StringComparison.Ordinal));
        }

        keys.AddRange(scanner.Keys.Take(32));
        keys.AddRange(scanner.Sections.Take(32).Select(s => s + ":X"));
        keys.AddRange(scanner.BoundSections.Keys.Take(32).Select(s => s + ":X:Y"));
        foreach (var key in keys)
        {
            var verdict = scanner.Check(key);
            if (!verdict.IsRead && string.IsNullOrEmpty(verdict.Reason))
            {
                throw new InvariantException($"Check('{key}') says not read without a reason.");
            }
        }

        _ = scanner.DynamicReads.Count + scanner.Keys.Count + scanner.Sections.Count + scanner.BoundSections.Count;

        foreach (var source in sources.Select(s => s.Item2))
        {
            var stripped = CodeReadScanner.StripComments(source);
            if (stripped.Length > source.Length)
            {
                throw new InvariantException("StripComments made the text longer.");
            }
        }

        if (watch.Elapsed > SlowLimit)
        {
            throw new InvariantException($"CodeReadScanner took {watch.Elapsed.TotalSeconds:0.0}s on {text.Length} characters.");
        }
    }

    private static void HelmUsage(ReadOnlySpan<byte> data)
    {
        var text = Encoding.UTF8.GetString(data);
        var split = text.IndexOf("%%", StringComparison.Ordinal);
        var templates = split < 0 ? text : text[..split];
        var values = split < 0 ? string.Empty : text[(split + 2)..];
        var watch = Stopwatch.StartNew();
        List<string> leaves;
        try
        {
            leaves = HelmValueUsage.Leaves(values).ToList();
        }
        catch (YamlException)
        {
            return;
        }

        var dead = HelmValueUsage.FindDead(templates.Split("\n---\n"), leaves.Select(l => (l, "values.yaml")), []);
        foreach (var d in dead)
        {
            if (!leaves.Contains(d.Path))
            {
                throw new InvariantException($"FindDead returned '{d.Path}', which is not a leaf.");
            }
        }

        if (watch.Elapsed > SlowLimit)
        {
            throw new InvariantException($"HelmValueUsage took {watch.Elapsed.TotalSeconds:0.0}s on {text.Length} characters.");
        }
    }
}
