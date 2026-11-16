namespace ConfigFerret.Compose;

/// <summary>How to read a Docker Compose project: the project directory, the env files and the variables used for interpolation.</summary>
public sealed class ComposeOptions
{
    /// <summary>The project directory, which relative paths are resolved against. Defaults to the first compose file's folder, like <c>docker compose --project-directory</c>.</summary>
    public string? ProjectDirectory { get; set; }

    /// <summary>
    /// Env files used for <c>${VAR}</c> interpolation in the compose files, lowest priority first, like
    /// <c>docker compose --env-file</c>. When empty, the project directory's <c>.env</c> is used if it exists.
    /// </summary>
    public IList<string> EnvFiles { get; } = [];

    /// <summary>
    /// Variables that stand for the shell: they win over the env files, for interpolation and for an
    /// <c>environment</c> entry that names a variable without a value.
    /// </summary>
    public IDictionary<string, string?> Variables { get; } = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// True to read the real process environment as the shell, under <see cref="Variables"/>. False (the default)
    /// never reads it, so the caller's own variables and tokens stay out of a trace.
    /// </summary>
    public bool UseProcessEnvironment { get; set; }

    /// <summary>
    /// True to read the contents of file-based <c>secrets</c>, so a trace can say which file a secret holds. They are
    /// always marked secret. False (the default) lists each secret and its file without reading it.
    /// </summary>
    public bool ReadSecretFiles { get; set; }

    /// <summary>
    /// True to read files that the compose files name (a config's or secret's <c>file</c>, an <c>env_file</c>, an
    /// <c>extends</c> file, an <c>include</c> and its env files) from anywhere, as docker compose does. False (the
    /// default) reads them, and the project directory's <c>.env</c>, only from in or below the project directory after
    /// following symbolic links, so a compose file from someone else can't pull other files into a trace; the others
    /// are listed in the warnings. An include's <c>project_directory</c> does not widen this. Either way a folder is
    /// not read, and a file of size 0, as a FIFO or a device reports, is taken as empty without opening it, so reading
    /// never waits. The compose files, <see cref="EnvFiles"/> and <see cref="ProjectDirectory"/> passed in are read as
    /// they are.
    /// </summary>
    public bool AllowFilesOutsideProject { get; set; }

    // The most parts of services that one service's extends may bring in, lowered by tests.
    internal int MaxExtendedParts { get; set; } = 10_000;

    // The most parts of services that the extends of all services of one load may bring in, lowered by tests.
    internal int MaxLoadExtendedParts { get; set; } = 1_000_000;

    internal ComposeOptions Copy(string? projectDirectory, IEnumerable<string> envFiles)
    {
        var copy = new ComposeOptions
        {
            ProjectDirectory = projectDirectory,
            UseProcessEnvironment = UseProcessEnvironment,
            ReadSecretFiles = ReadSecretFiles,
            AllowFilesOutsideProject = AllowFilesOutsideProject,
            MaxExtendedParts = MaxExtendedParts,
            MaxLoadExtendedParts = MaxLoadExtendedParts,
        };
        foreach (var file in envFiles)
        {
            copy.EnvFiles.Add(file);
        }

        foreach (var (name, value) in Variables)
        {
            copy.Variables[name] = value;
        }

        return copy;
    }
}
