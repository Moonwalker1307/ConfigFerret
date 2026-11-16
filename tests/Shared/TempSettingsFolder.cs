namespace ConfigFerret.Tests.Shared;

/// <summary>A temporary folder for settings files, deleted on dispose.</summary>
internal sealed class TempSettingsFolder : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("configferret-settings-");

    public string Write(string fileName, string text)
    {
        var path = Path.Join(_dir.FullName, fileName);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        // On Windows a virus scanner or an indexer can hold a file for a moment after it was written or read, and then
        // the folder can't be deleted. A temporary folder left behind must not fail a test, so give up after a while.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                _dir.Delete(recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 5 || !Directory.Exists(_dir.FullName))
                {
                    return;
                }
            }

            Thread.Sleep(50 << attempt);
        }
    }
}
