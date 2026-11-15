using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using ConfigFerret.Desktop.ViewModels;
using ConfigFerret.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ConfigFerret.Web.Components.Pages;

/// <summary>
/// The whole app on one page, drawn from the desktop app's <see cref="MainWindowViewModel"/>. The page re-renders when
/// the view model or any of its lists change, the way the desktop window's bindings update.
/// </summary>
public sealed partial class Home
{
    private readonly List<INotifyCollectionChanged> _collections = [];
    private DotNetObjectReference<Home>? _self;
    private IJSObjectReference? _shortcuts;
    private bool _exportOpen;

    /// <summary>This tab's view model.</summary>
    [Inject]
    public required MainWindowViewModel Vm { get; set; }

    /// <summary>This tab's folder picker and downloads.</summary>
    [Inject]
    public required WebDialogs Dialogs { get; set; }

    /// <summary>The folders on the server that can be opened.</summary>
    [Inject]
    public required FolderBrowser Folders { get; set; }

    /// <summary>Calls into the page's script.</summary>
    [Inject]
    public required IJSRuntime Js { get; set; }

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        Vm.PropertyChanged += OnViewModelChanged;
        Dialogs.Changed += OnDialogsChanged;
        _collections.AddRange(
        [
            Vm.Repositories, Vm.Discoveries, Vm.Apps, Vm.Environments, Vm.ValuesFiles, Vm.GitHubEnvironments, Vm.OctopusProjects,
            Vm.OctopusEnvironments, Vm.Rows, Vm.SecretRows, Vm.Unused, Vm.Problems,
        ]);
        foreach (var collection in _collections)
        {
            collection.CollectionChanged += OnCollectionChanged;
        }

        await Vm.InitializeAsync().ConfigureAwait(true);
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _self = DotNetObjectReference.Create(this);
            try
            {
                _shortcuts = await Js.InvokeAsync<IJSObjectReference>("configFerret.registerShortcuts", _self).ConfigureAwait(true);
            }
            catch (JSDisconnectedException)
            {
                // The tab closed before the first render finished.
            }
        }
    }

    /// <summary>Ctrl+B or Cmd+B: hides or shows the sidebar.</summary>
    [JSInvokable]
    public void ToggleSidebar()
    {
        Vm.ToggleSidebarCommand.Execute(null);
        StateHasChanged();
    }

    /// <summary>Escape: closes the open dialog or menu.</summary>
    [JSInvokable]
    public void Escape()
    {
        if (Dialogs.IsPickingFolder)
        {
            Dialogs.CompleteFolderPick(null);
        }
        else if (Vm.ShowConnections && Vm.IsGitHubConnected)
        {
            Vm.ShowConnections = false;
        }

        _exportOpen = false;
        StateHasChanged();
    }

    private async Task ExportAsync(IAsyncRelayCommand export)
    {
        _exportOpen = false;
        await export.ExecuteAsync(null).ConfigureAwait(true);
        if (Dialogs.TakeDownload() is not { } download)
        {
            return;
        }

        try
        {
            var stream = File.OpenRead(download.Path);
            await using (stream.ConfigureAwait(true))
            {
                using var reference = new DotNetStreamReference(stream);
                await Js.InvokeVoidAsync("configFerret.download", download.Name, reference).ConfigureAwait(true);
            }

            Vm.StatusText = $"Downloaded {download.Name}." + (Vm.RevealSecrets ? " It holds secret values: don't share it." : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or JSException or JSDisconnectedException or TaskCanceledException)
        {
            Vm.StatusText = $"Could not download the report: {ex.Message}";
        }
        finally
        {
            WebDialogs.TryDelete(download.Path);
        }
    }

    private string? Selected(int tab) => Vm.SelectedTab == tab ? "selected" : null;

    private static string N(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void OnDialogsChanged(object? sender, EventArgs e) => Refresh();

    // StateHasChanged only queues a render, so a list filled one item at a time still renders once.
    private void Refresh() => _ = InvokeAsync(StateHasChanged);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Vm.PropertyChanged -= OnViewModelChanged;
        Dialogs.Changed -= OnDialogsChanged;
        foreach (var collection in _collections)
        {
            collection.CollectionChanged -= OnCollectionChanged;
        }

        if (_shortcuts is not null)
        {
            try
            {
                await _shortcuts.InvokeVoidAsync("dispose").ConfigureAwait(false);
                await _shortcuts.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // The tab is gone, and its listener with it.
            }
        }

        _self?.Dispose();
    }
}
