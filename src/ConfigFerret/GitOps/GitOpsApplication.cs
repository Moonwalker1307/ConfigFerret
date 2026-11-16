using ConfigFerret.Internal;
using ConfigFerret.Kubernetes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ConfigFerret.GitOps;

/// <summary>The kind of GitOps object a <see cref="GitOpsApplication"/> was read from.</summary>
public enum GitOpsKind
{
    /// <summary>An Argo CD <c>Application</c> (<c>argoproj.io</c>).</summary>
    ArgoCdApplication,

    /// <summary>The <c>spec.template</c> of an Argo CD <c>ApplicationSet</c>. Generator parameters are not filled in.</summary>
    ArgoCdApplicationSet,

    /// <summary>A Flux <c>HelmRelease</c> (<c>helm.toolkit.fluxcd.io/v2</c>, <c>v2beta1</c> or <c>v2beta2</c>).</summary>
    FluxHelmRelease,

    /// <summary>A Flux <c>Kustomization</c> (<c>kustomize.toolkit.fluxcd.io</c>).</summary>
    FluxKustomization,
}

/// <summary>
/// An Argo CD Application or ApplicationSet template, a Flux HelmRelease or a Flux Kustomization, read from a manifest
/// in Git. It holds what the object adds on top of the chart or the manifests it deploys: Helm values files, inline
/// values and parameters, <c>valuesFrom</c>, kustomize overrides and <c>postBuild</c> substitutions.
/// </summary>
/// <remarks>
/// Turn a Helm release into Helm layers with <see cref="Helm.HelmChart.ForGitOps"/>, or trace it with
/// <see cref="ConfigTracer.WithGitOps"/>, which also links kustomize patches and Flux substitutions.
/// </remarks>
public sealed class GitOpsApplication
{
    private GitOpsApplication(GitOpsKind kind, string name, string sourceName, int line, KubernetesManifests data)
    {
        Kind = kind;
        Name = name;
        SourceName = sourceName;
        Line = line;
        Data = data;
    }

    /// <summary>The kind of object.</summary>
    public GitOpsKind Kind { get; }

    /// <summary>The object's name, for example <c>orders-prod</c>. For an ApplicationSet, the template's name when it has one.</summary>
    public string Name { get; }

    /// <summary>The object's namespace, when the manifest gives one.</summary>
    public string? Namespace { get; private init; }

    /// <summary>The file or text name the object was read from.</summary>
    public string SourceName { get; }

    /// <summary>The line of the object in <see cref="SourceName"/>.</summary>
    public int Line { get; }

    /// <summary>A name for reports, for example <c>Argo CD Application orders-prod</c> or <c>Flux HelmRelease orders</c>.</summary>
    public string DisplayName => $"{KindName(Kind)} {Name}";

    /// <summary>The Helm charts the object deploys with their values, in the order the manifest lists them.</summary>
    public IReadOnlyList<GitOpsHelmSource> HelmSources { get; private init; } = [];

    /// <summary>The kustomize overrides of an Argo CD source (<c>spec.source.kustomize</c>), when it has them.</summary>
    public GitOpsKustomize? Kustomize { get; private init; }

    /// <summary>
    /// The folder the object deploys, relative to its repository: an Argo CD source <c>path</c> or a Flux Kustomization
    /// <c>spec.path</c>. Null when it deploys a chart from a Helm repository.
    /// </summary>
    public string? Path { get; private init; }

    /// <summary>A Flux Kustomization's inline <c>postBuild.substitute</c> variables, in manifest order.</summary>
    public IReadOnlyList<GitOpsSubstitution> Substitutions { get; private init; } = [];

    /// <summary>A Flux Kustomization's <c>postBuild.substituteFrom</c> ConfigMaps and Secrets, lowest priority first.</summary>
    public IReadOnlyList<GitOpsDataReference> SubstituteFrom { get; private init; } = [];

    /// <summary>Settings the object has that ConfigFerret reads but does not trace, such as kustomize images or Argo CD file parameters.</summary>
    public IReadOnlyList<string> Notes { get; private init; } = [];

    /// <summary>The ConfigMaps and Secrets found in the same manifests, used for Flux <c>valuesFrom</c> and <c>substituteFrom</c>.</summary>
    public KubernetesManifests Data { get; }

