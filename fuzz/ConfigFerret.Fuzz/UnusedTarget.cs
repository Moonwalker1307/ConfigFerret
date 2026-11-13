using System.Text;
using ConfigFerret.Kubernetes;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Fuzz;

/// <summary>
/// Fuzz target for FindUnused and read recording. The input is
/// <c>appsettings.json %% appsettings.Production.json %% KEY=VALUE lines %% Kubernetes manifests %% keys read, one per line</c>.
/// The JSON files and lines are traced as layers of one configuration, the manifests with ConfigTracer.ForContainer,
/// and each report is checked against what FindUnused promises.
/// </summary>
internal static partial class Targets
{
    private static void Unused(ReadOnlySpan<byte> data)
    {
        var parts = Split(data);
        var reads = (parts.ElementAtOrDefault(4) ?? string.Empty).Split('\n').Take(200).ToList();

        var builder = new ConfigurationBuilder();
        using var appsettings = JsonStream(parts.ElementAtOrDefault(0));
        using var production = JsonStream(parts.ElementAtOrDefault(1));
        foreach (var stream in new[] { appsettings, production }.OfType<MemoryStream>())
        {
            builder.AddJsonStream(stream);
        }
        var memory = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (parts.ElementAtOrDefault(2) ?? string.Empty).Split('\n').Take(200))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                memory[line[..eq]] = line[(eq + 1)..];
            }
        }

        builder.AddInMemoryCollection(memory);
        var recorder = builder.RecordReads();

        IConfigurationRoot root;
        try
        {
            root = builder.Build();
        }
        catch (FormatException)
        {
            return;
        }
        catch (InvalidDataException)
        {
            return;
        }
        catch (System.Text.Json.JsonException)
        {
            // The JSON stream provider lets this through for input that is not JSON at all.
            return;
        }
        catch (InvalidOperationException)
        {
            // The JSON provider throws this for an escaped lone surrogate in a property name.
            return;
        }

        // Tracing must not count as reading.
        var report = ConfigTracer.For(root).WithOptions(o => o.Environment = new Dictionary<string, string?>()).Trace();
        if (recorder.ReadKeys.Count != 0)
        {
            throw new InvariantException($"Tracing recorded {recorder.ReadKeys.Count} reads.");
        }

        foreach (var key in reads.Where(k => k.Length > 0))
        {
            _ = root[key];
            if (!recorder.WasRead(key))
            {
                throw new InvariantException($"Read '{key}' but WasRead says it was not.");
            }
        }

        CheckUnused(report, report.FindUnused(recorder), recorder.ReadKeys);
        CheckUnused(report, report.FindUnused(reads), reads);
        CheckUnused(report, report.FindUnused(), null);

        KubernetesManifests manifests;
        try
        {
            manifests = KubernetesManifests.Parse(parts.ElementAtOrDefault(3) ?? string.Empty, "fuzz.yaml");
        }
        catch (FormatException)
        {
            return;
        }

        var container = ConfigTracer.ForContainer(manifests).Trace();
        CheckUnused(container, container.FindUnused(reads), reads);
    }

    private static MemoryStream? JsonStream(string? json) => json is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(json));

    private static void CheckUnused(ProvenanceReport report, UnusedReport unused, IReadOnlyCollection<string>? reads)
    {
        var redacted = new TraceOptions().RedactedText;
        foreach (var item in unused.NeverWinning)
        {
            var setting = report.Find(item.Key) ?? throw new InvariantException($"NeverWinning lists '{item.Key}', which the report does not have.");
            if (item.Step.IsEffective || item.Step.IsRemoval)
            {
                throw new InvariantException($"NeverWinning lists a winning or removing step for '{item.Key}'.");
            }

            if (!item.WonBy.IsEffective && !item.WonBy.IsRemoval)
            {
                throw new InvariantException($"'{item.Key}' is said to be beaten by a step that neither wins nor removes it.");
            }

            if (ReferenceEquals(item.Step, item.WonBy))
            {
                throw new InvariantException($"A step for '{item.Key}' is said to beat itself.");
            }

            if (item.SameValueAsWinner && (item.Step.IsSensitive || item.WonBy.IsSensitive || !string.Equals(item.Step.Value, item.WonBy.Value, StringComparison.Ordinal)))
            {
                throw new InvariantException($"'{item.Key}' is said to set the winner's value, but the values differ or are sensitive.");
            }

            if (!Contains(setting.Steps, item.Step))
            {
                throw new InvariantException($"The never-winning step for '{item.Key}' is not in its history.");
            }

            // A losing step is redacted when it is sensitive itself. A plain value that a Secret overrides is shown, as
            // it is in the trace itself, because the manifest or file that sets it holds it in plain text anyway.
            if (item.Step.IsSensitive && item.Step.Value is not null && item.Step.Value != redacted)
            {
                throw new InvariantException($"A sensitive never-winning value for '{item.Key}' is not redacted.");
            }

            if (setting.IsSensitive && item.WonBy.Value is not null && item.WonBy.Value != redacted)
            {
                throw new InvariantException($"'{item.Key}' is sensitive but the winning value is not redacted.");
            }
        }

        CheckReportOrder(report, unused.NeverWinning.Select(i => i.Key), "NeverWinning");

        if (reads is null)
        {
            if (unused.NotRead is not null || unused.RecordedReads is not null)
            {
                throw new InvariantException("Without reads, NotRead and RecordedReads must be null.");
            }
        }
        else
        {
            var normalized = new HashSet<string>(reads.Select(r => r.Replace("__", ":", StringComparison.Ordinal)), StringComparer.OrdinalIgnoreCase);
            var notRead = unused.NotRead ?? throw new InvariantException("With reads, NotRead must not be null.");
            if (unused.RecordedReads != normalized.Count)
            {
                throw new InvariantException($"RecordedReads is {unused.RecordedReads}, expected {normalized.Count}.");
            }

            foreach (var setting in notRead)
            {
                if (normalized.Contains(setting.Key))
                {
                    throw new InvariantException($"'{setting.Key}' was read but is listed as not read.");
                }
            }

            var unread = report.Settings.Count(s => !normalized.Contains(s.Key));
            if (notRead.Count + unused.SkippedEnvironmentKeys != unread)
            {
                throw new InvariantException($"{notRead.Count} not read plus {unused.SkippedEnvironmentKeys} skipped is not the {unread} unread keys.");
            }

            CheckReportOrder(report, notRead.Select(s => s.Key), "NotRead");
        }

        _ = unused.ToText();
        _ = unused.ToJson();
    }

    private static bool Contains(IReadOnlyList<ProvenanceStep> steps, ProvenanceStep wanted) =>
        steps.Any(s => ReferenceEquals(s, wanted) || Contains(s.Origins, wanted));

    // Both lists follow the order of the report's settings: sorted by key for a configuration, and environment
    // variables before files for a container.
    private static void CheckReportOrder(ProvenanceReport report, IEnumerable<string> keys, string list)
    {
        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < report.Settings.Count; i++)
        {
            position.TryAdd(report.Settings[i].Key, i);
        }

        var last = -1;
        foreach (var key in keys)
        {
            var at = position.GetValueOrDefault(key, -1);
            if (at < last)
            {
                throw new InvariantException($"{list} lists '{key}' out of the report's order.");
            }

            last = at;
        }
    }
}
