using System.Globalization;

namespace ConfigFerret.Kustomize;

/// <summary>Links ConfigMap and Secret keys to the kustomize generators that produced them.</summary>
internal sealed class KustomizeResolver(Kustomization kustomization) : IOriginResolver
{
    public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        var (name, key, isSecret) = layer.Reference switch
        {
            ConfigMapKeyRef cm => (cm.Name, cm.Key, false),
            SecretKeyRef s => (s.Name, s.Key, true),
            _ => (null, null, false),
        };
        if (name is null || key is null || layer.Kind is not (LayerKind.KubernetesConfigMap or LayerKind.KubernetesSecret))
        {
            return [];
        }

        var generated = kustomization.Find(name, isSecret);
        if (generated is null || !generated.Keys.TryGetValue(key, out var history))
        {
            return [];
        }

        return history.Select(h => new ProvenanceStep
        {
            Kind = LayerKind.Kustomize,
            Source = h.Kustomization,
            Location = string.Create(CultureInfo.InvariantCulture, $"line {h.Line}, {(isSecret ? "secretGenerator" : "configMapGenerator")} {generated.Name}"),
            Value = h.Value,
            IsRemoval = h.IsRemoval,
            IsSensitive = isSecret,
            Note = h.How,
        }).ToList();
    }
}
