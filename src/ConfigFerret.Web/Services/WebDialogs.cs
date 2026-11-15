using ConfigFerret.Desktop.ViewModels;

namespace ConfigFerret.Web.Services;

/// <summary>
/// The desktop app's file and folder pickers, for a browser. A folder is picked from the server's folder root in a
/// dialog on the page. A report is "saved" to a private temporary file, which the page then sends to the browser as a
/// download and deletes.
/// </summary>
public sealed class WebDialogs : IDialogService, IDisposable
{
    private TaskCompletionSource<string?>? _folderPick;
    private string? _exportFolder;

    /// <summary>Raised when the folder dialog opens or closes, so the page can render it.</summary>
    public event EventHandler? Changed;

    /// <summary>True while the page should show the folder dialog.</summary>
    public bool IsPickingFolder => _folderPick is not null;

    /// <summary>The title of the folder dialog.</summary>
    public string? FolderTitle { get; private set; }

    /// <summary>The file the last export was written to, waiting to be downloaded, and the name to download it as.</summary>
    public (string Path, string Name)? PendingDownload { get; private set; }

    /// <inheritdoc />
    public Task<string?> PickFolderAsync(string title)
    {
        _folderPick?.TrySetResult(null);
        _folderPick = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        FolderTitle = title;
        Changed?.Invoke(this, EventArgs.Empty);
        return _folderPick.Task;
    }

    /// <summary>Closes the folder dialog with <paramref name="folder"/>, or null for cancel.</summary>
    public void CompleteFolderPick(string? folder)
    {
        var pick = _folderPick;
        _folderPick = null;
        FolderTitle = null;
        Changed?.Invoke(this, EventArgs.Empty);
        pick?.TrySetResult(folder);
    }

    /// <inheritdoc />
    public Task<string?> SaveFileAsync(string title, string suggestedName)
    {
        DeletePendingDownload();
        _exportFolder ??= Directory.CreateTempSubdirectory("configferret-export-").FullName;
        var name = Path.GetFileName(suggestedName);
        var path = Path.Join(_exportFolder, Guid.NewGuid().ToString("N"));
        PendingDownload = (path, name);
        return Task.FromResult<string?>(path);
    }

    /// <summary>Takes the pending download, if the export wrote it. The caller deletes the file after sending it.</summary>
    public (string Path, string Name)? TakeDownload()
    {
        var pending = PendingDownload;
        PendingDownload = null;
        return pending is { } p && File.Exists(p.Path) ? p : null;
    }

    /// <summary>Deletes an exported file that was never downloaded.</summary>
    public void DeletePendingDownload()
    {
        if (PendingDownload is { } pending)
        {
            TryDelete(pending.Path);
            PendingDownload = null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _folderPick?.TrySetResult(null);
        DeletePendingDownload();
        if (_exportFolder is not null)
        {
            try
            {
                Directory.Delete(_exportFolder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the system's temp cleanup.
            }
        }
    }

    /// <summary>Deletes a file, ignoring failures.</summary>
    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the system's temp cleanup.
        }
    }
}
