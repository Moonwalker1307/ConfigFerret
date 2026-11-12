using System.Text;

namespace ConfigFerret.Internal;

/// <summary>
/// Writes a one-sentence history such as "set in values.yaml, overridden by values-prod.yaml, then by
/// environment variable DB__Timeout from ConfigMap api-config".
/// </summary>
internal static class StoryWriter
{
    public static string Write(TracedSetting setting)
    {
        if (setting.Steps.Count == 0)
        {
            return "not set";
        }

        var sb = new StringBuilder();
        for (var i = 0; i < setting.Steps.Count; i++)
        {
            var step = setting.Steps[i];
            sb.Append(i switch
            {
                0 => "set in ",
                1 => ", overridden by ",
                _ => ", overridden again by ",
            });
            sb.Append(step.Source);
            AppendUpstream(sb, step, 0);
        }

        return sb.ToString();
    }

    private static void AppendUpstream(StringBuilder sb, ProvenanceStep step, int depth)
    {
        if (depth > 6)
        {
            return;
        }

        // Follow only the winning branch upstream.
        var effective = step.Origins.LastOrDefault(o => o.IsEffective);
        if (effective is null)
        {
            return;
        }

        if (effective.Reference is HelmValueRef helm)
        {
            sb.Append(", rendered by ").Append(effective.Source).Append(" from .Values.").Append(helm.Path);
        }
        else if (effective.Kind is LayerKind.Helm or LayerKind.Kustomize)
        {
            sb.Append(", set in ").Append(effective.Source);
            var overridden = step.Origins
                .Where(o => !ReferenceEquals(o, effective) && !o.IsRemoval && o.Kind == effective.Kind)
                .Select(o => o.Source)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (overridden.Count > 0)
            {
                sb.Append(" over ").Append(string.Join(" and ", overridden));
            }
        }
        else
        {
            sb.Append(" from ").Append(effective.Source);
        }

        AppendUpstream(sb, effective, depth + 1);
    }
}
