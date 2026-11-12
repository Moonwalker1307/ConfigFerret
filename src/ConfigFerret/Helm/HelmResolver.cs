using System.Globalization;

namespace ConfigFerret.Helm;

/// <summary>Links ConfigMap keys and workload env entries to Helm templates and values files.</summary>
internal sealed class HelmResolver(HelmChart chart) : IOriginResolver
{
    public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics) => layer.Reference switch
    {
        ConfigMapKeyRef cm => ResolveTemplate(cm.Key, layer.Value),
        SecretKeyRef s => ResolveTemplate(s.Key, layer.Value),
        WorkloadEnvRef env => ResolveTemplate(env.Name, layer.Value),
        HelmValueRef v => ResolveValues(v.Path),
        _ => [],
    };

    private List<ProvenanceStep> ResolveTemplate(string name, string? renderedValue)
    {
        var uses = chart.FindUses(name);
        if (uses.Count == 0)
        {
            return [];
        }

        // Prefer the template line whose values path holds the value we actually see.
        var use = uses.FirstOrDefault(u => renderedValue is not null && string.Equals(chart.GetValue(u.ValuesPath), renderedValue, StringComparison.Ordinal))
            ?? uses[0];
        var helmValue = chart.GetValue(use.ValuesPath);
        var note = $"renders .Values.{use.ValuesPath}";
        if (renderedValue is not null && helmValue is not null && !string.Equals(helmValue, renderedValue, StringComparison.Ordinal))
        {
            note += $"; the values give '{helmValue}' but the manifest has '{renderedValue}', so the release may be out of date or the template transforms it";
        }

        return
        [
            new ProvenanceStep
            {
                Kind = LayerKind.Helm,
                Source = $"Helm template {use.Template}",
                Location = string.Create(CultureInfo.InvariantCulture, $"line {use.Line}"),
                Value = helmValue,
                IsInferred = true,
                Note = note,
                Reference = new HelmValueRef(use.ValuesPath),
            },
        ];
    }

    private List<ProvenanceStep> ResolveValues(string path)
    {
        if (!chart.History.TryGetValue(path, out var history) || history.Count == 0)
        {
            return
            [
                new ProvenanceStep
                {
                    Kind = LayerKind.Helm,
                    Source = "Helm template default",
                    Location = path,
                    Note = "no values file sets this path, so the template's own default (if any) is used",
                },
            ];
        }

        return history.Select(h => new ProvenanceStep
        {
            Kind = LayerKind.Helm,
            Source = h.Source,
            Location = h.Line > 0 ? string.Create(CultureInfo.InvariantCulture, $"line {h.Line}, {path}") : path,
            Value = h.Value,
            IsRemoval = h.IsRemoval,
        }).ToList();
    }
}