    /// <summary>Reads every Argo CD Application, ApplicationSet, Flux HelmRelease and Flux Kustomization in the YAML text.</summary>
    /// <param name="text">The manifest text. Other objects are skipped, except ConfigMaps and Secrets, which go into <see cref="Data"/>.</param>
    /// <param name="sourceName">A name for the text, used in output (for example a file name).</param>
    /// <exception cref="FormatException">The text is not valid YAML.</exception>
    public static IReadOnlyList<GitOpsApplication> ParseAll(string text, string sourceName = "manifests")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        List<YamlNode> docs;
        try
        {
            docs = Yaml.LoadDocuments(text);
        }
        catch (YamlException ex)
        {
            throw new FormatException($"Could not parse GitOps manifests in {sourceName}: {ex.Message}", ex);
        }

        var data = new KubernetesManifests();
        var objects = docs.SelectMany(Flatten).ToList();
        foreach (var obj in objects.Where(o => o.Str("kind") is "ConfigMap" or "Secret"))
        {
            data.AddObject(obj, sourceName);
        }

        return objects.Select(o => Read(o, sourceName, data)).OfType<GitOpsApplication>().ToList();
    }

    /// <summary>Reads the one GitOps object in the text, or the one named <paramref name="name"/>.</summary>
    /// <exception cref="FormatException">The text is not valid YAML.</exception>
    /// <exception cref="InvalidOperationException">No object matches, or several do and no name was given.</exception>
    public static GitOpsApplication Parse(string text, string sourceName = "manifests", string? name = null) =>
        Pick(ParseAll(text, sourceName), sourceName, name);

    /// <summary>Reads the one GitOps object in a manifest file, or the one named <paramref name="name"/>.</summary>
    /// <exception cref="FormatException">The file is not valid YAML.</exception>
    /// <exception cref="InvalidOperationException">No object matches, or several do and no name was given.</exception>
    public static GitOpsApplication Load(string manifestPath, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        return Parse(File.ReadAllText(manifestPath), System.IO.Path.GetFileName(manifestPath), name);
    }

    /// <summary>Picks the object named <paramref name="name"/> (or the only one) from <paramref name="applications"/>.</summary>
    internal static GitOpsApplication Pick(IReadOnlyList<GitOpsApplication> applications, string sourceName, string? name, Func<GitOpsApplication, bool>? filter = null)
    {
        var candidates = applications.Where(a => filter is null || filter(a)).ToList();
        if (!string.IsNullOrWhiteSpace(name))
        {
            return candidates.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"{sourceName} has no GitOps object named '{name}'. It has: {Names(candidates)}.");
        }

        return candidates.Count switch
        {
            0 => throw new InvalidOperationException($"{sourceName} has no Argo CD Application or Flux HelmRelease or Kustomization."),
            1 => candidates[0],
            _ => throw new InvalidOperationException($"{sourceName} has {candidates.Count} GitOps objects ({Names(candidates)}); pass the name of the one to trace."),
        };
    }

    private static string Names(List<GitOpsApplication> applications) =>
        applications.Count == 0 ? "none" : string.Join(", ", applications.Select(a => a.DisplayName));

    internal static string KindName(GitOpsKind kind) => kind switch
    {
        GitOpsKind.ArgoCdApplication => "Argo CD Application",
        GitOpsKind.ArgoCdApplicationSet => "Argo CD ApplicationSet",
        GitOpsKind.FluxHelmRelease => "Flux HelmRelease",
        _ => "Flux Kustomization",
    };

    private static IEnumerable<YamlNode> Flatten(YamlNode doc) =>
        doc.Str("kind") is "List" ? doc.Get("items").Items() : [doc];

    private static GitOpsApplication? Read(YamlNode obj, string sourceName, KubernetesManifests data)
    {
        var apiVersion = obj.Str("apiVersion") ?? "";
        var kind = obj.Str("kind");
        return (kind, apiVersion) switch
        {
            ("Application", _) when apiVersion.StartsWith("argoproj.io/", StringComparison.Ordinal) =>
                ReadArgo(obj, obj.Get("spec"), GitOpsKind.ArgoCdApplication, obj.Get("metadata").Str("name"), sourceName, data, []),
            ("ApplicationSet", _) when apiVersion.StartsWith("argoproj.io/", StringComparison.Ordinal) =>
                ReadArgo(
                    obj,
                    obj.Get("spec").Get("template").Get("spec"),
                    GitOpsKind.ArgoCdApplicationSet,
                    obj.Get("spec").Get("template").Get("metadata").Str("name") ?? obj.Get("metadata").Str("name"),
                    sourceName,
                    data,
                    ["ApplicationSet generator parameters such as {{name}} are not filled in, so the template is read as written"]),
            ("HelmRelease", _) when apiVersion.StartsWith("helm.toolkit.fluxcd.io/v2", StringComparison.Ordinal) =>
                ReadHelmRelease(obj, sourceName, data),
            ("Kustomization", _) when apiVersion.StartsWith("kustomize.toolkit.fluxcd.io/", StringComparison.Ordinal) =>
                ReadFluxKustomization(obj, sourceName, data),
            _ => null,
        };
    }

    private static string NameOf(string? name) => string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name;

    // An empty namespace is no namespace, as in the manifests the object reads its ConfigMaps and Secrets from.
    private static string? NamespaceOf(YamlNode obj) => obj.Get("metadata").Str("namespace") is { Length: > 0 } ns ? ns : null;

    private static GitOpsApplication ReadArgo(YamlNode obj, YamlNode? spec, GitOpsKind kind, string? name, string sourceName, KubernetesManifests data, List<string> notes)
    {
        var sources = spec.Get("sources").Items().ToList();
        if (sources.Count == 0 && spec.Get("source") is { } single)
        {
            sources.Add(single);
        }

        var helm = sources
            .Where(s => s.Get("chart") is not null || s.Get("helm") is not null)
            .Select(s => ReadArgoHelm(s, notes))
            .ToList();
        var kustomizeSource = sources.FirstOrDefault(s => s.Get("kustomize") is not null);
        var pathSource = sources.FirstOrDefault(s => s.Str("path") is not null && s.Str("ref") is null);
        var kustomize = kustomizeSource is null ? null : ReadArgoKustomize(kustomizeSource.Get("kustomize")!, notes);
        return new GitOpsApplication(kind, NameOf(name), sourceName, obj.Line(), data)
        {
            Namespace = NamespaceOf(obj),
            HelmSources = helm,
            Kustomize = kustomize,
            Path = (kustomizeSource ?? pathSource)?.Str("path"),
            Notes = notes,
        };
    }

    private static GitOpsHelmSource ReadArgoHelm(YamlNode source, List<string> notes)
    {
        var helm = source.Get("helm");
        var layers = new List<GitOpsValuesLayer>();
        var valuesObject = helm.Get("valuesObject");
        var values = helm.Get("values");
        if (valuesObject is YamlMappingNode)
        {
            if (values.Scalar() is not null)
            {
                notes.Add("helm.values is ignored because helm.valuesObject is set, as Argo CD does");
            }

            layers.Add(new GitOpsValuesLayer("helm.valuesObject", GitOpsValuesKind.Inline, valuesObject.Line()) { Node = valuesObject });
        }
        else if (values is YamlMappingNode)
        {
            // Older manifests sometimes write values as a mapping; Argo CD reads it the same way.
            layers.Add(new GitOpsValuesLayer("helm.values", GitOpsValuesKind.Inline, values.Line()) { Node = values });
        }
        else if (values is YamlScalarNode scalar && scalar.Scalar() is { } text)
        {
            layers.Add(new GitOpsValuesLayer("helm.values", GitOpsValuesKind.Inline, scalar.Line())
            {
                Text = text,
                LineOffset = BlockOffset(scalar),
            });
        }

        layers.AddRange(helm.Get("parameters").Items()
            .Select(p => (Node: p, Path: p.Str("name")))
            .Where(p => !string.IsNullOrWhiteSpace(p.Path))
            .Select(p => new GitOpsValuesLayer("helm.parameters", GitOpsValuesKind.Parameter, p.Node.Line())
            {
                TargetPath = p.Path,
                Value = p.Node.Get("value").Scalar() ?? "",
                ForceString = string.Equals(p.Node.Str("forceString"), "true", StringComparison.OrdinalIgnoreCase),
            }));

        if (helm.Get("fileParameters").Items().Any())
        {
            notes.Add("helm.fileParameters (--set-file) are not traced");
        }

        return new GitOpsHelmSource
        {
            RepoUrl = source.Str("repoURL"),
            Chart = source.Str("chart"),
            Path = source.Str("path"),
            TargetRevision = source.Str("targetRevision"),
            ReleaseName = helm.Str("releaseName"),
            ValuesFiles = helm.Get("valueFiles").Items()
                .Select(v => v.Scalar() is { } p ? new GitOpsValuesFile(p, v.Line()) : null)
                .OfType<GitOpsValuesFile>()
                .ToList(),
            IgnoreMissingValuesFiles = IsTrue(helm.Str("ignoreMissingValueFiles")),
            Layers = layers,
            IsRemote = source.Str("chart") is not null,
        };
    }

    private static GitOpsKustomize ReadArgoKustomize(YamlNode kustomize, List<string> notes)
    {
        var result = new GitOpsKustomize
        {
            NamePrefix = kustomize.Str("namePrefix"),
            NameSuffix = kustomize.Str("nameSuffix"),
            Namespace = kustomize.Str("namespace"),
            Images = kustomize.Get("images").Items().Select(i => i.Scalar()).OfType<string>().ToList(),
            CommonLabels = Strings(kustomize.Get("commonLabels")),
            CommonAnnotations = Strings(kustomize.Get("commonAnnotations")),
            Patches = kustomize.Get("patches").Items().Select(ReadPatch).ToList(),
        };

        var settings = new List<string>();
        if (result.NamePrefix is not null)
        {
            settings.Add($"namePrefix '{result.NamePrefix}'");
        }

        if (result.NameSuffix is not null)
        {
            settings.Add($"nameSuffix '{result.NameSuffix}'");
        }

        if (result.Namespace is not null)
        {
            settings.Add($"namespace '{result.Namespace}'");
        }

        if (result.Images.Count > 0)
        {
            settings.Add("images " + string.Join(", ", result.Images));
        }

        if (result.CommonLabels.Count > 0)
        {
            settings.Add("commonLabels " + string.Join(", ", result.CommonLabels.Select(l => $"{l.Key}={l.Value}")));
        }

        if (result.CommonAnnotations.Count > 0)
        {
            settings.Add("commonAnnotations " + string.Join(", ", result.CommonAnnotations.Keys));
        }

        var untraced = result.Patches.Count(p => p.Values.Count == 0);
        if (untraced > 0)
        {
            settings.Add(untraced == 1 ? "1 patch that does not set ConfigMap data" : $"{untraced} patches that do not set ConfigMap data");
        }

        if (settings.Count > 0)
        {
            notes.Add("kustomize " + string.Join("; ", settings) + " are applied by Argo CD on top of the manifests in Git");
        }

        return result;
    }

    private static GitOpsPatch ReadPatch(YamlNode item)
    {
        var target = item.Get("target");
        var text = item.Str("patch");
        var targetKind = target.Str("kind");
        var targetName = target.Str("name");
        var values = new List<GitOpsPatchValue>();
        if (text is not null)
        {
            YamlNode? doc = null;
            try
            {
                doc = Yaml.LoadDocuments(text).FirstOrDefault();
            }
            catch (YamlException)
            {
                doc = null;
            }

            var offset = BlockOffset(item.Get("patch")!);
            if (doc is YamlSequenceNode ops)
            {
                values.AddRange(ReadJsonPatch(ops, targetKind, targetName, offset));
            }
            else if (doc is not null)
            {
                targetKind ??= doc.Str("kind");
                targetName ??= doc.Get("metadata").Str("name");
                if (targetKind == "ConfigMap" && targetName is not null)
                {
                    values.AddRange(doc.Get("data").Pairs().Select(p => new GitOpsPatchValue(targetName, p.Key, p.Value.Scalar(), p.Value.Line() + offset, IsRemoval: p.Value.IsNull())));
                }
            }
        }

        return new GitOpsPatch(targetKind, targetName, item.Str("path"), item.Line(), values);
    }

    private static List<GitOpsPatchValue> ReadJsonPatch(YamlSequenceNode ops, string? targetKind, string? targetName, int offset)
    {
        if (targetKind != "ConfigMap" || targetName is null)
        {
            return [];
        }

        return ops.Children
            .Select(op => (Op: op.Str("op"), Path: op.Str("path"), Node: op))
            .Where(o => o.Op is "add" or "replace" or "remove" && o.Path is not null && o.Path.StartsWith("/data/", StringComparison.Ordinal) && o.Path.Length > 6)
            .Select(o => new GitOpsPatchValue(
                targetName,
                o.Path!["/data/".Length..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal),
                o.Op == "remove" ? null : Yaml.Render(o.Node.Get("value")),
                o.Node.Line() + offset,
                IsRemoval: o.Op == "remove"))
            .ToList();
    }

    private static GitOpsApplication ReadHelmRelease(YamlNode obj, string sourceName, KubernetesManifests data)
    {
        var spec = obj.Get("spec");
        var chartSpec = spec.Get("chart").Get("spec");
        var sourceRef = chartSpec.Get("sourceRef") ?? spec.Get("chartRef");
        var sourceKind = sourceRef.Str("kind");
        var notes = new List<string>();
        var layers = spec.Get("valuesFrom").Items().Select(ReadValuesFrom).OfType<GitOpsValuesLayer>().ToList();
        if (spec.Get("values") is YamlMappingNode values)
        {
            layers.Add(new GitOpsValuesLayer("spec.values", GitOpsValuesKind.Inline, values.Line()) { Node = values });
        }

        if (spec.Get("postRenderers").Items().Any())
        {
            notes.Add("spec.postRenderers are applied by Flux after Helm renders the chart and are not traced");
        }

        var files = chartSpec.Get("valuesFiles").Items().ToList();
        if (chartSpec.Get("valuesFile") is { } legacy)
        {
            files.Insert(0, legacy);
        }

        var isPath = sourceKind is "GitRepository" or "Bucket";
        var helm = new GitOpsHelmSource
        {
            RepoUrl = sourceRef is null ? null : $"{sourceKind}/{sourceRef.Str("name")}",
            Chart = isPath ? null : chartSpec.Str("chart"),
            Path = isPath ? chartSpec.Str("chart") : null,
            TargetRevision = chartSpec.Str("version"),
            ReleaseName = spec.Str("releaseName"),
            ValuesFiles = files.Select(v => v.Scalar() is { } p ? new GitOpsValuesFile(p, v.Line()) : null).OfType<GitOpsValuesFile>().ToList(),
            IgnoreMissingValuesFiles = IsTrue(chartSpec.Str("ignoreMissingValuesFiles")),
            Layers = layers,
            IsRemote = !isPath,
            ValuesFilesReplaceChartValues = true,
        };

        return new GitOpsApplication(GitOpsKind.FluxHelmRelease, NameOf(obj.Get("metadata").Str("name")), sourceName, obj.Line(), data)
        {
            Namespace = NamespaceOf(obj),
            HelmSources = [helm],
            Path = helm.Path,
            Notes = notes,
        };
    }

    private static GitOpsValuesLayer? ReadValuesFrom(YamlNode item)
    {
        var kind = item.Str("kind");
        var name = item.Str("name");
        if (kind is not ("ConfigMap" or "Secret") || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var key = item.Str("valuesKey") ?? "values.yaml";
        return new GitOpsValuesLayer($"valuesFrom {kind} {name}, key {key}", kind == "Secret" ? GitOpsValuesKind.Secret : GitOpsValuesKind.ConfigMap, item.Line())
        {
            ObjectName = name,
            Key = key,
            TargetPath = item.Str("targetPath"),
            Optional = IsTrue(item.Str("optional")),
        };
    }

    private static GitOpsApplication ReadFluxKustomization(YamlNode obj, string sourceName, KubernetesManifests data)
    {
        var spec = obj.Get("spec");
        var postBuild = spec.Get("postBuild");
        var notes = new List<string>();
        if (spec.Get("patches").Items().Any())
        {
            notes.Add("spec.patches are applied by Flux on top of the manifests in Git and are not traced");
        }

        if (spec.Get("images").Items().Any())
        {
            notes.Add("spec.images are applied by Flux on top of the manifests in Git");
        }

        return new GitOpsApplication(GitOpsKind.FluxKustomization, NameOf(obj.Get("metadata").Str("name")), sourceName, obj.Line(), data)
        {
            Namespace = NamespaceOf(obj),
            Path = spec.Str("path"),
            Substitutions = postBuild.Get("substitute").Pairs()
                .Select(p => new GitOpsSubstitution(p.Key, p.Value.Scalar() ?? "", p.Value.Line()))
                .ToList(),
            SubstituteFrom = postBuild.Get("substituteFrom").Items()
                .Select(i => (Kind: i.Str("kind"), Name: i.Str("name"), Node: i))
                .Where(i => i.Kind is "ConfigMap" or "Secret" && !string.IsNullOrWhiteSpace(i.Name))
                .Select(i => new GitOpsDataReference(i.Kind == "Secret", i.Name!, IsTrue(i.Node.Str("optional")), i.Node.Line()))
                .ToList(),
            Notes = notes,
        };
    }

    private static Dictionary<string, string> Strings(YamlNode? node) =>
        node.Pairs().ToDictionary(p => p.Key, p => p.Value.Scalar() ?? "", StringComparer.Ordinal);

    private static bool IsTrue(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The line offset of a block scalar's content: line 1 of a <c>|</c> block is the line after the key.</summary>
    private static int BlockOffset(YamlNode node) =>
        node is YamlScalarNode { Style: ScalarStyle.Literal or ScalarStyle.Folded } ? node.Line() : node.Line() - 1;
}

/// <summary>A Helm chart an Argo CD source or a Flux HelmRelease deploys, and the values it passes.</summary>
public sealed record GitOpsHelmSource
{
    /// <summary>The Argo CD <c>repoURL</c>, or the Flux <c>sourceRef</c> as <c>Kind/name</c>.</summary>
    public string? RepoUrl { get; init; }

    /// <summary>The chart name in a Helm repository, or null when the chart is a folder in Git (<see cref="Path"/>).</summary>
    public string? Chart { get; init; }

    /// <summary>The chart folder in the Git repository, when the chart is not from a Helm repository.</summary>
    public string? Path { get; init; }

    /// <summary>The Argo CD <c>targetRevision</c> or the Flux chart <c>version</c>.</summary>
    public string? TargetRevision { get; init; }

    /// <summary>The Helm release name, when the manifest sets one.</summary>
    public string? ReleaseName { get; init; }

    /// <summary>The values files (Argo CD <c>valueFiles</c>, Flux <c>valuesFiles</c>), lowest priority first.</summary>
    public IReadOnlyList<GitOpsValuesFile> ValuesFiles { get; init; } = [];

    /// <summary>True when a missing values file is skipped instead of failing the sync.</summary>
    public bool IgnoreMissingValuesFiles { get; init; }

    /// <summary>The values layers that come after the files, lowest priority first: inline values, <c>valuesFrom</c> and parameters.</summary>
    public IReadOnlyList<GitOpsValuesLayer> Layers { get; init; } = [];

    /// <summary>True when the chart comes from a Helm or OCI repository, which ConfigFerret cannot fetch.</summary>
    public bool IsRemote { get; init; }

    /// <summary>
    /// True for Flux: listed values files replace the chart's own <c>values.yaml</c> unless they include it.
    /// Argo CD always starts from the chart's <c>values.yaml</c>.
    /// </summary>
    public bool ValuesFilesReplaceChartValues { get; init; }
}

/// <summary>A values file named by a GitOps object.</summary>
/// <param name="Path">The path as written, for example <c>values-prod.yaml</c> or <c>$values/envs/prod.yaml</c>.</param>
/// <param name="Line">The line in the GitOps manifest.</param>
public sealed record GitOpsValuesFile(string Path, int Line);

/// <summary>Where a <see cref="GitOpsValuesLayer"/> comes from.</summary>
public enum GitOpsValuesKind
{
    /// <summary>Values written in the manifest (Argo CD <c>helm.values</c> or <c>helm.valuesObject</c>, Flux <c>spec.values</c>).</summary>
    Inline,

    /// <summary>An Argo CD <c>helm.parameters</c> entry, passed like <c>--set</c>.</summary>
    Parameter,

    /// <summary>A Flux <c>valuesFrom</c> ConfigMap key.</summary>
    ConfigMap,

    /// <summary>A Flux <c>valuesFrom</c> Secret key. Its values are sensitive.</summary>
    Secret,
}

/// <summary>One values layer a GitOps object adds over the chart and its values files.</summary>
/// <param name="Name">The field it comes from, for example <c>helm.values</c>, <c>helm.parameters</c> or <c>valuesFrom ConfigMap orders-values, key values.yaml</c>.</param>
/// <param name="Kind">Where the values come from.</param>
/// <param name="Line">The line in the GitOps manifest.</param>
public sealed record GitOpsValuesLayer(string Name, GitOpsValuesKind Kind, int Line)
{
    /// <summary>The values path a parameter or a <c>valuesFrom</c> with <c>targetPath</c> sets, for example <c>db.timeout</c>.</summary>
    public string? TargetPath { get; init; }

    /// <summary>A parameter's value.</summary>
    public string? Value { get; init; }

    /// <summary>True for an Argo CD parameter with <c>forceString: true</c> (<c>--set-string</c>).</summary>
    public bool ForceString { get; init; }

    /// <summary>The ConfigMap or Secret a <c>valuesFrom</c> entry reads.</summary>
    public string? ObjectName { get; init; }

    /// <summary>The key a <c>valuesFrom</c> entry reads (<c>valuesKey</c>, default <c>values.yaml</c>).</summary>
    public string? Key { get; init; }

    /// <summary>True when a missing <c>valuesFrom</c> object is not an error.</summary>
    public bool Optional { get; init; }

    internal YamlNode? Node { get; init; }

    internal string? Text { get; init; }

    internal int LineOffset { get; init; }
}

/// <summary>The kustomize overrides of an Argo CD source.</summary>
public sealed record GitOpsKustomize
{
    /// <summary>The <c>namePrefix</c> added to every resource name.</summary>
    public string? NamePrefix { get; init; }

    /// <summary>The <c>nameSuffix</c> added to every resource name.</summary>
    public string? NameSuffix { get; init; }

    /// <summary>The namespace every resource is moved to.</summary>
    public string? Namespace { get; init; }

    /// <summary>The image overrides, as written, for example <c>orders=registry/orders:1.4</c>.</summary>
    public IReadOnlyList<string> Images { get; init; } = [];

    /// <summary>The labels added to every resource.</summary>
    public IReadOnlyDictionary<string, string> CommonLabels { get; init; } = new Dictionary<string, string>();

    /// <summary>The annotations added to every resource.</summary>
    public IReadOnlyDictionary<string, string> CommonAnnotations { get; init; } = new Dictionary<string, string>();

    /// <summary>The patches, in order.</summary>
    public IReadOnlyList<GitOpsPatch> Patches { get; init; } = [];
}

/// <summary>A kustomize patch on an Argo CD source.</summary>
/// <param name="TargetKind">The kind of resource it patches, when known.</param>
/// <param name="TargetName">The name of the resource it patches, when known.</param>
/// <param name="File">The patch file (<c>path</c>), for a patch that is not inline. Its content is not read.</param>
/// <param name="Line">The line in the GitOps manifest.</param>
/// <param name="Values">The ConfigMap data keys an inline patch sets or removes.</param>
public sealed record GitOpsPatch(string? TargetKind, string? TargetName, string? File, int Line, IReadOnlyList<GitOpsPatchValue> Values);

/// <summary>A ConfigMap data key that a kustomize patch sets or removes.</summary>
/// <param name="ConfigMap">The ConfigMap name in the manifests in Git, before any <c>namePrefix</c>.</param>
/// <param name="Key">The data key.</param>
/// <param name="Value">The value, or null for a removal.</param>
/// <param name="Line">The line in the GitOps manifest.</param>
/// <param name="IsRemoval">True when the patch removes the key.</param>
public sealed record GitOpsPatchValue(string ConfigMap, string Key, string? Value, int Line, bool IsRemoval);

/// <summary>A Flux <c>postBuild.substitute</c> variable.</summary>
/// <param name="Name">The variable name, for example <c>DB_TIMEOUT</c>.</param>
/// <param name="Value">The value.</param>
/// <param name="Line">The line in the GitOps manifest.</param>
public sealed record GitOpsSubstitution(string Name, string Value, int Line);

/// <summary>A ConfigMap or Secret a Flux object reads values or variables from.</summary>
/// <param name="IsSecret">True for a Secret.</param>
/// <param name="Name">The object name.</param>
/// <param name="Optional">True when a missing object is not an error.</param>
/// <param name="Line">The line in the GitOps manifest.</param>
public sealed record GitOpsDataReference(bool IsSecret, string Name, bool Optional, int Line);

/// <summary>A Flux <c>postBuild</c> substitution variable, the reference on steps that substitute it.</summary>
/// <param name="Kustomization">The Flux Kustomization name.</param>
/// <param name="Name">The variable name.</param>
public sealed record GitOpsVariableRef(string Kustomization, string Name) : SourceRef
{
    /// <inheritdoc/>
    protected internal override string? ValueName() => Name;
}

/// <summary>A ConfigMap key set by a kustomize patch on an Argo CD Application.</summary>
/// <param name="Application">The Application name.</param>
/// <param name="ConfigMap">The ConfigMap name in Git.</param>
/// <param name="Key">The data key.</param>
public sealed record GitOpsPatchRef(string Application, string ConfigMap, string Key) : SourceRef
{
    /// <inheritdoc/>
    protected internal override string? ValueName() => Key;
}
