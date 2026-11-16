using System.Globalization;

namespace ConfigFerret.Helm;

/// <summary>Links ConfigMap keys and workload env entries to Helm templates and values files.</summary>
internal sealed class HelmResolver(HelmChart chart) : IOriginResolver
{
    private readonly string[] _sensitivePaths = chart.SensitivePaths();
    private bool _warned;

    public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        if (!_warned)
        {
            _warned = true;
            foreach (var warning in chart.Warnings)
            {
                diagnostics.Warn(warning);
            }
        }

        return layer.Reference switch
        {
            ConfigMapKeyRef cm => ResolveTemplate(cm.Key, layer.Value),
            SecretKeyRef s => ResolveTemplate(s.Key, layer.Value),
            WorkloadEnvRef env => ResolveTemplate(env.Name, layer.Value),
            HelmValueRef v => ResolveValues(v.Path),
            _ => [],
        };
    }

    private List<ProvenanceStep> ResolveTemplate(string name, string? renderedValue)
    {
        var uses = chart.FindUses(name);
        if (uses.Count == 0)
        {
            return [];
        }

        // Prefer the template line whose values path holds the value we actually see.
        var matched = uses.FirstOrDefault(u => renderedValue is not null && string.Equals(chart.GetValue(u.ValuesPath), renderedValue, StringComparison.Ordinal));
        var use = matched ?? uses[0];
        var helmValue = chart.GetValue(use.ValuesPath);
        var note = $"renders .Values.{use.ValuesPath}";

        // The value may be built from every values path on its line, and without a match from any line, so it is
        // secret when any of those, or a value below one of them, comes from a Secret.
        var sensitive = (matched is null ? uses : uses.Where(u => u.Template == use.Template && u.Line == use.Line))
            .Any(u => HelmChart.HoldsPathOrBelow(_sensitivePaths, u.ValuesPath));
        if (renderedValue is not null && helmValue is not null && !string.Equals(helmValue, renderedValue, StringComparison.Ordinal))
        {
            // Notes are not redacted, and whether the key is secret by its name is only decided after the resolvers ran,
            // so neither value is ever written into one. Both are the values of this step and the one it resolves.
            note += "; the values differ from the manifest, so the release may be out of date or the template transforms it";
        }

        return
        [
            new ProvenanceStep
            {
                Kind = LayerKind.Helm,
                Source = $"Helm template {use.Template}",
                Location = string.Create(CultureInfo.InvariantCulture, $"line {use.Line}"),
                Value = helmValue,
                IsSensitive = sensitive,
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
            Kind = chart.KindOf(h.Source),
            Source = h.Source,
            Location = h.Line > 0 ? string.Create(CultureInfo.InvariantCulture, $"line {h.Line}, {path}") : path,
            Value = h.Value,
            IsRemoval = h.IsRemoval,
            IsSensitive = chart.IsSensitiveSource(h.Source),
        }).ToList();
    }
}
