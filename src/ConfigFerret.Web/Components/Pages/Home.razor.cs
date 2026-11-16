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
    private bool _notesOpen;
    private bool _sourcesOpen;

    private static readonly Tile[] Tiles =
    [
        new(TileFilter.All, "all variables", null, "Show every row: settings, plus GitHub and Octopus variables and Helm values no setting picks up", vm => vm.RowCount),
        new(TileFilter.Overridden, "overridden", null, "Keys set by more than one layer", vm => vm.OverriddenCount),
        new(TileFilter.Upstream, "from upstream", null, "Values that come from further upstream than the app, such as Helm, Kustomize, GitHub or Octopus", vm => vm.UpstreamCount),
        new(TileFilter.Protected, "secrets protected", "good", "Secrets kept in a secret store", vm => vm.ProtectedCount),
        new(TileFilter.Check, "secrets to check", "check", "Secrets that may or may not be safe: check them", vm => vm.CheckCount),
        new(TileFilter.Exposed, "secrets exposed", "bad", "Secrets in plain configuration", vm => vm.ExposedCount),
        new(TileFilter.Dead, "dead variables", "bad", "Settings no code reads, Helm values no template reads, and GitHub or Octopus variables nothing uses", vm => vm.DeadCount),
        new(TileFilter.Workflow, "used by workflows", null, "GitHub variables and secrets a workflow uses directly, while no app setting does", vm => vm.WorkflowOnlyCount, vm => vm.HasWorkflowOnly),
        new(TileFilter.Duplicate, "duplicated names", "check", "Names set in more than one place, such as a secret set for the repository and for an environment, or Db:Host and DB__HOST", vm => vm.DuplicateCount, vm => vm.HasDuplicates),
    ];

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
            Vm.OctopusEnvironments, Vm.Rows, Vm.SecretRows, Vm.Problems, .. Vm.ColumnFilters.Select(c => c.Choices),
        ]);
        foreach (var filter in Vm.ColumnFilters)
        {
            filter.PropertyChanged += OnViewModelChanged;
        }
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
        _notesOpen = false;
        _sourcesOpen = false;
        StateHasChanged();
    }

    private async Task ExportAsync(IAsyncRelayCommand export)
    {
        _exportOpen = false;
        await export.ExecuteAsync(null).ConfigureAwait(true);
        var holdsSecrets = Vm.LastExportHoldsSecrets;
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

            Vm.StatusText = $"Downloaded {download.Name}." + (holdsSecrets ? " It holds secret values: don't share it." : string.Empty);
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
        foreach (var filter in Vm.ColumnFilters)
        {
            filter.PropertyChanged -= OnViewModelChanged;
        }

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

/// <summary>A stat tile and the filter it sets.</summary>
internal sealed record Tile(TileFilter Filter, string Label, string? Css, string Tip, Func<MainWindowViewModel, int> Count, Func<MainWindowViewModel, bool>? Shown = null)
{
    /// <summary>True when the tile is shown.</summary>
    public bool Visible(MainWindowViewModel vm) => Shown?.Invoke(vm) ?? true;
}
