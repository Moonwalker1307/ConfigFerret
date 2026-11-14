namespace ConfigFerret.Desktop.Tests;

/// <summary>A temporary folder, deleted on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("configferret-test-");

    public string Path => _dir.FullName;

    public void Dispose() => _dir.Delete(recursive: true);
}
