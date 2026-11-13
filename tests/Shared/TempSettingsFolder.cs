namespace ConfigFerret.Tests.Shared;

/// <summary>A temporary folder for JSON settings files, deleted on dispose.</summary>
internal sealed class TempSettingsFolder : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("configferret-settings-");

    public string Write(string fileName, string json)
    {
        var path = Path.Join(_dir.FullName, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    public void Dispose() => _dir.Delete(recursive: true);
}
