using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ConfigFerret.Compose;
using ConfigFerret.Internal;

namespace ConfigFerret.Kubernetes;

/// <summary>Links environment variables and mounted files to the workload spec, ConfigMaps and Secrets.</summary>
internal sealed partial class KubernetesResolver(KubernetesManifests manifests, string? workloadName, string? containerName) : IOriginResolver
{
    // How many $(VAR) hops are shown below a step. Deeper chains still pass on their sensitivity, but not their origins.
    private const int MaxReferenceDepth = 8;

    // The most characters the expanded env values of one container may hold together. A value can repeat earlier
    // values many times, so a few lines of manifest can ask for gigabytes, and the kubelet sets no limit of its own.
    // Linux does: a process cannot start with an environment over a quarter of its stack limit (2 MiB by default) or
    // with one variable over 128 KiB, so no container that runs gets near this. It is the same limit the YAML reader
    // puts on what aliases expand to.
    private const long MaxExpandedLength = Yaml.MaxExpandedSize;

    private (Workload Workload, Container Container)? _target;
    private bool _selected;
    private (Container Container, List<ProvenanceStep> Steps)? _entrySteps;

    public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        // A Secret key that an ExternalSecret or a SecretProviderClass fills, whichever resolver found the key.
        if (layer is { Kind: LayerKind.KubernetesSecret, Reference: SecretKeyRef secretKey })
        {
            return manifests.SecretOrigins(secretKey.Name, secretKey.Key).Select(OriginStep).ToList();
        }

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

