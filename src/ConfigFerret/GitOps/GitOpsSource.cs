using ConfigFerret.Kubernetes;
using ConfigFerret.Sources;

namespace ConfigFerret.GitOps;

/// <summary>
/// An Argo CD Application or a Flux HelmRelease or Kustomization read from a manifest file, as an <see cref="IConfigSource"/>.
/// Its entries are the Helm values paths, patched ConfigMap keys and substitution variables the object sets.
/// </summary>
public sealed class GitOpsSource : IConfigSource
{
    private readonly IReadOnlyList<GitOpsKind> _kinds;

    /// <summary>Creates the source. Nothing is read until <see cref="LoadAsync"/>.</summary>
    /// <param name="manifestFile">The YAML file that holds the object.</param>
    /// <param name="name">The object's name, needed when the file holds several.</param>
    /// <param name="options">Where the chart, the repository checkout and the ConfigMaps and Secrets it reads are.</param>
    /// <param name="kinds">The kinds of object to look for. All kinds when null or empty.</param>
    /// <param name="dataFile">A file or folder of ConfigMaps and Secrets to read when loading, added to <see cref="GitOpsOptions.Data"/>.</param>
    public GitOpsSource(string manifestFile, string? name = null, GitOpsOptions? options = null, IReadOnlyList<GitOpsKind>? kinds = null, string? dataFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestFile);
        ManifestFile = manifestFile;
        Name = string.IsNullOrWhiteSpace(name) ? null : name;
        Options = options ?? new GitOpsOptions();
        _kinds = kinds is { Count: > 0 } ? kinds : Enum.GetValues<GitOpsKind>();
        DataFile = string.IsNullOrWhiteSpace(dataFile) ? null : dataFile;
    }

    /// <summary>The file or folder of ConfigMaps and Secrets read when loading, when one was given.</summary>
    public string? DataFile { get; }

    /// <summary>The YAML file that holds the object.</summary>
    public string ManifestFile { get; }

    /// <summary>The object's name, when one was given.</summary>
    public string? Name { get; }

    /// <summary>Where the chart, the repository checkout and the ConfigMaps and Secrets are.</summary>
    public GitOpsOptions Options { get; }

    /// <inheritdoc />
    public string DisplayName => Name is null ? $"GitOps {Path.GetFileName(ManifestFile)}" : $"GitOps {Name}";

    /// <inheritdoc />
    /// <exception cref="FormatException">The manifest is not valid YAML.</exception>
    /// <exception cref="InvalidOperationException">The file has no matching object, or several and no name was given.</exception>
    public Task<LoadedConfigSource> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceName = Path.GetFileName(ManifestFile);
        var all = GitOpsApplication.ParseAll(File.ReadAllText(ManifestFile), sourceName);
        var application = GitOpsApplication.Pick(all, sourceName, Name, a => _kinds.Contains(a.Kind));
        var fileData = DataFile is null ? null : KubernetesManifests.LoadFiles(DataFile);
        var resolver = new GitOpsResolver(application, Options, fileData);
        return Task.FromResult(new LoadedConfigSource(application.DisplayName, resolver, resolver.Entries(), resolver.Warnings));
    }
}

/// <summary>Creates a <see cref="GitOpsSource"/> for an Argo CD Application or ApplicationSet. Id <c>argocd-application</c>.</summary>
public sealed class ArgoCdApplicationSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Id => "argocd-application";

    /// <inheritdoc />
    public string DisplayName => "Argo CD Application";

    /// <inheritdoc />
    public SourceCategory Category => SourceCategory.GitOps;

    /// <inheritdoc />
    public IReadOnlyList<SourceSetting> Settings { get; } = GitOpsProviders.Settings("Application", withChart: true, withData: false);

    /// <inheritdoc />
    public IConfigSource Create(IReadOnlyDictionary<string, string?> values) =>
        GitOpsProviders.Create(values, [GitOpsKind.ArgoCdApplication, GitOpsKind.ArgoCdApplicationSet]);
}

