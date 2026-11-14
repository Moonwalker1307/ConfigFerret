namespace ConfigFerret.Desktop.Tests;

/// <summary>A temporary folder, deleted on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("configferret-test-");

    public string Path => _dir.FullName;

    public string Full(string relativePath) => System.IO.Path.GetFullPath(System.IO.Path.Join(Path, relativePath));

    public void Write(string relativePath, string text)
    {
        var file = Full(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    public void Dispose() => _dir.Delete(recursive: true);
}
