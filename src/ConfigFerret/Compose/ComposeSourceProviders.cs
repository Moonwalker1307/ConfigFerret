using ConfigFerret.Sources;

namespace ConfigFerret.Compose;

/// <summary>Describes the dotenv source to hosts such as the desktop app and the command-line tool.</summary>
public sealed class DotEnvSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc/>
    public string Id => "dotenv";

    /// <inheritdoc/>
    public string DisplayName => "dotenv files";

    /// <inheritdoc/>
    public SourceCategory Category => SourceCategory.File;

    /// <inheritdoc/>
    public IReadOnlyList<SourceSetting> Settings { get; } =
    [
        new("Files", "Files", SourceSettingKind.Lines, Required: true, Help: "The dotenv files, one per line, lowest priority first, for example .env then .env.local."),
    ];

    /// <inheritdoc/>
    public IConfigSource Create(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new DotEnvSource(ProviderValues.Files(values, "Files", required: true));
    }
}

/// <summary>Describes the Docker Compose source to hosts such as the desktop app and the command-line tool.</summary>
public sealed class ComposeSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc/>
    public string Id => "docker-compose";

    /// <inheritdoc/>
    public string DisplayName => "Docker Compose";

    /// <inheritdoc/>
    public SourceCategory Category => SourceCategory.File;

    /// <inheritdoc/>
    public IReadOnlyList<SourceSetting> Settings { get; } =
    [
        new("Files", "Compose files", SourceSettingKind.Lines, Required: true, Help: "The compose files, one per line, lowest priority first, for example compose.yaml then compose.override.yaml."),
        new("Service", "Service", Help: "The service the app runs as. Leave empty when the project has one service."),
        new("EnvFiles", "Env files", SourceSettingKind.Lines, Help: "Env files for ${VAR} interpolation, one per line, like --env-file. Leave empty to use the project directory's .env."),
        new("ProjectDirectory", "Project directory", SourceSettingKind.Path, Help: "The folder relative paths are resolved against. Leave empty for the first compose file's folder."),
    ];

    /// <inheritdoc/>
    public IConfigSource Create(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var options = new ComposeOptions { ProjectDirectory = ProviderValues.Text(values, "ProjectDirectory") };
        foreach (var file in ProviderValues.Files(values, "EnvFiles", required: false))
        {
            options.EnvFiles.Add(file);
        }

        return new ComposeSource(ProviderValues.Files(values, "Files", required: true), ProviderValues.Text(values, "Service"), options);
    }
}

internal static class ProviderValues
{
    public static string? Text(IReadOnlyDictionary<string, string?> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    public static List<string> Files(IReadOnlyDictionary<string, string?> values, string name, bool required)
    {
        var files = (Text(values, name) ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        if (files.Count == 0 && required)
        {
            throw new ArgumentException($"{name} is required.", nameof(values));
        }

        return files;
    }
}
