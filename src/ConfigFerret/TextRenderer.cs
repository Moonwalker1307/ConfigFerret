using System.Text;

namespace ConfigFerret;

/// <summary>Renders traced settings as indented text.</summary>
public static class TextRenderer
{
    /// <summary>Renders every setting in <paramref name="report"/>, followed by any warnings.</summary>
    public static string Render(ProvenanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        foreach (var setting in report.Settings)
        {
            Write(sb, setting);
            sb.AppendLine();
        }

        if (report.Warnings.Count > 0)
        {
            sb.AppendLine("Warnings:");
            foreach (var warning in report.Warnings)
            {
                sb.Append("  - ").AppendLine(warning);
            }
        }

        return sb.ToString();
    }

    /// <summary>Renders one setting with its story and every layer.</summary>
    public static string Render(TracedSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        var sb = new StringBuilder();
        Write(sb, setting);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, TracedSetting setting)
    {
        sb.Append(setting.Key).Append(" = ").AppendLine(Show(setting.Value));
        sb.Append("  ").AppendLine(setting.Story);
        foreach (var step in setting.Steps)
        {
            WriteStep(sb, step, 1);
        }
    }

    private static void WriteStep(StringBuilder sb, ProvenanceStep step, int depth)
    {
        sb.Append(' ', depth * 2);
        sb.Append(depth == 1 ? "" : "<- ");
        sb.Append(step.IsRemoval ? "[removed]    " : step.IsEffective ? "[effective]  " : "[overridden] ");
        sb.Append(step.Source);
        if (step.Location is not null)
        {
            sb.Append(" (").Append(step.Location).Append(')');
        }

        if (!step.IsRemoval && step.Value is not null)
        {
            sb.Append(" = ").Append(Show(step.Value));
        }

        if (step.IsInferred)
        {
            sb.Append(" [inferred]");
        }

        sb.AppendLine();
        if (step.Note is not null)
        {
            sb.Append(' ', (depth * 2) + (depth == 1 ? 13 : 16)).Append(step.Note).AppendLine();
        }

        foreach (var origin in step.Origins)
        {
            WriteStep(sb, origin, depth + 1);
        }
    }

    internal static string Show(string? value)
    {
        if (value is null)
        {
            return "(null)";
        }

        var oneLine = value.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        return oneLine.Length > 120 ? $"\"{oneLine[..117]}...\"" : $"\"{oneLine}\"";
    }
}
