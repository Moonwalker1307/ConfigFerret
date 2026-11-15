using ConfigFerret.Sources;

namespace ConfigFerret.Compose;

/// <summary>Reads layered dotenv files as an <see cref="IConfigSource"/>, linking environment variables to the lines that set them.</summary>
public sealed class DotEnvSource : IConfigSource
{
    private readonly IReadOnlyDictionary<string, string?>? _variables;

    /// <summary>Creates the source.</summary>
    /// <param name="files">The dotenv files, lowest priority first, for example <c>.env</c> then <c>.env.local</c>.</param>
    /// <param name="variables">Variables such as the shell's, which <c>${VAR}</c> references read first. The process environment is never read.</param>
    public DotEnvSource(IEnumerable<string> files, IReadOnlyDictionary<string, string?>? variables = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        Files = [.. files];
        if (Files.Count == 0)
        {
            throw new ArgumentException("Pass at least one dotenv file.", nameof(files));
        }

        _variables = variables;
    }

    /// <summary>The dotenv files, lowest priority first.</summary>
    public IReadOnlyList<string> Files { get; }

    /// <inheritdoc/>
    public string DisplayName => "dotenv " + string.Join(", ", Files.Select(Path.GetFileName));

    /// <inheritdoc/>
    public Task<LoadedConfigSource> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var layers = DotEnvLayers.Load(Files, _variables);
        var entries = layers.Files
            .SelectMany(f => f.Entries)
            .GroupBy(e => new DotEnvEntryRef(e.Path ?? e.File, e.Name))
            .Select(g => new SourceEntry(g.Key.Name, g.Key, ComposeSteps.IsSensitiveName(g.Key.Name), $"line {g.Last().Line}", g.Last().File));
        return Task.FromResult(new LoadedConfigSource(DisplayName, new DotEnvResolver(layers), entries, layers.Warnings));
    }
}