    /// <summary>
    /// The environment variables the selected container gets, in the order Kubernetes applies them: every key of each
    /// loaded <c>envFrom</c> source with its prefix, then each <c>env</c> entry. A name set more than once is listed once.
    /// </summary>
    internal IReadOnlyList<string> EnvironmentVariableNames(TraceDiagnostics diagnostics)
    {
        if (Select(diagnostics) is not (var workload, var container))
        {
            return [];
        }

        var names = new List<string>();
        foreach (var from in container.EnvFrom)
        {
            if (!manifests.TryGetData(from.IsSecret, from.Name, out var data))
            {
                diagnostics.Warn($"envFrom {(from.IsSecret ? "Secret" : "ConfigMap")} {from.Name} on {workload.DisplayName}, container {container.Name} was not loaded, so the variables it adds are not listed.");
                continue;
            }

            if (data.HasUnknownKeys)
            {
                diagnostics.Warn($"envFrom Secret {from.Name} on {workload.DisplayName}, container {container.Name} is created by {data.CreatedBy}, whose dataFrom or template adds keys that are only known once the store is read, so only its listed keys are shown.");
            }

            names.AddRange(data.Data.Keys.Order(StringComparer.Ordinal).Select(key => from.Prefix + key));
        }

        names.AddRange(container.Env.Select(e => e.Name));
        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Every layer that sets the environment variable <paramref name="name"/> on the selected container, lowest priority first.</summary>
    internal IReadOnlyList<ProvenanceStep> ResolveEnvironmentVariable(string name, TraceDiagnostics diagnostics) =>
        Select(diagnostics) is (var workload, var container) ? ResolveEnv(workload, container, name) : [];

    /// <summary>
    /// The files that ConfigMap and Secret volumes put into the selected container, as absolute paths: one per
    /// <c>items</c> entry, one per key of a loaded source without <c>items</c>, and the mount path itself for a
    /// <c>subPath</c> mount. A path mounted more than once is listed once. Other volume types are skipped.
    /// </summary>
    internal IReadOnlyList<string> MountedFilePaths(TraceDiagnostics diagnostics)
    {
        if (Select(diagnostics) is not (var workload, var container))
        {
            return [];
        }

        var paths = new List<string>();
        foreach (var mount in container.VolumeMounts)
        {
            var volume = workload.Volumes.FirstOrDefault(v => string.Equals(v.Name, mount.Name, StringComparison.Ordinal));
            if (volume is null || (volume.Projections.Count == 0 && volume.SecretProviderClass is null))
            {
                continue;
            }

            var mountPath = mount.MountPath.TrimEnd('/');
            if (mount.SubPath is { Length: > 0 })
            {
                paths.Add(mountPath);
                continue;
            }

            if (volume.SecretProviderClass is { } spcName)
            {
                if (manifests.SecretProviderClasses.TryGetValue(spcName, out var spc))
                {
                    paths.AddRange(spc.Objects.Select(o => mountPath + "/" + o.FileName.TrimStart('/')));
                }
                else
                {
                    diagnostics.Warn($"Volume {volume.Name} on {workload.DisplayName}, container {container.Name} mounts SecretProviderClass {spcName} at {mount.MountPath}, which was not loaded, so the files it adds are not listed.");
                }
            }

            foreach (var projection in volume.Projections)
            {
                IEnumerable<string> relative;
                if (projection.Items.Count > 0)
                {
                    relative = projection.Items.Select(i => i.Value);
                }
                else if (manifests.TryGetData(projection.IsSecret, projection.SourceName, out var data))
                {
                    relative = data.Data.Keys.Order(StringComparer.Ordinal);
                    if (data.HasUnknownKeys)
                    {
                        diagnostics.Warn($"Volume {volume.Name} on {workload.DisplayName}, container {container.Name} mounts Secret {projection.SourceName}, which {data.CreatedBy} fills with keys that are only known once the store is read, so only its listed keys are shown.");
                    }
                }
                else
                {
                    diagnostics.Warn($"Volume {volume.Name} on {workload.DisplayName}, container {container.Name} mounts {(projection.IsSecret ? "Secret" : "ConfigMap")} {projection.SourceName} at {mount.MountPath}, which was not loaded, so the files it adds are not listed.");
                    continue;
                }

                paths.AddRange(relative.Select(r => mountPath + "/" + r.TrimStart('/')));
            }
        }

        return paths.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The ConfigMap or Secret key behind the mounted file at <paramref name="path"/>, with the file's content as its
    /// value when the source was loaded.
    /// </summary>
    internal IReadOnlyList<ProvenanceStep> ResolveMountedFile(string path, TraceDiagnostics diagnostics)
    {
        if (Select(diagnostics) is not (var workload, var container))
        {
            return [];
        }

        var steps = ResolveFile(workload, container, path);
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Reference is ExternalSecretRef)
            {
                // A CSI file: its content is in the store, and the step already says so.
                continue;
            }

            var (isSecret, name, key) = steps[i].Reference switch
            {
                SecretKeyRef s => (true, s.Name, s.Key),
                ConfigMapKeyRef c => (false, c.Name, c.Key),
                _ => (false, "", ""),
            };
            if (key.Length == 0 || !manifests.TryGetData(isSecret, name, out var data))
            {
                steps[i] = steps[i] with { Note = steps[i].Note + "; its contents were not loaded" };
            }
            else if (data.Data.TryGetValue(key, out var value))
            {
                steps[i] = steps[i] with
                {
                    Value = value.Value,
                    Location = value.Line > 0 ? $"key {key}, {data.SourceName}:{value.Line}" : steps[i].Location,
                    Note = data.CreatedBy is null ? steps[i].Note : steps[i].Note + CreatedNote(data),
                };
            }
            else if (data.HasUnknownKeys)
            {
                steps[i] = steps[i] with { Note = steps[i].Note + UnknownKeyNote(data) };
            }
            else
            {
                steps[i] = steps[i] with { Note = steps[i].Note + "; the key is missing from it" };
            }
        }

        return steps;
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
        // Without a name, pick among the app containers; init containers run before the app and are rarely what is traced.
        var containers = containerName is not null
            ? workload.Containers.Where(c => string.Equals(c.Name, containerName, StringComparison.Ordinal)).ToList()
            : workload.Containers.Where(c => !c.IsInitContainer).ToList();
        if (containers.Count == 0 && containerName is null)
        {
            containers = [.. workload.Containers];
        }

        if (containers.Count == 0)
        {
            diagnostics.Warn(containerName is null
                ? $"{workload.DisplayName} has no containers."
                : $"No container named '{containerName}' in {workload.DisplayName}.");
            return null;
        }

        if (containers.Count > 1 && containerName is null)
        {
            diagnostics.Warn($"{workload.DisplayName} has {containers.Count} containers; using '{containers[0].Name}'. Pass a container name to pick another.");
        }

        _target = (workload, containers[0]);
        return _target;
    }

