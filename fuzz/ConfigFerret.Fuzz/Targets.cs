using System.Text;
using ConfigFerret.DotNet;
using ConfigFerret.Helm;
using ConfigFerret.Kubernetes;
using ConfigFerret.Kustomize;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace ConfigFerret.Fuzz;

/// <summary>A fuzz target. It throws only when it finds a bug: an unexpected exception or a broken invariant.</summary>
internal sealed record FuzzTarget(string Description, FuzzAction Run);

internal delegate void FuzzAction(ReadOnlySpan<byte> data);

/// <summary>Thrown when a target's output breaks an invariant the library promises.</summary>
internal sealed class InvariantException(string message) : Exception(message);

/// <summary>
/// The fuzz targets. Each one feeds the input to a ConfigFerret parser and then drives the resolvers over what it parsed.
/// Malformed input is fine as long as the parser rejects it with the exception it documents
/// (<see cref="FormatException"/> for YAML, <see cref="System.Text.Json.JsonException"/> for JSON).
/// Inputs that hold several files separate them with a line holding only <c>%%</c>.
/// </summary>
internal static class Targets
{
    private static readonly string Scratch = Path.Join(Path.GetTempPath(), $"configferret-fuzz-{Environment.ProcessId}");

    public static readonly IReadOnlyDictionary<string, FuzzTarget> All = new Dictionary<string, FuzzTarget>(StringComparer.Ordinal)
    {
        ["json"] = new("appsettings JSON line locator, checked against the .NET JSON provider", Json),
        ["helm-values"] = new("Helm values files merged in order (files split by %%)", HelmValues),
        ["helm-set"] = new("Helm --set expressions, one per line", HelmSet),
        ["helm-template"] = new("Helm template scanning for .Values references", HelmTemplate),
        ["kubernetes"] = new("Kubernetes manifests and the env/volume resolver", KubernetesManifestsTarget),
        ["container"] = new("Kubernetes manifests traced from outside with ConfigTracer.ForContainer, then rendered", Container),
        ["kustomize"] = new("Kustomization overlay %% env file %% base kustomization", KustomizeTarget),
        ["env-names"] = new("Environment variable name matching: prefix, key, value, then NAME=VALUE lines", EnvNames),
    };

    private static void Json(ReadOnlySpan<byte> data)
    {
        Dictionary<string, int>? located;
        try
        {
            located = JsonLineLocator.Locate(data);
        }
        catch (System.Text.Json.JsonException)
        {
            located = null;
        }

        // Whatever the .NET JSON provider accepts, the locator must read too, and find every key with a value.
        IDictionary<string, string?> provider;
        try
        {
            provider = LoadWithJsonProvider(data.ToArray());
        }
        catch (FormatException)
        {
            return;
        }

        if (located is null)
        {
            throw new InvariantException("The .NET JSON provider read the input but JsonLineLocator threw.");
        }

        foreach (var (key, value) in provider)
        {
            if (value is not null && !located.ContainsKey(key))
            {
                throw new InvariantException($"JsonLineLocator did not find key '{key}', which the .NET JSON provider has.");
            }

            if (located.TryGetValue(key, out var line) && line < 1)
            {
                throw new InvariantException($"JsonLineLocator gave key '{key}' line {line}.");
            }
        }
    }

    private static Dictionary<string, string?> LoadWithJsonProvider(byte[] json)
    {
        var provider = new JsonStreamConfigurationProvider(new JsonStreamConfigurationSource());
        using var stream = new MemoryStream(json);
        try
        {
            provider.Load(stream);
        }
        catch (InvalidDataException ex)
        {
            throw new FormatException(ex.Message, ex);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new FormatException(ex.Message, ex);
        }
        catch (InvalidOperationException ex)
        {
            // The provider throws this for an escaped lone surrogate in a property name.
            throw new FormatException(ex.Message, ex);
        }

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in AllKeys(provider, null))
        {
            provider.TryGet(key, out var value);
            result[key] = value;
        }

