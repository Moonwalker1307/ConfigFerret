using ConfigFerret.Tests.Shared;

namespace ConfigFerret.Aws.Tests;

/// <summary>Deleting a temporary folder doesn't fail a test because a file in it is held for a moment.</summary>
public class TempSettingsFolderTests
{
    [Fact]
    public void Waits_for_a_file_that_is_held_for_a_moment()
    {
        var folder = new TempSettingsFolder();
        var path = folder.Write("held.txt", "text");
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Windows doesn't delete a file that is open like this until it is closed.
        var release = new Thread(() =>
        {
            Thread.Sleep(100);
            held.Dispose();
        });
        release.Start();

        folder.Dispose();
        release.Join();

        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void Gives_up_quietly_on_a_file_that_stays_held()
    {
        var folder = new TempSettingsFolder();
        var path = folder.Write("held.txt", "text");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            folder.Dispose();
        }

        // On Windows the folder is still there and nothing holds it now, so remove it.
        folder.Dispose();
    }
}
