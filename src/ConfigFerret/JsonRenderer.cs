using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ConfigFerret;

/// <summary>Renders a report as JSON.</summary>
public static class JsonRenderer
{
    internal static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        // The report is a file for people and tools, not text embedded in HTML, so keep ' + < > & and
        // non-ASCII text readable instead of writing \u0027 and the like. Quotes and control characters are still escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Renders <paramref name="report"/> as indented JSON.</summary>
    public static string Render(ProvenanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("settings");
            foreach (var setting in report.Settings)
            {
                writer.WriteStartObject();
                writer.WriteString("key", setting.Key);
                writer.WriteString("value", setting.Value);
                writer.WriteBoolean("sensitive", setting.IsSensitive);
                writer.WriteString("story", setting.Story);
                WriteSteps(writer, "layers", setting.Steps);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("warnings");
            foreach (var warning in report.Warnings)
            {
                writer.WriteStringValue(warning);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static void WriteSteps(Utf8JsonWriter writer, string name, IReadOnlyList<ProvenanceStep> steps)
    {
        writer.WriteStartArray(name);
        foreach (var step in steps)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", step.Kind.ToString());
            writer.WriteString("source", step.Source);
            if (step.Location is not null)
            {
                writer.WriteString("location", step.Location);
            }

            writer.WriteString("value", step.Value);
            writer.WriteBoolean("effective", step.IsEffective);
            if (step.IsRemoval)
            {
                writer.WriteBoolean("removal", true);
            }

            if (step.IsSensitive)
            {
                writer.WriteBoolean("sensitive", true);
            }

            if (step.IsInferred)
            {
                writer.WriteBoolean("inferred", true);
            }

            if (step.Note is not null)
            {
                writer.WriteString("note", step.Note);
            }

            if (step.Origins.Count > 0)
            {
                WriteSteps(writer, "origins", step.Origins);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