        return result;
    }

    private static IEnumerable<string> AllKeys(IConfigurationProvider provider, string? parent)
    {
        foreach (var child in provider.GetChildKeys([], parent).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var key = parent is null ? child : ConfigurationPath.Combine(parent, child);
            yield return key;
            foreach (var nested in AllKeys(provider, key))
            {
                yield return nested;
            }
        }
    }

    private static void HelmValues(ReadOnlySpan<byte> data)
    {
        var files = Split(data).Select((text, i) => ($"values-{i}.yaml", text)).ToList();
        HelmChart chart;
        try
        {
            chart = HelmChart.FromText(files);
        }
        catch (FormatException)
        {
            return;
        }

        ExerciseChart(chart, chart.History.Keys);
    }

    private static void HelmSet(ReadOnlySpan<byte> data)
    {
        var sets = Text(data).Split('\n');
        var chart = HelmChart.Load(EmptyDirectory(), setValues: sets);
        ExerciseChart(chart, chart.History.Keys);
    }

    private static void HelmTemplate(ReadOnlySpan<byte> data)
    {
        var text = Text(data);
        const string values = "db:\n  timeout: 30\n  host: db.internal\nlogging:\n  level: Information\n";
        var chart = HelmChart.FromText([("values.yaml", values)], [("templates/fuzz.yaml", text)]);
        var names = text.Split([' ', '\t', '\n', '\r', ':', '"', '\'', '{', '}'], StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .Take(200);
        ExerciseChart(chart, names);
    }

    private static void ExerciseChart(HelmChart chart, IEnumerable<string> names)
    {
        var resolver = new HelmResolver(chart);
        var diagnostics = new TraceDiagnostics();
        foreach (var name in names.ToList())
        {
            var value = chart.GetValue(name);
            if (chart.History.TryGetValue(name, out var steps) && steps.Count > 0 && (value is null) != (steps[^1].IsRemoval || steps[^1].Value is null))
            {
                throw new InvariantException($"GetValue('{name}') disagrees with the last history step.");
            }

            foreach (var use in chart.FindUses(name))
            {
                if (use.Line < 1)
                {
                    throw new InvariantException($"Template use of '{name}' has line {use.Line}.");
                }
            }

            Consume(resolver.Resolve(Step(LayerKind.Helm, new HelmValueRef(name), value), diagnostics));
            Consume(resolver.Resolve(Step(LayerKind.KubernetesConfigMap, new ConfigMapKeyRef("cm", name), value), diagnostics));
            Consume(resolver.Resolve(Step(LayerKind.KubernetesWorkload, new WorkloadEnvRef("w", "c", name), value), diagnostics));
        }
    }

    private static void KubernetesManifestsTarget(ReadOnlySpan<byte> data)
    {
        KubernetesManifests manifests;
        try
        {
            manifests = KubernetesManifests.Parse(Text(data), "fuzz.yaml");
        }
        catch (FormatException)
        {
            return;
        }

        var diagnostics = new TraceDiagnostics();
        foreach (var workload in manifests.Workloads.Take(20))
        {
            foreach (var container in workload.Containers.Take(20))
            {
                var resolver = new KubernetesResolver(manifests, workload.Name, container.Name);
                var names = container.Env.Select(e => e.Name)
                    .Concat(container.EnvFrom.SelectMany(f => KeysOf(manifests, f).Select(k => f.Prefix + k)))
                    .Distinct(StringComparer.Ordinal)
                    .Take(200);
                foreach (var name in names)
                {
                    Consume(resolver.Resolve(Step(LayerKind.DotNetProvider, new EnvironmentVariableRef(name), null), diagnostics));
                }

                foreach (var mount in container.VolumeMounts.Take(50))
                {
                    foreach (var path in new[] { mount.MountPath, mount.MountPath + "/appsettings.json", mount.MountPath.TrimEnd('/') + "/" + mount.SubPath })
                    {
                        Consume(resolver.Resolve(Step(LayerKind.DotNetProvider, new FileRef(path), null), diagnostics));
                    }
                }
            }
        }
    }

    private static void Container(ReadOnlySpan<byte> data)
    {
        KubernetesManifests manifests;
        try
        {
            manifests = KubernetesManifests.Parse(Text(data), "fuzz.yaml");
        }
        catch (FormatException)
        {
            return;
        }

        // The default pick (no names) and every named container, the way a cluster browser would ask.
        var targets = new List<(string? Workload, string? Container)> { (null, null) };
        foreach (var workload in manifests.Workloads.Take(10))
        {
            foreach (var container in workload.Containers.Take(10))
            {
                targets.Add((workload.Name, container.Name));
            }
        }

        foreach (var (workloadName, containerName) in targets)
        {
            var report = ConfigTracer.ForContainer(manifests, workloadName, containerName).Trace();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var setting in report.Settings)
            {
                if (!keys.Add(setting.Key))
                {
                    throw new InvariantException($"ForContainer listed '{setting.Key}' twice.");
                }

                if (!ReferenceEquals(report.Find(setting.Key), setting))
                {
                    throw new InvariantException($"Find('{setting.Key}') did not return the listed setting.");
                }

                // A Secret's value must never reach the report unredacted.
                if (setting.Steps.Count > 0 && setting.Steps[^1].Kind == LayerKind.KubernetesSecret
                    && setting.Value is not null && setting.Value != new TraceOptions().RedactedText)
                {
                    throw new InvariantException($"'{setting.Key}' comes from a Secret but its value is not redacted.");
                }
            }

            _ = report.ToText();
            _ = report.ToJson();
        }
    }

    private static IEnumerable<string> KeysOf(KubernetesManifests manifests, EnvFromEntry from) =>
        (from.IsSecret ? manifests.Secrets : manifests.ConfigMaps).TryGetValue(from.Name, out var data) ? data.Data.Keys : [];

    private static void KustomizeTarget(ReadOnlySpan<byte> data)
    {
        var parts = Split(data);
        var overlay = Path.Join(Scratch, "kustomize", "overlay");
        var baseDir = Path.Join(Scratch, "kustomize", "base");
        Directory.CreateDirectory(overlay);
        Directory.CreateDirectory(baseDir);
        WriteOrDelete(Path.Join(overlay, "kustomization.yaml"), parts.ElementAtOrDefault(0) ?? "");
        WriteOrDelete(Path.Join(overlay, "app.properties"), parts.ElementAtOrDefault(1));
        WriteOrDelete(Path.Join(baseDir, "kustomization.yaml"), parts.ElementAtOrDefault(2));

        Kustomization kustomization;
        try
        {
            kustomization = Kustomization.Load(overlay);
        }
        catch (FormatException)
        {
            return;
        }
        catch (FileNotFoundException ex) when (ex.Message.StartsWith("No kustomization file in ", StringComparison.Ordinal))
        {
            return;
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Kustomization cycle at ", StringComparison.Ordinal))
        {
            return;
        }

        var resolver = new KustomizeResolver(kustomization);
        var diagnostics = new TraceDiagnostics();
        foreach (var (name, generated) in kustomization.ConfigMaps.Concat(kustomization.Secrets))
        {
            if (kustomization.Find(name, generated.IsSecret) is null || kustomization.Find(name + "-0123456789", generated.IsSecret) is null)
            {
                throw new InvariantException($"Kustomization.Find did not find generated '{name}'.");
            }

            foreach (var key in generated.Keys.Keys.Take(200))
            {
                SourceRef reference = generated.IsSecret ? new SecretKeyRef(name, key) : new ConfigMapKeyRef(name, key);
                var kind = generated.IsSecret ? LayerKind.KubernetesSecret : LayerKind.KubernetesConfigMap;
                Consume(resolver.Resolve(Step(kind, reference, null), diagnostics));
            }
        }
    }

    private static void EnvNames(ReadOnlySpan<byte> data)
    {
        var lines = Text(data).Split('\n');
        var prefix = lines.ElementAtOrDefault(0) ?? "";
        var key = lines.ElementAtOrDefault(1) ?? "";
        var value = lines.ElementAtOrDefault(2);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(3))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                environment[line[..eq]] = line[(eq + 1)..];
            }
        }

        var describer = new ProviderDescriber(environment, new TraceDiagnostics());
        var found = describer.FindEnvironmentVariable(prefix, key, value);
        if (found is not null && !environment.ContainsKey(found))
        {
            throw new InvariantException($"FindEnvironmentVariable returned '{found}', which is not in the environment.");
        }

        var step = describer.Describe(new EnvironmentVariablesConfigurationProvider(prefix), key, value);
        if (step.Reference is not EnvironmentVariableRef)
        {
            throw new InvariantException("An environment variable step has no EnvironmentVariableRef.");
        }
    }

    private static ProvenanceStep Step(LayerKind kind, SourceRef reference, string? value) =>
        new() { Kind = kind, Source = "fuzz", Reference = reference, Value = value };

    private static void Consume(IReadOnlyList<ProvenanceStep> steps)
    {
        foreach (var step in steps)
        {
            if (step.Source is null)
            {
                throw new InvariantException("A resolver returned a step with no source.");
            }
        }
    }

    private static string Text(ReadOnlySpan<byte> data) => Encoding.UTF8.GetString(data);

    private static List<string> Split(ReadOnlySpan<byte> data) =>
        Text(data).Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n%%\n").ToList();

    private static string EmptyDirectory()
    {
        var dir = Path.Join(Scratch, "empty");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteOrDelete(string path, string? text)
    {
        if (text is null)
        {
            File.Delete(path);
        }
        else
        {
            File.WriteAllText(path, text);
        }
    }
}
