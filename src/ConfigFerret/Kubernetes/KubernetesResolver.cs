namespace ConfigFerret.Kubernetes;

/// <summary>Links environment variables and mounted files to the workload spec, ConfigMaps and Secrets.</summary>
internal sealed class KubernetesResolver(KubernetesManifests manifests, string? workloadName, string? containerName) : IOriginResolver
{
    private (Workload Workload, Container Container)? _target;
    private bool _selected;

    public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        if (layer.Kind != LayerKind.DotNetProvider)
        {
            return [];
        }

        var target = Select(diagnostics);
        if (target is null)
        {
            return [];
        }

        var (workload, container) = target.Value;
        return layer.Reference switch
        {
            EnvironmentVariableRef env => ResolveEnv(workload, container, env.Name),
            FileRef file => ResolveFile(workload, container, file.Path),
            _ => [],
        };
    }

    private (Workload, Container)? Select(TraceDiagnostics diagnostics)
    {
        if (_selected)
        {
            return _target;
        }

        _selected = true;
        var candidates = manifests.Workloads
            .Where(w => workloadName is null || string.Equals(w.Name, workloadName, StringComparison.Ordinal))
            .ToList();
        if (candidates.Count == 0)
        {
            diagnostics.Warn(workloadName is null
                ? "No Kubernetes workload found in the manifests."
                : $"No Kubernetes workload named '{workloadName}' found in the manifests.");
            return null;
        }

        if (candidates.Count > 1)
        {
            diagnostics.Warn($"The manifests hold {candidates.Count} workloads; using {candidates[0].DisplayName}. Pass a workload name to pick another.");
        }

        var workload = candidates[0];
        var containers = workload.Containers
            .Where(c => containerName is null || string.Equals(c.Name, containerName, StringComparison.Ordinal))
            .ToList();
        if (containers.Count == 0)
        {
            diagnostics.Warn($"No container named '{containerName}' in {workload.DisplayName}.");
            return null;
        }

        if (containers.Count > 1 && containerName is null)
        {
            diagnostics.Warn($"{workload.DisplayName} has {containers.Count} containers; using '{containers[0].Name}'. Pass a container name to pick another.");
        }

        _target = (workload, containers[0]);
        return _target;
    }

    private List<ProvenanceStep> ResolveEnv(Workload workload, Container container, string name)
    {
        var where = $"{workload.DisplayName}, container {container.Name}";
        var chain = new List<ProvenanceStep>();
        var unloaded = new List<EnvFromEntry>();

        // envFrom is applied first, in order; env entries override it, and a later env entry overrides an earlier one.
        foreach (var from in container.EnvFrom)
        {
            if (!name.StartsWith(from.Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var key = name[from.Prefix.Length..];
            var store = from.IsSecret ? manifests.Secrets : manifests.ConfigMaps;
            if (!store.TryGetValue(from.Name, out var data))
            {
                unloaded.Add(from);
                continue;
            }

            if (data.Data.TryGetValue(key, out var value))
            {
                chain.Add(DataStep(from.IsSecret, data, key, value, $"envFrom on {where}" + (from.Prefix.Length > 0 ? $" with prefix '{from.Prefix}'" : "")));
            }
        }

        foreach (var entry in container.Env.Where(e => string.Equals(e.Name, name, StringComparison.Ordinal)))
        {
            chain.Add(entry.Source switch
            {
                EnvValueSource.Literal literal => new ProvenanceStep
                {
                    Kind = LayerKind.KubernetesWorkload,
                    Source = $"{workload.DisplayName} env {name}",
                    Location = $"{workload.SourceName}:{entry.Line}",
                    Value = literal.Value,
                    Note = literal.Value.Contains("$(", StringComparison.Ordinal)
                        ? $"container {container.Name}; Kubernetes expands $(VAR) references in this value"
                        : $"container {container.Name}",
                    Reference = new WorkloadEnvRef(workload.Name, container.Name, name),
                },
                EnvValueSource.FromConfigMap cm => KeyRefStep(isSecret: false, cm.Name, cm.Key, $"configMapKeyRef on {where}"),
                EnvValueSource.FromSecret s => KeyRefStep(isSecret: true, s.Name, s.Key, $"secretKeyRef on {where}"),
                EnvValueSource.FromField f => new ProvenanceStep
                {
                    Kind = LayerKind.KubernetesField,
                    Source = $"downward API {f.FieldPath}",
                    Location = $"{workload.SourceName}:{entry.Line}",
                    Note = $"fieldRef on {where}",
                },
                _ => throw new InvalidOperationException("Unknown env value source."),
            });
        }

        if (chain.Count == 0)
        {
            foreach (var from in unloaded)
            {
                chain.Add(new ProvenanceStep
                {
                    Kind = from.IsSecret ? LayerKind.KubernetesSecret : LayerKind.KubernetesConfigMap,
                    Source = $"{(from.IsSecret ? "Secret" : "ConfigMap")} {from.Name}",
                    Location = $"key {name[from.Prefix.Length..]}",
                    IsInferred = true,
                    IsSensitive = from.IsSecret,
                    Note = $"envFrom on {where}; its contents were not loaded, so this is a guess",
                    Reference = from.IsSecret
                        ? new SecretKeyRef(from.Name, name[from.Prefix.Length..])
                        : new ConfigMapKeyRef(from.Name, name[from.Prefix.Length..]),
                });
            }
        }

        return chain;
    }

    private ProvenanceStep KeyRefStep(bool isSecret, string sourceName, string key, string note)
    {
        var store = isSecret ? manifests.Secrets : manifests.ConfigMaps;
        if (store.TryGetValue(sourceName, out var data) && data.Data.TryGetValue(key, out var value))
        {
            return DataStep(isSecret, data, key, value, note);
        }

        return new ProvenanceStep
        {
            Kind = isSecret ? LayerKind.KubernetesSecret : LayerKind.KubernetesConfigMap,
            Source = $"{(isSecret ? "Secret" : "ConfigMap")} {sourceName}",
            Location = $"key {key}",
            IsSensitive = isSecret,
            Note = note + (store.ContainsKey(sourceName) ? "; the key is missing from it" : "; its contents were not loaded"),
            Reference = isSecret ? new SecretKeyRef(sourceName, key) : new ConfigMapKeyRef(sourceName, key),
        };
    }

    private static ProvenanceStep DataStep(bool isSecret, KubernetesData data, string key, KubernetesValue value, string note) => new()
    {
        Kind = isSecret ? LayerKind.KubernetesSecret : LayerKind.KubernetesConfigMap,
        Source = $"{(isSecret ? "Secret" : "ConfigMap")} {data.Name}",
        Location = value.Line > 0 ? $"key {key}, {data.SourceName}:{value.Line}" : $"key {key}",
        Value = value.Value,
        IsSensitive = isSecret,
        Note = note,
        Reference = isSecret ? new SecretKeyRef(data.Name, key) : new ConfigMapKeyRef(data.Name, key),
    };

    private List<ProvenanceStep> ResolveFile(Workload workload, Container container, string path)
    {
        var normalized = path.Replace('\\', '/');
        foreach (var mount in container.VolumeMounts.OrderByDescending(m => m.MountPath.Length))
        {
            var mountPath = mount.MountPath.TrimEnd('/');
            string relative;
            if (mount.SubPath is { Length: > 0 } subPath)
            {
                if (!string.Equals(normalized, mountPath, StringComparison.Ordinal))
                {
                    continue;
                }

                relative = subPath;
            }
            else if (normalized.StartsWith(mountPath + "/", StringComparison.Ordinal))
            {
                relative = normalized[(mountPath.Length + 1)..];
            }
            else
            {
                continue;
            }

            var volume = workload.Volumes.FirstOrDefault(v => string.Equals(v.Name, mount.Name, StringComparison.Ordinal));
            if (volume is null)
            {
                continue;
            }

            foreach (var projection in volume.Projections)
            {
                var key = projection.Items.Count == 0
                    ? (relative.Contains('/', StringComparison.Ordinal) ? null : relative)
                    : projection.Items.FirstOrDefault(i => string.Equals(i.Value, relative, StringComparison.Ordinal)).Key;
                if (key is null)
                {
                    continue;
                }

                var store = projection.IsSecret ? manifests.Secrets : manifests.ConfigMaps;
                if (projection.Items.Count == 0 && store.TryGetValue(projection.SourceName, out var known) && !known.Data.ContainsKey(key)
                    && volume.Projections.Count > 1)
                {
                    continue;
                }

                var kind = projection.IsSecret ? "Secret" : "ConfigMap";
                return
                [
                    new ProvenanceStep
                    {
                        Kind = projection.IsSecret ? LayerKind.KubernetesSecret : LayerKind.KubernetesConfigMap,
                        Source = $"{kind} {projection.SourceName}",
                        Location = $"key {key}",
                        IsSensitive = projection.IsSecret,
                        Note = $"mounted at {mount.MountPath} by volume {volume.Name} on {workload.DisplayName}, container {container.Name}",
                        Reference = projection.IsSecret ? new SecretKeyRef(projection.SourceName, key) : new ConfigMapKeyRef(projection.SourceName, key),
                    },
                ];
            }
        }

        return [];
    }
}
