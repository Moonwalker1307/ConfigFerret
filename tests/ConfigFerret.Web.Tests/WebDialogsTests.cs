using ConfigFerret.Web.Services;

namespace ConfigFerret.Web.Tests;

public sealed class WebDialogsTests
{
    [Fact]
    public async Task A_folder_pick_waits_for_the_dialog()
    {
        using var dialogs = new WebDialogs();
        var changes = 0;
        dialogs.Changed += (_, _) => changes++;

        var pick = dialogs.PickFolderAsync("Open a repository folder");

        Assert.True(dialogs.IsPickingFolder);
        Assert.Equal("Open a repository folder", dialogs.FolderTitle);
        Assert.False(pick.IsCompleted);

        dialogs.CompleteFolderPick("/repos/orders");

        Assert.Equal("/repos/orders", await pick);
        Assert.False(dialogs.IsPickingFolder);
        Assert.Null(dialogs.FolderTitle);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task A_second_pick_cancels_the_first()
    {
        using var dialogs = new WebDialogs();
        var first = dialogs.PickFolderAsync("one");
        var second = dialogs.PickFolderAsync("two");

        Assert.Null(await first);
        dialogs.CompleteFolderPick(null);
        Assert.Null(await second);
    }

    [Fact]
    public async Task Disposing_cancels_an_open_pick()
    {
        Task<string?> pick;
        using (var dialogs = new WebDialogs())
        {
            pick = dialogs.PickFolderAsync("Open");
        }

        Assert.Null(await pick);
    }

    [Fact]
    public async Task An_export_is_written_to_a_private_file_and_downloaded_under_its_name()
    {
        using var dialogs = new WebDialogs();

        var path = await dialogs.SaveFileAsync("Export the report", "../configferret-report.json");
        Assert.NotNull(path);
        await File.WriteAllTextAsync(path, "{}", TestContext.Current.CancellationToken);

        var download = dialogs.TakeDownload();

        Assert.NotNull(download);
        Assert.Equal(path, download.Value.Path);
        Assert.Equal("configferret-report.json", download.Value.Name);
        Assert.Null(dialogs.TakeDownload());
        WebDialogs.TryDelete(path);
    }

    [Fact]
    public async Task Nothing_is_downloaded_when_the_export_wrote_nothing()
    {
        using var dialogs = new WebDialogs();
        await dialogs.SaveFileAsync("Export the report", "configferret-report.txt");

        Assert.Null(dialogs.TakeDownload());
    }

    [Fact]
    public async Task Disposing_deletes_an_export_that_was_never_downloaded()
    {
        string? path;
        using (var dialogs = new WebDialogs())
        {
            path = await dialogs.SaveFileAsync("Export the report", "configferret-report.txt");
            await File.WriteAllTextAsync(path!, "secret", TestContext.Current.CancellationToken);
        }

        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }
}
