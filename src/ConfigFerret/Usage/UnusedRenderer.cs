using System.Text;
using System.Text.Json;

namespace ConfigFerret;

/// <summary>Renders an <see cref="UnusedReport"/> as text or JSON.</summary>
internal static class UnusedRenderer
{
    internal const string Caution =
        "Nothing here is proven safe to delete. A value that never wins here can win in another environment, "
        + "and a key the app did not read may be read on a code path that did not run.";

    public static string RenderText(UnusedReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Possibly unused configuration");
        sb.AppendLine(Caution);
        sb.AppendLine();

        sb.Append("Values that never win (").Append(report.NeverWinning.Count).AppendLine(")");
        string? lastKey = null;
        foreach (var item in report.NeverWinning)
        {
            if (!string.Equals(item.Key, lastKey, StringComparison.Ordinal))
            {
                sb.Append("  ").AppendLine(item.Key);
                lastKey = item.Key;
            }

            sb.Append("    ").AppendLine(Describe(item.Step));
            sb.Append("      ").Append(item.WonBy.IsRemoval ? "removed by " : "overridden by ").AppendLine(Where(item.WonBy));
            if (item.Upstream.Count > 0)
            {
                sb.Append("      upstream of ").AppendLine(string.Join(" <- ", item.Upstream.Select(s => s.Source)));
            }

            if (item.SameValueAsWinner)
            {
                sb.AppendLine("      sets the same value as the layer that wins, so it changes nothing here");
            }
        }

        sb.AppendLine();
        if (report.NotRead is null)
        {
            sb.AppendLine("Keys the app did not read: not checked. Record reads with RecordReads() to find them.");
            return sb.ToString();
        }

        sb.Append("Keys the app did not read (").Append(report.NotRead.Count)
            .Append(", from ").Append(report.RecordedReads).AppendLine(" recorded reads)");
        foreach (var setting in report.NotRead)
        {
            foreach (var line in TextRenderer.Render(setting).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                sb.Append("  ").AppendLine(line.TrimEnd('\r'));
            }
        }

        if (report.SkippedEnvironmentKeys > 0)
        {
            sb.Append("  (").Append(report.SkippedEnvironmentKeys)
                .AppendLine(" plain process environment variables such as PATH are left out)");
        }

        return sb.ToString();
    }

    private static string Describe(ProvenanceStep step)
    {
        var text = Where(step);
        if (step.IsRemoval)
        {
            return text + " (removes the key)";
        }

        return step.Value is null ? text : text + " = " + TextRenderer.Show(step.Value);
    }

    private static string Where(ProvenanceStep step) =>
        step.Location is null ? step.Source : $"{step.Source} ({step.Location})";

    public static string RenderJson(UnusedReport report)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonRenderer.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("caution", Caution);
            writer.WriteStartArray("neverWinning");
            foreach (var item in report.NeverWinning)
            {
                writer.WriteStartObject();
                writer.WriteString("key", item.Key);
                WriteLayer(writer, item.Step);
                writer.WriteStartObject(item.WonBy.IsRemoval ? "removedBy" : "overriddenBy");
                WriteLayer(writer, item.WonBy);
                writer.WriteEndObject();
                if (item.Upstream.Count > 0)
                {
                    writer.WriteStartArray("upstreamOf");
                    foreach (var step in item.Upstream)
                    {
                        writer.WriteStringValue(step.Source);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteBoolean("sameValueAsWinner", item.SameValueAsWinner);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (report.NotRead is null)
            {
                writer.WriteNull("notRead");
            }
            else
            {
                writer.WriteNumber("recordedReads", report.RecordedReads ?? 0);
                writer.WriteNumber("skippedEnvironmentKeys", report.SkippedEnvironmentKeys);
                writer.WriteStartArray("notRead");
                foreach (var setting in report.NotRead)
                {
                    writer.WriteStartObject();
                    writer.WriteString("key", setting.Key);
                    writer.WriteString("value", setting.Value);
                    writer.WriteBoolean("sensitive", setting.IsSensitive);
                    writer.WriteString("story", setting.Story);
                    JsonRenderer.WriteSteps(writer, "layers", setting.Steps);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteLayer(Utf8JsonWriter writer, ProvenanceStep step)
    {
        writer.WriteString("kind", step.Kind.ToString());
        writer.WriteString("source", step.Source);
        if (step.Location is not null)
        {
            writer.WriteString("location", step.Location);
        }

        if (step.IsRemoval)
        {
            writer.WriteBoolean("removal", true);
        }
        else
        {
            writer.WriteString("value", step.Value);
        }

        if (step.IsSensitive)
        {
            writer.WriteBoolean("sensitive", true);
        }
    }
}
