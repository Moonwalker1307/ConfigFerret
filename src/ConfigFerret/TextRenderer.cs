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
                sb.Append("  - ").AppendLine(Escape(warning));
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
        sb.Append(Escape(setting.Key)).Append(" = ").AppendLine(ShowValue(setting));
        sb.Append("  ").AppendLine(Escape(setting.Story));
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
        sb.Append(Escape(step.Source));
        if (step.Location is not null)
        {
            sb.Append(" (").Append(Escape(step.Location)).Append(')');
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
            sb.Append(' ', (depth * 2) + (depth == 1 ? 13 : 16)).Append(Escape(step.Note)).AppendLine();
        }

        foreach (var origin in step.Origins)
        {
            WriteStep(sb, origin, depth + 1);
        }
    }

    // A container trace lists what the manifests can't tell, so say why the value is missing rather than "(null)".
    private static string ShowValue(TracedSetting setting)
    {
        if (setting.Value is not null || setting.Steps.Count == 0)
        {
            return Show(setting.Value);
        }

        return setting.Steps[^1].Kind switch
        {
            LayerKind.KubernetesField => "(set at run time)",
            LayerKind.KubernetesConfigMap or LayerKind.KubernetesSecret or LayerKind.KubernetesWorkload => "(unknown)",
            _ => Show(setting.Value),
        };
    }

    internal static string Show(string? value)
    {
        if (value is null)
        {
            return "(null)";
        }

        // A Windows line ending reads as one line break; any other carriage return is escaped like the rest.
        var oneLine = Escape(value.Replace("\r\n", "\n", StringComparison.Ordinal));
        return oneLine.Length > 120 ? $"\"{oneLine[..117]}...\"" : $"\"{oneLine}\"";
    }

    /// <summary>
    /// Makes text from the input safe to print on one line: control characters (including ESC, line breaks, tab and
    /// DEL), the Unicode line and paragraph separators and the bidirectional formatting controls are written as
    /// <c>\n</c>, <c>\r</c>, <c>\t</c> or <c>\uXXXX</c>. Every other character, quotes and backslashes included, is kept.
    /// </summary>
    internal static string Escape(string text)
    {
        var first = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (NeedsEscape(text[i]))
            {
                first = i;
                break;
            }
        }

        if (first < 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length + 16);
        sb.Append(text, 0, first);
        for (var i = first; i < text.Length; i++)
        {
            var c = text[i];
            if (!NeedsEscape(c))
            {
                sb.Append(c);
                continue;
            }

            switch (c)
            {
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    sb.Append("\\u").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    break;
            }
        }

        return sb.ToString();
    }

    private static bool NeedsEscape(char c) =>
        char.IsControl(c) || c is '\u2028' or '\u2029' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');
}