/// <summary>Creates a <see cref="GitOpsSource"/> for a Flux HelmRelease. Id <c>flux-helmrelease</c>.</summary>
public sealed class FluxHelmReleaseSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Id => "flux-helmrelease";

    /// <inheritdoc />
    public string DisplayName => "Flux HelmRelease";

    /// <inheritdoc />
    public SourceCategory Category => SourceCategory.GitOps;

    /// <inheritdoc />
    public IReadOnlyList<SourceSetting> Settings { get; } = GitOpsProviders.Settings("HelmRelease", withChart: true, withData: true);

    /// <inheritdoc />
    public IConfigSource Create(IReadOnlyDictionary<string, string?> values) =>
        GitOpsProviders.Create(values, [GitOpsKind.FluxHelmRelease]);
}

/// <summary>Creates a <see cref="GitOpsSource"/> for a Flux Kustomization and its <c>postBuild</c> substitutions. Id <c>flux-kustomization</c>.</summary>
public sealed class FluxKustomizationSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Id => "flux-kustomization";

    /// <inheritdoc />
    public string DisplayName => "Flux Kustomization";

    /// <inheritdoc />
    public SourceCategory Category => SourceCategory.GitOps;

    /// <inheritdoc />
    public IReadOnlyList<SourceSetting> Settings { get; } = GitOpsProviders.Settings("Kustomization", withChart: false, withData: true);

    /// <inheritdoc />
    public IConfigSource Create(IReadOnlyDictionary<string, string?> values) =>
        GitOpsProviders.Create(values, [GitOpsKind.FluxKustomization]);
}

internal static class GitOpsProviders
{
    public static List<SourceSetting> Settings(string kind, bool withChart, bool withData)
    {
        var settings = new List<SourceSetting>
        {
            new("ManifestFile", "Manifest file", SourceSettingKind.Path, Required: true, Help: $"The YAML file in Git that holds the {kind}."),
            new("Name", "Name", Help: $"The {kind}'s name, needed when the file holds several."),
        };
        if (withChart)
        {
            settings.Add(new("ChartFolder", "Chart folder", SourceSettingKind.Path, Help: "The chart folder, for a chart from a Helm or OCI repository (helm pull --untar)."));
        }

        settings.Add(new("RepositoryRoot", "Repository checkout", SourceSettingKind.Path, Help: "The local checkout of the Git repository the manifests and values files are in."));
        if (withData)
        {
            settings.Add(new("DataFile", "ConfigMaps and Secrets", SourceSettingKind.Path, Help: "A file or folder with the ConfigMaps and Secrets it reads values or variables from."));
        }

        return settings;
    }

    public static GitOpsSource Create(IReadOnlyDictionary<string, string?> values, GitOpsKind[] kinds)
    {
        ArgumentNullException.ThrowIfNull(values);
        var manifest = Get(values, "ManifestFile") ?? throw new ArgumentException("ManifestFile is required.", nameof(values));
        if (!File.Exists(manifest))
        {
            throw new ArgumentException($"The manifest file {manifest} does not exist.", nameof(values));
        }

        var options = new GitOpsOptions
        {
            ChartFolder = Folder(values, "ChartFolder"),
            RepositoryRoot = Folder(values, "RepositoryRoot"),
        };
        var data = Get(values, "DataFile");
        if (data is not null && !File.Exists(data) && !Directory.Exists(data))
        {
            throw new ArgumentException($"DataFile {data} does not exist.", nameof(values));
        }

        return new GitOpsSource(manifest, Get(values, "Name"), options, kinds, data);
    }

    private static string? Get(IReadOnlyDictionary<string, string?> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static string? Folder(IReadOnlyDictionary<string, string?> values, string name)
    {
        var folder = Get(values, name);
        return folder is null || Directory.Exists(folder)
            ? folder
            : throw new ArgumentException($"{name} {folder} does not exist.", nameof(values));
    }
}
