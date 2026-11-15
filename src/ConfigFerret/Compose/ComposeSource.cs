using ConfigFerret.Sources;

namespace ConfigFerret.Compose;

/// <summary>
/// Reads a Docker Compose project as an <see cref="IConfigSource"/>, linking an app's environment variables to the
/// <c>environment</c> entries, <c>env_file</c> lines and <c>.env</c> variables of one service.
/// </summary>
public sealed class ComposeSource : IConfigSource
{
    private readonly ComposeOptions? _options;

    /// <summary>Creates the source.</summary>
    /// <param name="files">The compose files, lowest priority first, for example <c>compose.yaml</c> and <c>compose.override.yaml</c>.</param>
    /// <param name="service">The service the app runs as. Needed only when the project has more than one.</param>
    /// <param name="options">The project directory, env files and variables. The process environment is not read unless the options say so.</param>
    public ComposeSource(IEnumerable<string> files, string? service = null, ComposeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        Files = [.. files];
        if (Files.Count == 0)
        {
            throw new ArgumentException("Pass at least one compose file.", nameof(files));
        }

        Service = service;
        _options = options;
    }

    /// <summary>The compose files, lowest priority first.</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>The service, or null for the only one.</summary>
    public string? Service { get; }

    /// <inheritdoc/>
    public string DisplayName => $"Docker Compose {Path.GetFileName(Files[0])}{(Service is null ? string.Empty : " service " + Service)}";

    /// <inheritdoc/>
    public Task<LoadedConfigSource> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = ComposeProject.Load(Files, _options);
        var resolver = new ComposeResolver(project, Service);
        var diagnostics = new TraceDiagnostics();
        var layers = resolver.ReadLayers(diagnostics);
        var where = $"service {Service ?? (project.ServiceNames.Count > 0 ? project.ServiceNames[0] : null)}";
        var entries = layers
            .Where(l => l.Steps.Count > 0 && l.Steps[^1].Reference is not null)
            .Select(l => new SourceEntry(l.Key, l.Steps[^1].Reference!, l.Steps[^1].IsSensitive, l.Steps[^1].Location, where));
        return Task.FromResult(new LoadedConfigSource(DisplayName, resolver, entries, diagnostics.Warnings));
    }
}
