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

    internal ComposeOptions Copy(string? projectDirectory, IEnumerable<string> envFiles)
    {
        var copy = new ComposeOptions
        {
            ProjectDirectory = projectDirectory,
            UseProcessEnvironment = UseProcessEnvironment,
            ReadSecretFiles = ReadSecretFiles,
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
