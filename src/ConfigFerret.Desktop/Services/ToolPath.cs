namespace ConfigFerret.Desktop.Services;

/// <summary>
/// Finds a tool such as <c>helm</c> or <c>secret-tool</c> in the folders on PATH, and nowhere else. Given a bare name,
/// <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/> looks in the app's own folder
/// and the current directory before PATH, so the folder the app was started from, such as a cloned repository, could
/// supply its own <c>helm</c> and have it run with the user's rights. Start the path this returns instead.
/// </summary>
internal static class ToolPath
{
    /// <summary>The full path of <paramref name="tool"/> in the first folder on this process's PATH that has it, or null when none does.</summary>
    /// <param name="tool">A name such as <c>helm</c>, or a path, which is returned as a full path whether it exists or not.</param>
    public static string? Find(string tool) => Find(tool, Environment.GetEnvironmentVariable("PATH"));

    /// <summary>The full path of <paramref name="tool"/> in the first folder on <paramref name="path"/> that has it, or null when none does.</summary>
    internal static string? Find(string tool, string? path)
    {
        ArgumentException.ThrowIfNullOrEmpty(tool);
        if (Path.GetFileName(tool) != tool)
        {
            return Path.GetFullPath(tool);
        }

        // Only .exe, which is what Windows adds to a name without an extension. A .cmd or .bat runs through cmd.exe,
        // which reads its own meaning into the arguments, such as an & in a repository's folder name.
        var file = OperatingSystem.IsWindows() && !Path.HasExtension(tool) ? tool + ".exe" : tool;
        foreach (var entry in (path ?? string.Empty).Split(Path.PathSeparator))
        {
            // An empty or relative entry is a folder under the current directory; an empty one is the current directory.
            var folder = OperatingSystem.IsWindows() ? entry.Trim('"') : entry;
            if (Path.IsPathFullyQualified(folder) && IsExecutable(Path.Join(folder, file)))
            {
                return Path.Join(folder, file);
            }
        }

        return null;
    }

    // A file, not a folder, that may be run: on Unix one with an execute permission bit set.
    private static bool IsExecutable(string file)
    {
        if (!File.Exists(file))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            return (File.GetUnixFileMode(file) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
