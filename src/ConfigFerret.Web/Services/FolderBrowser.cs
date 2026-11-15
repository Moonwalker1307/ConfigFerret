namespace ConfigFerret.Web.Services;

/// <summary>
/// Lists the folders under one root folder, for the "Open a folder" dialog. It never lists or returns a path outside
/// the root, and it skips links, so a link in a mounted repository can't lead the browser elsewhere on the server.
/// </summary>
public sealed class FolderBrowser
{
    /// <summary>Creates a browser for <paramref name="root"/>. An empty root, or one that doesn't exist, allows nothing.</summary>
    public FolderBrowser(string? root)
    {
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
    }

    /// <summary>The root folder, or null when folders can't be opened.</summary>
    public string? Root { get; }

    /// <summary>True when there is a root to browse.</summary>
    public bool IsEnabled => Root is not null;

    /// <summary>
    /// Resolves <paramref name="relative"/> (for example <c>team/orders</c>; empty for the root) to a full path under the
    /// root. Null when it leaves the root, passes through a link, or doesn't exist.
    /// </summary>
    public string? Resolve(string? relative)
    {
        if (Root is null)
        {
            return null;
        }

        var path = Root;
        foreach (var part in (relative ?? string.Empty).Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or ".." || part.Contains(':', StringComparison.Ordinal))
            {
                return null;
            }

            path = Path.Join(path, part);
            var info = new DirectoryInfo(path);
            if (!info.Exists || info.LinkTarget is not null)
            {
                return null;
            }
        }

        return path;
    }

    /// <summary>The path of <paramref name="fullPath"/> relative to the root, with forward slashes.</summary>
    public string Relative(string fullPath)
    {
        if (Root is null)
        {
            return string.Empty;
        }

        var relative = Path.GetRelativePath(Root, fullPath).Replace('\\', '/');
        return relative == "." ? string.Empty : relative;
    }

    /// <summary>The folders directly inside <paramref name="relative"/>, by name, without links and hidden folders.</summary>
    public IReadOnlyList<string> List(string? relative)
    {
        if (Resolve(relative) is not { } folder)
        {
            return [];
        }

        try
        {
            return new DirectoryInfo(folder)
                .EnumerateDirectories()
                .Where(d => d.LinkTarget is null && !d.Name.StartsWith('.'))
                .Select(d => d.Name)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