    private List<ProvenanceStep> ResolveEnv(Workload workload, Container container, string name) =>
        ResolveEnv(workload, container, name, EntrySteps(workload, container));

    // The layers that set name from envFrom and from the env entries whose steps are given: all of them, or the ones
    // before the entry whose $(VAR) references are being looked up.
    private List<ProvenanceStep> ResolveEnv(Workload workload, Container container, string name, List<ProvenanceStep> entries)
    {
        var where = $"{workload.DisplayName}, container {container.Name}";
        var chain = new List<ProvenanceStep>();
        var unloaded = new List<EnvFromEntry>();
        var unknown = new List<(EnvFromEntry From, KubernetesData Data)>();

        // envFrom is applied first, in order; env entries override it, and a later env entry overrides an earlier one.
        foreach (var from in container.EnvFrom)
        {
            if (!name.StartsWith(from.Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var key = name[from.Prefix.Length..];
            if (!manifests.TryGetData(from.IsSecret, from.Name, out var data))
            {
                unloaded.Add(from);
                continue;
            }

            if (data.Data.TryGetValue(key, out var value))
            {
                chain.Add(DataStep(from.IsSecret, data, key, value, $"envFrom on {where}" + (from.Prefix.Length > 0 ? $" with prefix '{from.Prefix}'" : "")));
            }
            else if (data.HasUnknownKeys)
            {
                unknown.Add((from, data));
            }
        }

        for (var i = 0; i < entries.Count; i++)
        {
            if (string.Equals(container.Env[i].Name, name, StringComparison.Ordinal))
            {
                chain.Add(entries[i]);
            }
        }

        if (chain.Count == 0)
        {
            foreach (var (from, data) in unknown)
            {
                var key = name[from.Prefix.Length..];
                chain.Add(new ProvenanceStep
                {
                    Kind = LayerKind.KubernetesSecret,
                    Source = $"Secret {from.Name}",
                    Location = $"key {key}",
                    IsInferred = true,
                    IsSensitive = true,
                    Note = $"envFrom on {where}{UnknownKeyNote(data)}, so this is a guess",
                    Reference = new SecretKeyRef(from.Name, key),
                });
            }

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

    // The kubelet builds a container's environment in this order (makeEnvironmentVariables in pkg/kubelet/kubelet_pods.go):
    // each envFrom source in turn, every key with its prefix, a later source overriding an earlier one; then each env
    // entry in list order, a later entry overriding an earlier one. Only an env entry's literal value is expanded, never
    // a value from envFrom or valueFrom. A $(NAME) in the value of an entry sees NAME as it is at that point: from
    // envFrom, or from an env entry before it, whose own value was already expanded, so references chain. An entry later
    // in the list, or the entry itself, is not seen, unless envFrom sets the same name. A reference to a name that is not
    // set stays as written, and $$ becomes $, so $$(NAME) gives a literal $(NAME). The value put in is not expanded
    // again. The kubelet also sees the service link variables (such as DB_SERVICE_HOST); they are not in the manifests,
    // so references to them stay as written here. When a variable that is referred to has a value the manifests don't
    // hold (a downward API field, a Secret that was not loaded), the expanded value is not known either.
    // Each variable a value refers to is an origin of that value, and when any of them may be secret, so is the value.
    private List<ProvenanceStep> EntrySteps(Workload workload, Container container)
    {
        if (_entrySteps is { } cached && ReferenceEquals(cached.Container, container))
        {
            return cached.Steps;
        }

        var where = $"{workload.DisplayName}, container {container.Name}";
        var steps = new List<ProvenanceStep>(container.Env.Count);
        var depths = new List<int>(container.Env.Count);
        var expandedLength = 0L;
        foreach (var entry in container.Env)
        {
            var depth = 0;
            steps.Add(entry.Source switch
            {
                EnvValueSource.Literal literal => LiteralStep(workload, container, entry, literal.Value, steps, depths, out depth, ref expandedLength),
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
            depths.Add(depth);
        }

        _entrySteps = (container, steps);
        return steps;
    }

    // A literal env entry with its $(VAR) references expanded, and the variables they see as origins. earlier and depths
    // hold the entries before it; depth is how many reference hops are below the step. expandedLength is how many
    // characters the values expanded so far for the container hold; a value that would take it past
    // MaxExpandedLength is not built and has no value. The notes name only the variables the container is known to
    // have: the text of a reference nothing sets is part of the value, which may be secret, so it is only counted.
    private ProvenanceStep LiteralStep(Workload workload, Container container, EnvEntry entry, string value, List<ProvenanceStep> earlier, List<int> depths, out int depth, ref long expandedLength)
    {
        var origins = new List<ProvenanceStep>();
        var seen = new Dictionary<string, ProvenanceStep?>(StringComparer.Ordinal);
        var (replaced, unknown) = (new List<string>(), new List<string>());
        var (guessed, unset) = (0, 0);
        var (escaped, expands, tooLong) = (false, false, false);
        var hops = 0;
        var room = MaxExpandedLength - expandedLength;
        var expanded = new StringBuilder();
        var end = 0;

        // Every reference is looked up, even once the value is too long to build, so that all of them stay origins.
        for (var match = EnvReference().Match(value); match.Success; match = match.NextMatch())
        {
            expands = true;
            string text;
            if (!match.Groups["name"].Success)
            {
                escaped = true;
                text = "$";
            }
            else
            {
                var name = match.Groups["name"].Value;
                if (!seen.TryGetValue(name, out var origin))
                {
                    var found = ReferencedStep(workload, container, entry, name, earlier, depths);
                    origin = found?.Step;
                    seen[name] = origin;
                    if (found is (var step, var stepDepth))
                    {
                        origins.Add(step);
                        hops = Math.Max(hops, stepDepth + 1);
                    }

                    if (origin is { IsInferred: false })
                    {
                        (origin.Value is null ? unknown : replaced).Add(name);
                    }
                }

                if (origin is null)
                {
                    unset++;
                }
                else if (origin.IsInferred)
                {
                    guessed++;
                }

                text = origin?.Value ?? match.Value;
            }

            tooLong = tooLong || !TryAppend(expanded, value.AsSpan(end, match.Index - end), room) || !TryAppend(expanded, text, room);
            end = match.Index + match.Length;
        }

        tooLong = expands && (tooLong || !TryAppend(expanded, value.AsSpan(end), room));
        var known = unknown.Count == 0 && guessed == 0 && !tooLong;
        if (expands && known)
        {
            expandedLength += expanded.Length;
        }

        var notes = new List<string> { $"container {container.Name}" };
        if (replaced.Count > 0)
        {
            notes.Add($"Kubernetes replaces {References(replaced)} with {(replaced.Count == 1 ? "its value" : "their values")}");
        }

        if (unknown.Count > 0)
        {
            notes.Add($"the {(unknown.Count == 1 ? "value" : "values")} of {References(unknown)} {(unknown.Count == 1 ? "is" : "are")} not known here, so this value is not known either");
        }

        if (guessed > 0)
        {
            notes.Add($"{Count(guessed)} may take {(guessed == 1 ? "its value" : "their values")} from a source that was not loaded, so this value is not known");
        }

        if (unset > 0)
        {
            notes.Add($"{Count(unset)} {(unset == 1 ? "is" : "are")} left as written, because no variable of {(unset == 1 ? "that name is" : "those names are")} set before this entry");
        }

        if (escaped)
        {
            notes.Add("Kubernetes turns $$ into $");
        }

        if (tooLong)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"expanding it would take the values expanded for this container past {MaxExpandedLength:N0} characters, so it is not worked out here"));
        }

        depth = hops;
        return new ProvenanceStep
        {
            Kind = LayerKind.KubernetesWorkload,
            Source = $"{workload.DisplayName} env {entry.Name}",
            Location = $"{workload.SourceName}:{entry.Line}",
            Value = !expands ? value : known ? expanded.ToString() : null,
            IsSensitive = origins.Exists(o => o.IsSensitive),
            Note = string.Join("; ", notes),
            Reference = new WorkloadEnvRef(workload.Name, container.Name, entry.Name),
            Origins = origins,
        };

        static string References(List<string> names)
        {
            var written = names.Select(n => $"$({n})").ToList();
            return written.Count == 1 ? written[0] : string.Join(", ", written[..^1]) + " and " + written[^1];
        }

        static string Count(int references) => references == 1 ? "1 reference" : string.Create(CultureInfo.InvariantCulture, $"{references} references");
    }

    // Appends text unless that would make builder longer than room, so an oversized value is never built.
    private static bool TryAppend(StringBuilder builder, ReadOnlySpan<char> text, long room)
    {
        if (builder.Length + (long)text.Length > room)
        {
            return false;
        }

        builder.Append(text);
        return true;
    }

    // The step that sets the variable a $(NAME) reference in entry sees, with how many reference hops are below it, or
    // null when no variable of that name is set before the entry. The step is sensitive when the variable may hold a
    // secret: it comes from a Secret, it refers to one itself, or its name looks like a secret.
    private (ProvenanceStep Step, int Depth)? ReferencedStep(Workload workload, Container container, EnvEntry entry, string name, List<ProvenanceStep> earlier, List<int> depths)
    {
        var chain = ResolveEnv(workload, container, name, earlier);
        if (chain.Count == 0)
        {
            return null;
        }

        var step = chain[^1];
        var depth = 0;
        for (var i = earlier.Count - 1; i >= 0; i--)
        {
            if (string.Equals(container.Env[i].Name, name, StringComparison.Ordinal))
            {
                depth = depths[i];
                break;
            }
        }

        if (depth >= MaxReferenceDepth)
        {
            (step, depth) = (step with { Origins = [] }, 0);
        }

        // A guess names one of the unloaded sources that may set the variable; any of them may be the one. Nothing is
        // known to set the name then, so the text between $( and ) may be only a part of the value, which may be
        // secret: the step says where the reference is written, and leaves out the text and the key it would be.
        if (step.IsInferred)
        {
            return (step with
            {
                Location = null,
                Reference = null,
                IsSensitive = chain.Exists(s => s.IsSensitive) || ComposeSteps.IsSensitiveName(name),
                IsEffective = true,
                Note = ComposeSteps.Join(step.Note, $"a reference in {workload.SourceName}:{entry.Line} may take its value from here"),
            }, depth);
        }

        var sensitive = step.IsSensitive || ComposeSteps.IsSensitiveName(name);
        return (step with
        {
            IsSensitive = sensitive,
            IsEffective = true,
            Note = ComposeSteps.Join(step.Note, $"referenced as $({name}) in {workload.SourceName}:{entry.Line}"),
        }, depth);
    }

    private ProvenanceStep KeyRefStep(bool isSecret, string sourceName, string key, string note)
    {
        var found = manifests.TryGetData(isSecret, sourceName, out var data);
        if (found && data!.Data.TryGetValue(key, out var value))
        {
            return DataStep(isSecret, data, key, value, note);
        }

        return new ProvenanceStep
        {
            Kind = isSecret ? LayerKind.KubernetesSecret : LayerKind.KubernetesConfigMap,
            Source = $"{(isSecret ? "Secret" : "ConfigMap")} {sourceName}",
            Location = $"key {key}",
            IsSensitive = isSecret,
            Note = note + (!found ? "; its contents were not loaded" : data!.HasUnknownKeys ? UnknownKeyNote(data) : "; the key is missing from it"),
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
        Note = data.CreatedBy is null ? note : note + CreatedNote(data),
        Reference = isSecret ? new SecretKeyRef(data.Name, key) : new ConfigMapKeyRef(data.Name, key),
    };

    private static string CreatedNote(KubernetesData data) =>
        $"; the Secret is not in the manifests but {data.CreatedBy} creates it, so its value is not known here";

    private static string UnknownKeyNote(KubernetesData data) =>
        $"; {data.CreatedBy} creates the Secret and adds keys that are only known once the store is read";

    private static ProvenanceStep OriginStep(SecretOrigin origin) => new()
    {
        Kind = LayerKind.Other,
        Source = origin.Source,
        Location = origin.Location,
        IsSensitive = true,
        IsInferred = origin.IsInferred,
        Note = origin.Note,
        Reference = origin.Reference,
    };

    private static string? RelativeToMount(VolumeMount mount, string mountPath, string normalized)
    {
        if (mount.SubPath is { Length: > 0 } subPath)
        {
            return string.Equals(normalized, mountPath, StringComparison.Ordinal) ? subPath : null;
        }

        return normalized.StartsWith(mountPath + "/", StringComparison.Ordinal) ? normalized[(mountPath.Length + 1)..] : null;
    }

    private List<ProvenanceStep> ResolveFile(Workload workload, Container container, string path)
    {
        var normalized = path.Replace('\\', '/');
        foreach (var mount in container.VolumeMounts.OrderByDescending(m => m.MountPath.Length))
        {
            var mountPath = mount.MountPath.TrimEnd('/');
            var relative = RelativeToMount(mount, mountPath, normalized);
            if (relative is null)
            {
                continue;
            }

            var volume = workload.Volumes.FirstOrDefault(v => string.Equals(v.Name, mount.Name, StringComparison.Ordinal));
            if (volume is null)
            {
                continue;
            }

            if (volume.SecretProviderClass is { } spcName)
            {
                var mountedAs = $"mounted at {mount.MountPath} by CSI volume {volume.Name} on {workload.DisplayName}, container {container.Name}";
                if (!manifests.SecretProviderClasses.TryGetValue(spcName, out var spc))
                {
                    return
                    [
                        new ProvenanceStep
                        {
                            Kind = LayerKind.Other,
                            Source = $"SecretProviderClass {spcName}",
                            Location = $"object {relative}",
                            IsSensitive = true,
                            IsInferred = true,
                            Note = $"{mountedAs}; the SecretProviderClass was not loaded",
                        },
                    ];
                }

                if (KubernetesManifests.FindObject(spc, relative) is { } origin)
                {
                    return [OriginStep(origin) with { Note = $"{origin.Note}; {mountedAs}" }];
                }

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

                if (projection.Items.Count == 0 && manifests.TryGetData(projection.IsSecret, projection.SourceName, out var known) && !known.Data.ContainsKey(key)
                    && !known.HasUnknownKeys && volume.Projections.Count > 1)
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

    // Kubernetes' $(VAR) syntax (third_party/forked/golang/expansion in kubernetes/kubernetes): $$ is an escaped $, and
    // $(NAME) runs to the first closing parenthesis. Any other $, including a $( that is never closed, is plain text.
    [GeneratedRegex(@"\$\$|\$\((?<name>[^)]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex EnvReference();
}
