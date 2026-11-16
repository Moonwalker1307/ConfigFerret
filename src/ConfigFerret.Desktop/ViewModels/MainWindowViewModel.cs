using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConfigFerret.Desktop.Credentials;
using ConfigFerret.Desktop.Model;
using ConfigFerret.Desktop.Services;
using ConfigFerret.Kustomize;

namespace ConfigFerret.Desktop.ViewModels;

/// <summary>File and folder pickers, provided by the window.</summary>
public interface IDialogService
{
    /// <summary>Asks for a folder. Null when cancelled.</summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>Asks where to save a file. Null when cancelled.</summary>
    Task<string?> SaveFileAsync(string title, string suggestedName);
}

/// <summary>
/// The main window: the repositories the GitHub token can read on the left; for the selected one, what it holds, the
/// merged configuration with each value's history, possibly unused values and variables, and a secrets audit.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    /// <summary>The credential store account for the GitHub token.</summary>
    public const string GitHubAccountName = "github/account";

    /// <summary>The credential store account for the Octopus API key.</summary>
    public const string OctopusAccountName = "octopus/account";

    private const string None = "(none)";
    private const string AnyEnvironment = "(any)";

    private readonly WorkspaceStore _store;
    private readonly ICredentialStore _credentials;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TraceRunner _runner;
    private readonly RepositoryAnalyzer _analyzer;
    private readonly Workspace _workspace;
    private readonly Func<HttpClient>? _traceHttp;
    private readonly bool _isSharedServer;
    private readonly string? _octopusServerFallback;

    // Where the addresses the last Connect checked are kept: the workspace, or on a shared server this view model only.
    private readonly Workspace _addresses;
    private readonly bool _ownsDownloadRoot;
    private bool _disposed;
    private string? _downloadRoot;
    private List<RepositoryRow> _allRepositories = [];
    private List<SettingRow> _allRows = [];
    private RepositoryScan? _scan;
    private string? _currentKey;
    private string? _currentDownload;
    private TraceResult? _lastResult;

    // Whether the result on show was traced with secret values shown, which can differ from the switch: a trace after
    // the switch changed may fail or not yet be done.
    private bool _lastResultRevealed;

    // The setting selected when the switch went off cleared the result, for the masked result to select again.
    private string? _selectedKeyBeforeMasking;

    private bool _populating;
    private bool _clearing;
    private CancellationTokenSource? _work;
    private CancellationTokenSource? _repositoryLoad;

    // Counts the repositories and folders opened. An open that finds a newer one started when it resumes stops there, so
    // the one picked last is the one shown even when an earlier one was still scanning.
    private int _opening;

    // Counts the traces started, so one that a newer trace stopped leaves the view to the newer one.
    private int _tracing;

    /// <summary>Creates the view model and loads the saved workspace.</summary>
    /// <param name="store">Where the workspace is saved.</param>
    /// <param name="credentials">Where tokens and API keys are kept.</param>
    /// <param name="httpHandler">The HTTP handler for GitHub and Octopus; null for the default. For tests.</param>
    /// <param name="analyzer">Builds the trace from a repository; null for the default. For tests.</param>
    /// <param name="downloadRoot">Where repositories are downloaded; null for a folder under the temp folder.</param>
    public MainWindowViewModel(
        WorkspaceStore store,
        ICredentialStore credentials,
        HttpMessageHandler? httpHandler = null,
        RepositoryAnalyzer? analyzer = null,
        string? downloadRoot = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _ownsHttp = true;
        _http = httpHandler is null ? new HttpClient(CreateHttpHandler(), disposeHandler: true) : new HttpClient(httpHandler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(5);
        // A GitHub or Octopus answer is buffered up to the packages' limit; the repository download streams and has its own.
        _http.MaxResponseContentBufferSize = ConfigFerret.Shared.JsonApi.MaxResponseBytes;
        _traceHttp = httpHandler is null ? null : () => new HttpClient(httpHandler, disposeHandler: false);
        _runner = new TraceRunner(credentials, httpClientFactory: _traceHttp);
        _analyzer = analyzer ?? new RepositoryAnalyzer();
        _downloadRoot = downloadRoot;
        _ownsDownloadRoot = downloadRoot is null;

        try
        {
            _workspace = store.Load();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _workspace = new Workspace();
            StatusText = $"Could not read {store.Path}: {ex.Message}. Started fresh; the file is replaced when settings change.";
        }

        _addresses = _workspace;
        GitHubApiUrl = _workspace.GitHubApiUrl;
        OctopusServer = _workspace.OctopusServer;
        OctopusSpace = _workspace.OctopusSpace;
        IsDarkTheme = _workspace.DarkTheme;
        IsSidebarOpen = !_workspace.SidebarHidden;
        KeyFilter = new ColumnFilter(TableColumn.Key, "KEY", "Sections", r => [r.Section], r => [r.Key, r.Key.Replace(":", "__", StringComparison.Ordinal)], OnColumnFilterChanged, SortBy);
        ValueFilter = new ColumnFilter(TableColumn.Value, "VALUE", "Values", r => [r.Value], r => [r.Value], OnColumnFilterChanged, SortBy);
        ComesFromFilter = new ColumnFilter(TableColumn.ComesFrom, "COMES FROM", "Sources", r => [r.ComesFrom], r => SettingRow.AllSteps(r.Setting.Steps).Select(s => s.Source), OnColumnFilterChanged, SortBy);
        LayersFilter = new ColumnFilter(TableColumn.Layers, "LAYERS", "Layers", r => r.AllKinds.Select(LayerKinds.ShortName), r => r.AllKinds.Select(LayerKinds.ShortName), OnColumnFilterChanged, SortBy);
        ColumnFilters = [KeyFilter, ValueFilter, ComesFromFilter, LayersFilter];
        IsOctopusConnected = OctopusServer.Length > 0 && HasCredential(OctopusAccountName);
    }

    /// <summary>The Octopus server to use when none is saved, for example from OCTOPUS_URL.</summary>
    public string? OctopusServerFallback
    {
        get => null;
        init
        {
            _octopusServerFallback = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_octopusServerFallback is not null && (OctopusServer.Length == 0 || _isSharedServer))
            {
                OctopusServer = _octopusServerFallback;
                _addresses.OctopusServer = OctopusServer;
                IsOctopusConnected = HasCredential(OctopusAccountName);
            }
        }
    }

    /// <summary>
    /// True when the app runs on a server for other people, as the web app does. The credentials an
    /// <see cref="EnvironmentFallbackCredentialStore"/> reads from the environment (GITHUB_TOKEN, OCTOPUS_API_KEY) then
    /// belong to whoever runs the server, not to the person using the page: the GitHub token is sent only to
    /// api.github.com and the Octopus key only to <see cref="OctopusServerFallback"/>, and a person who names another
    /// address has to connect with their own. The connection addresses stay in this view model instead of the
    /// workspace file, which everyone using the server shares. False in the desktop app, where the environment and the
    /// workspace file are the user's own.
    /// </summary>
    internal bool IsSharedServer
    {
        get => _isSharedServer;
        init
        {
            _isSharedServer = value;
            if (value)
            {
                _addresses = new Workspace { OctopusServer = _octopusServerFallback ?? string.Empty };

                // A workspace file saved before addresses stayed in the tab may still hold some; the next save drops them.
                _workspace.GitHubApiUrl = _workspace.OctopusServer = _workspace.OctopusSpace = string.Empty;
                GitHubApiUrl = string.Empty;
                OctopusServer = _addresses.OctopusServer;
                OctopusSpace = string.Empty;
                IsOctopusConnected = OctopusServer.Length > 0 && HasCredential(OctopusAccountName);
            }
        }
    }

    /// <summary>Picks folders and files. Set by the window.</summary>
    public IDialogService? Dialogs { get; set; }

    /// <summary>The name of the credential store, shown in the connections panel.</summary>
    public string CredentialStoreName => _credentials.DisplayName;

    // ---- Connections -------------------------------------------------------------------------------------------

    /// <summary>The connections panel is open.</summary>
    [ObservableProperty]
    public partial bool ShowConnections { get; set; }

    /// <summary>The signed-in GitHub user, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGitHubConnected), nameof(GitHubStatus), nameof(ShowConnectPrompt))]
    public partial string? GitHubLogin { get; set; }

    /// <summary>True when a GitHub token works.</summary>
    public bool IsGitHubConnected => GitHubLogin is not null;

    /// <summary>A line about the GitHub connection.</summary>
    public string GitHubStatus => GitHubLogin is null ? "Not connected" : $"Signed in as {GitHubLogin}";

    /// <summary>A GitHub token typed in but not yet saved. Never written to the workspace file.</summary>
    [ObservableProperty]
    public partial string GitHubTokenInput { get; set; } = string.Empty;

    /// <summary>The GitHub API address. Empty for github.com.</summary>
    [ObservableProperty]
    public partial string GitHubApiUrl { get; set; } = string.Empty;

    /// <summary>True when Octopus is connected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OctopusStatus))]
    public partial bool IsOctopusConnected { get; set; }

    /// <summary>A line about the Octopus connection.</summary>
    public string OctopusStatus => IsOctopusConnected ? $"Connected to {OctopusServer}" : "Not connected";

    /// <summary>The Octopus server address.</summary>
    [ObservableProperty]
    public partial string OctopusServer { get; set; } = string.Empty;

    /// <summary>The Octopus space. Empty for the default.</summary>
    [ObservableProperty]
    public partial string OctopusSpace { get; set; } = string.Empty;

    /// <summary>An Octopus API key typed in but not yet saved. Never written to the workspace file.</summary>
    [ObservableProperty]
    public partial string OctopusKeyInput { get; set; } = string.Empty;

    /// <summary>The last connection error, shown in the connections panel.</summary>
    [ObservableProperty]
    public partial string? ConnectionError { get; set; }

    // ---- Repositories ------------------------------------------------------------------------------------------

    /// <summary>The repositories that match the search.</summary>
    public ObservableCollection<RepositoryRow> Repositories { get; } = [];

    /// <summary>Search text for the repository list.</summary>
    [ObservableProperty]
    public partial string? RepositoryFilter { get; set; }

    /// <summary>The selected repository. Selecting one analyses it.</summary>
    [ObservableProperty]
    public partial RepositoryRow? SelectedRepository { get; set; }

    /// <summary>A line like "42 repositories".</summary>
    [ObservableProperty]
    public partial string? RepositoryCountText { get; set; }

    /// <summary>True while the repository list loads.</summary>
    [ObservableProperty]
    public partial bool IsLoadingRepositories { get; set; }

    /// <summary>True when GitHub isn't connected yet, so the sidebar asks for it.</summary>
    public bool ShowConnectPrompt => !IsGitHubConnected;

    // ---- Analysis ----------------------------------------------------------------------------------------------

    /// <summary>The repository or folder being shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubject), nameof(ShowWelcome))]
    public partial string? SubjectTitle { get; set; }

    /// <summary>A line under the title, such as the branch or the folder path.</summary>
    [ObservableProperty]
    public partial string? SubjectDetail { get; set; }

    /// <summary>True when a repository or folder is open.</summary>
    public bool HasSubject => SubjectTitle is not null;

    /// <summary>True when nothing is open, to show the welcome panel.</summary>
    public bool ShowWelcome => SubjectTitle is null;

    /// <summary>True while downloading, scanning or tracing.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(OpenFolderCommand), nameof(ExportJsonCommand), nameof(ExportTextCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>What is running, for the busy overlay.</summary>
    [ObservableProperty]
    public partial string? BusyText { get; set; }

    /// <summary>True once a trace has finished.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportJsonCommand), nameof(ExportTextCommand))]
    public partial bool HasResult { get; set; }

    /// <summary>What the scan found.</summary>
    public ObservableCollection<SourceToggle> Discoveries { get; } = [];

    /// <summary>A label for the Sources drop-down, such as "Sources 4 of 5".</summary>
    [ObservableProperty]
    public partial string SourcesText { get; set; } = "Sources";

    /// <summary>The app folders found.</summary>
    public ObservableCollection<string> Apps { get; } = [];

    /// <summary>The chosen app folder.</summary>
    [ObservableProperty]
    public partial string? SelectedApp { get; set; }

    /// <summary>The app's environments.</summary>
    public ObservableCollection<string> Environments { get; } = [];

    /// <summary>The chosen environment.</summary>
    [ObservableProperty]
    public partial string? SelectedEnvironment { get; set; }

    /// <summary>The Helm values files, with (none).</summary>
    public ObservableCollection<string> ValuesFiles { get; } = [];

    /// <summary>The chosen values file.</summary>
    [ObservableProperty]
    public partial string? SelectedValuesFile { get; set; }

    /// <summary>True when the repository has a Helm chart.</summary>
    [ObservableProperty]
    public partial bool HasHelm { get; set; }

    /// <summary>The GitHub deployment environments, with (none).</summary>
    public ObservableCollection<string> GitHubEnvironments { get; } = [];

    /// <summary>The chosen GitHub environment.</summary>
    [ObservableProperty]
    public partial string? SelectedGitHubEnvironment { get; set; }

    /// <summary>True when a GitHub repository (not a local folder) is open.</summary>
    [ObservableProperty]
    public partial bool IsGitHubSubject { get; set; }

    /// <summary>The Octopus projects, with (none).</summary>
    public ObservableCollection<string> OctopusProjects { get; } = [];

    /// <summary>The chosen Octopus project.</summary>
    [ObservableProperty]
    public partial string? SelectedOctopusProject { get; set; }

    /// <summary>The Octopus environments, with (any).</summary>
    public ObservableCollection<string> OctopusEnvironments { get; } = [];

    /// <summary>The chosen Octopus environment.</summary>
    [ObservableProperty]
    public partial string? SelectedOctopusEnvironment { get; set; }

    // ---- Results -----------------------------------------------------------------------------------------------

    /// <summary>The variables that match the filters.</summary>
    public ObservableCollection<SettingRow> Rows { get; } = [];

    /// <summary>The selected variable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(Details), nameof(SelectedSecret))]
    public partial SettingRow? SelectedRow { get; set; }

    /// <summary>True when a variable is selected.</summary>
    public bool HasSelection => SelectedRow is not null;

    /// <summary>The history of the selected variable, as a tree.</summary>
    public IReadOnlyList<StepNode> Details => SelectedRow?.Setting.Steps.Select(s => new StepNode(s)).ToList() ?? [];

    /// <summary>The secrets audit of the selected variable, when it looks like a secret.</summary>
    public SecretRow? SelectedSecret => SelectedRow is null ? null : SecretRows.FirstOrDefault(s => string.Equals(s.Key, SelectedRow.Key, StringComparison.Ordinal));

    /// <summary>The secrets audit, red first.</summary>
    public ObservableCollection<SecretRow> SecretRows { get; } = [];

    /// <summary>Variables nothing reads: settings no code reads, Helm values no template reads, GitHub variables no setting or workflow uses, and Octopus variables no setting uses.</summary>
    [ObservableProperty]
    public partial int DeadCount { get; set; }

    /// <summary>GitHub variables a workflow uses directly while no setting does, such as a deploy action's publish profile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkflowOnly))]
    public partial int WorkflowOnlyCount { get; set; }

    /// <summary>Rows whose name another row also has, such as a secret set for the repository and for an environment.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDuplicates))]
    public partial int DuplicateCount { get; set; }

    /// <summary>True when some names appear in more than one row, to show their tile.</summary>
    public bool HasDuplicates => DuplicateCount > 0;

    /// <summary>The duplicated-names tile is the active filter.</summary>
    public bool IsDuplicateTile => ActiveTile == TileFilter.Duplicate;

    /// <summary>True when some GitHub variables are used only by workflows, to show their tile.</summary>
    public bool HasWorkflowOnly => WorkflowOnlyCount > 0;

    /// <summary>What the dead-variable scan read, such as "Read 12 C# files".</summary>
    [ObservableProperty]
    public partial string? DeadScanText { get; set; }

    /// <summary>Warnings and source problems from the last trace.</summary>
    public ObservableCollection<ProblemRow> Problems { get; } = [];

    /// <summary>Search text: matches keys, values and source names.</summary>
    [ObservableProperty]
    public partial string? FilterText { get; set; }

    /// <summary>The stat tile used as a filter. <see cref="TileFilter.All"/> shows every row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllTile), nameof(IsOverriddenTile), nameof(IsUpstreamTile), nameof(IsProtectedTile), nameof(IsCheckTile), nameof(IsExposedTile), nameof(IsDeadTile), nameof(IsWorkflowTile), nameof(IsDuplicateTile))]
    public partial TileFilter ActiveTile { get; set; }

    /// <summary>The "all" tile is the active filter.</summary>
    public bool IsAllTile => ActiveTile == TileFilter.All;

    /// <summary>The overridden tile is the active filter.</summary>
    public bool IsOverriddenTile => ActiveTile == TileFilter.Overridden;

    /// <summary>The from-upstream tile is the active filter.</summary>
    public bool IsUpstreamTile => ActiveTile == TileFilter.Upstream;

    /// <summary>The secrets-protected tile is the active filter.</summary>
    public bool IsProtectedTile => ActiveTile == TileFilter.Protected;

    /// <summary>The secrets-to-check tile is the active filter.</summary>
    public bool IsCheckTile => ActiveTile == TileFilter.Check;

    /// <summary>The secrets-exposed tile is the active filter.</summary>
    public bool IsExposedTile => ActiveTile == TileFilter.Exposed;

    /// <summary>The dead tile is the active filter.</summary>
    public bool IsDeadTile => ActiveTile == TileFilter.Dead;

    /// <summary>The used-by-workflows tile is the active filter.</summary>
    public bool IsWorkflowTile => ActiveTile == TileFilter.Workflow;

    /// <summary>The filter behind the KEY header: sections to tick, and a search in the key.</summary>
    public ColumnFilter KeyFilter { get; }

    /// <summary>The filter behind the VALUE header.</summary>
    public ColumnFilter ValueFilter { get; }

    /// <summary>The filter behind the COMES FROM header.</summary>
    public ColumnFilter ComesFromFilter { get; }

    /// <summary>The filter behind the LAYERS header.</summary>
    public ColumnFilter LayersFilter { get; }

    /// <summary>Every column filter, in column order.</summary>
    public IReadOnlyList<ColumnFilter> ColumnFilters { get; }

    /// <summary>The column the table is sorted by, or null for the trace's order.</summary>
    [ObservableProperty]
    public partial TableColumn? SortColumn { get; set; }

    /// <summary>True when sorted Z to A.</summary>
    [ObservableProperty]
    public partial bool SortDescending { get; set; }

    /// <summary>True when any filter (search, tile or column) hides rows.</summary>
    public bool IsFiltered => !string.IsNullOrWhiteSpace(FilterText) || ActiveTile != TileFilter.All || ColumnFilters.Any(c => c.IsActive);

    /// <summary>True when there is a result but the filters hide every row.</summary>
    public bool ShowNoMatches => HasResult && Rows.Count == 0 && _allRows.Count > 0;

    /// <summary>Shows secret values instead of ***. Traces again when changed.</summary>
    [ObservableProperty]
    public partial bool RevealSecrets { get; set; }

    /// <summary>
    /// True when the last export wrote secret values in clear. It is the export's own, so it holds even when the switch
    /// went off while the file was written or sent.
    /// </summary>
    public bool LastExportHoldsSecrets { get; private set; }

    /// <summary>A line about the last action.</summary>
    [ObservableProperty]
    public partial string? StatusText { get; set; }

    /// <summary>The dark theme is on.</summary>
    [ObservableProperty]
    public partial bool IsDarkTheme { get; set; } = true;

    /// <summary>The repository sidebar is shown. Hiding it gives the results the whole window.</summary>
    [ObservableProperty]
    public partial bool IsSidebarOpen { get; set; } = true;

    /// <summary>How many settings the trace found.</summary>
    [ObservableProperty]
    public partial int TotalCount { get; set; }

    /// <summary>How many rows the table holds: the settings, plus GitHub and Octopus variables and Helm values no setting picks up.</summary>
    [ObservableProperty]
    public partial int RowCount { get; set; }

    /// <summary>How many variables are set by more than one layer.</summary>
    [ObservableProperty]
    public partial int OverriddenCount { get; set; }

    /// <summary>How many variables come from further upstream than the app.</summary>
    [ObservableProperty]
    public partial int UpstreamCount { get; set; }

    /// <summary>Secrets kept in a secret store.</summary>
    [ObservableProperty]
    public partial int ProtectedCount { get; set; }

    /// <summary>Secrets to check.</summary>
    [ObservableProperty]
    public partial int CheckCount { get; set; }

    /// <summary>Secrets in plain configuration.</summary>
    [ObservableProperty]
    public partial int ExposedCount { get; set; }

    /// <summary>Warnings and problems.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems), nameof(ProblemText))]
    public partial int ProblemCount { get; set; }

    /// <summary>True when the trace left notes, warnings or source problems.</summary>
    public bool HasProblems => ProblemCount > 0;

    /// <summary>The label on the notes button, such as "3 notes".</summary>
    public string ProblemText => ProblemCount == 1 ? "1 note" : string.Create(CultureInfo.CurrentCulture, $"{ProblemCount} notes");

    /// <summary>A line like "12 of 48 variables".</summary>
    [ObservableProperty]
    public partial string? ShownText { get; set; }

    // ---- Startup -----------------------------------------------------------------------------------------------

    /// <summary>Signs in with the saved GitHub token, if there is one, and loads the repositories.</summary>
    public async Task InitializeAsync()
    {
        if (!HasCredential(GitHubAccountName))
        {
            ShowConnections = true;
            return;
        }

        await LoadRepositoriesAsync().ConfigureAwait(true);
    }

    partial void OnRepositoryFilterChanged(string? value) => ApplyRepositoryFilter();

    partial void OnSelectedRepositoryChanged(RepositoryRow? value)
    {
        if (value is not null && !_populating)
        {
            _ = AnalyzeRepositoryAsync(value);
        }
    }

    partial void OnSelectedAppChanged(string? value)
    {
        if (_populating || _scan is null)
        {
            return;
        }

        RefreshEnvironmentChoices();
        RefreshChartChoices();
        _ = RetraceAsync();
    }

    partial void OnSelectedEnvironmentChanged(string? value) => RetraceOnChoice();

    partial void OnSelectedValuesFileChanged(string? value) => RetraceOnChoice();

    partial void OnSelectedGitHubEnvironmentChanged(string? value) => RetraceOnChoice();

    partial void OnSelectedOctopusProjectChanged(string? value) => RetraceOnChoice();

    partial void OnSelectedOctopusEnvironmentChanged(string? value) => RetraceOnChoice();

    partial void OnFilterTextChanged(string? value) => ApplyFilter();

    partial void OnActiveTileChanged(TileFilter value) => ApplyFilter();

    private void OnColumnFilterChanged(ColumnFilter filter) => ApplyFilter();

    private void SortBy(TableColumn column, bool? descending)
    {
        foreach (var filter in ColumnFilters)
        {
            filter.IsSorted = descending is not null && filter.Column == column;
            filter.IsDescending = filter.IsSorted && descending == true;
        }

        SortColumn = descending is null ? null : column;
        SortDescending = descending == true;
        ApplyFilter();
    }

    partial void OnRevealSecretsChanged(bool value)
    {
        // Secret values leave the view, the secrets panel and the export the moment the switch goes off, rather than when
        // the next trace is done: that trace can fail, be refused or be stopped.
        if (!value && _lastResultRevealed)
        {
            var selected = SelectedRow?.Key;
            ClearResult();
            _selectedKeyBeforeMasking = selected;
            StatusText = null;
        }

        RetraceOnChoice();
    }

    partial void OnIsSidebarOpenChanged(bool value)
    {
        if (_workspace.SidebarHidden != !value)
        {
            _workspace.SidebarHidden = !value;
            Save();
        }
    }

    partial void OnIsDarkThemeChanged(bool value)
    {
        _workspace.DarkTheme = value;
        Save();
    }

    // ---- Commands ----------------------------------------------------------------------------------------------

    /// <summary>Opens or closes the connections panel.</summary>
    [RelayCommand]
    private void ToggleConnections()
    {
        ConnectionError = null;
        ShowConnections = !ShowConnections;
    }

    /// <summary>Checks the typed GitHub token, saves it in the credential store and loads the repositories.</summary>
    [RelayCommand]
    private async Task ConnectGitHubAsync()
    {
        ConnectionError = null;
        var token = GitHubTokenInput.Trim();
        GitHubTokenInput = string.Empty;
        if (token.Length == 0)
        {
            ConnectionError = "Paste a GitHub token first.";
            return;
        }

        try
        {
            var account = CreateGitHub(token);
            GitHubLogin = await account.GetLoginAsync().ConfigureAwait(true);
            _credentials.Write(GitHubAccountName, token);
            _addresses.GitHubApiUrl = GitHubApiUrl.Trim();
            Save();
            ShowConnections = false;
            await LoadRepositoriesAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or CredentialStoreException or ArgumentException or UriFormatException or TaskCanceledException or JsonException)
        {
            GitHubLogin = null;
            ConnectionError = ex.Message;
        }
    }

    /// <summary>Forgets the GitHub token.</summary>
    [RelayCommand]
    private void DisconnectGitHub()
    {
        TryDelete(GitHubAccountName);
        GitHubLogin = null;
        _allRepositories = [];
        ApplyRepositoryFilter();
    }

    /// <summary>Checks the typed Octopus API key, and saves it in the credential store.</summary>
    [RelayCommand]
    private async Task ConnectOctopusAsync()
    {
        ConnectionError = null;
        var key = OctopusKeyInput.Trim();
        OctopusKeyInput = string.Empty;
        if (key.Length == 0 || OctopusServer.Trim().Length == 0)
        {
            ConnectionError = "Enter the Octopus server address and an API key.";
            return;
        }

        try
        {
            var octopus = new OctopusAccount(_http, ParseUrl(OctopusServer.Trim()), key, OctopusSpace);
            var projects = await octopus.ListProjectsAsync().ConfigureAwait(true);
            _credentials.Write(OctopusAccountName, key);
            _addresses.OctopusServer = OctopusServer.Trim();
            _addresses.OctopusSpace = OctopusSpace.Trim();
            Save();
            IsOctopusConnected = true;
            StatusText = $"Connected to Octopus: {projects.Count} projects.";
            if (_scan is not null)
            {
                await LoadOctopusChoicesAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or CredentialStoreException or ArgumentException or UriFormatException or TaskCanceledException or JsonException)
        {
            ConnectionError = ex.Message;
        }
    }

    /// <summary>Forgets the Octopus API key.</summary>
    [RelayCommand]
    private void DisconnectOctopus()
    {
        TryDelete(OctopusAccountName);
        IsOctopusConnected = false;
        OctopusProjects.Clear();
        OctopusEnvironments.Clear();
    }

    /// <summary>Reloads the repository list.</summary>
    [RelayCommand]
    private Task ReloadRepositoriesAsync() => LoadRepositoriesAsync();

    /// <summary>Analyses a folder on this computer, such as a clone.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task OpenFolderAsync()
    {
        if (Dialogs is null || await Dialogs.PickFolderAsync("Open a repository folder").ConfigureAwait(true) is not { } folder)
        {
            return;
        }

        await AnalyzeFolderAsync(folder).ConfigureAwait(true);
    }

    /// <summary>Downloads the repository again and traces it.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task RefreshAsync()
    {
        if (ShownRepository() is { } repository)
        {
            await AnalyzeRepositoryAsync(repository).ConfigureAwait(true);
        }
        else if (!IsGitHubSubject && _currentKey is not null && Directory.Exists(_currentKey))
        {
            await AnalyzeFolderAsync(_currentKey).ConfigureAwait(true);
        }
    }

    /// <summary>Saves the report as JSON.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportJsonAsync() => ExportAsync("configferret-report.json", r => r.ToJson());

    /// <summary>Saves the report as text.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportTextAsync() => ExportAsync("configferret-report.txt", r => r.ToText());

    /// <summary>Switches between the dark and light themes.</summary>
    [RelayCommand]
    private void ToggleTheme() => IsDarkTheme = !IsDarkTheme;

    /// <summary>Hides or shows the repository sidebar.</summary>
    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    /// <summary>Clears the search, the tile, every column filter and the sort.</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        _clearing = true;
        try
        {
            FilterText = null;
            ActiveTile = TileFilter.All;
            foreach (var filter in ColumnFilters)
            {
                filter.Clear();
            }
        }
        finally
        {
            _clearing = false;
        }

        SortBy(TableColumn.Key, null);
    }

    /// <summary>Filters the table by a stat tile: all, overridden, upstream, protected, check, exposed, dead or workflow. Picking the active tile again shows every row.</summary>
    [RelayCommand]
    private void SelectTile(string? tile)
    {
        var picked = Enum.TryParse<TileFilter>(tile, ignoreCase: true, out var value) ? value : TileFilter.All;
        ActiveTile = picked == ActiveTile ? TileFilter.All : picked;
    }

    private bool IsIdle() => !IsBusy;

    private bool CanExport() => HasResult && !IsBusy;

    // ---- Work --------------------------------------------------------------------------------------------------

    internal async Task LoadRepositoriesAsync()
    {
        string? token;
        bool serverToken;
        try
        {
            token = ReadCredential(GitHubAccountName, out serverToken);
        }
        catch (CredentialStoreException ex)
        {
            StatusText = ex.Message;
            return;
        }

        if (string.IsNullOrEmpty(token))
        {
            ShowConnections = true;
            return;
        }

        // A reload, or closing the window or tab, stops the load still running.
        if (_repositoryLoad is not null)
        {
            await _repositoryLoad.CancelAsync().ConfigureAwait(true);
        }

        using var cancellation = new CancellationTokenSource();
        _repositoryLoad = cancellation;
        IsLoadingRepositories = true;
        try
        {
            var account = CreateGitHub(token, serverToken);
            GitHubLogin ??= await account.GetLoginAsync(cancellation.Token).ConfigureAwait(true);
            var repositories = await account.ListRepositoriesAsync(cancellation.Token).ConfigureAwait(true);
            _allRepositories = repositories.Select(r => new RepositoryRow(r)).ToList();
            ApplyRepositoryFilter();
            StatusText = $"Signed in to GitHub as {GitHubLogin}.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The newer load shows its own result.
        }
        catch (Exception ex) when (ex is HttpRequestException or ArgumentException or UriFormatException or TaskCanceledException or JsonException)
        {
            StatusText = ex.Message;
            ConnectionError = ex.Message;
            ShowConnections = true;
        }
        finally
        {
            if (ReferenceEquals(_repositoryLoad, cancellation))
            {
                _repositoryLoad = null;
                IsLoadingRepositories = false;
            }
        }
    }

    internal async Task AnalyzeRepositoryAsync(RepositoryRow repository)
    {
        var opening = ++_opening;
        string? fetched = null;
        var token = await RunAsync($"Downloading {repository.FullName}…", async cancellationToken =>
        {
            var github = CreateGitHub(ReadCredential(GitHubAccountName, out var serverToken) ?? throw new HttpRequestException("Connect GitHub first."), serverToken);
            var environments = await github.ListEnvironmentsAsync(repository.FullName, cancellationToken).ConfigureAwait(true);
            fetched = await github.DownloadAsync(repository.FullName, repository.Info.DefaultBranch, DownloadRoot(), cancellationToken).ConfigureAwait(true);
            return new Download(fetched, environments);
        }, isOpen: true).ConfigureAwait(true);

        if (token is not { } downloaded || opening != _opening)
        {
            EndOpenWithoutScan(opening, fetched);
            return;
        }

        var subject = new Subject(
            repository.FullName,
            $"{repository.Info.DefaultBranch} · {(repository.IsPrivate ? "private" : "public")}{(repository.HasLanguage ? " · " + repository.Language : string.Empty)}",
            IsGitHub: true);
        await OpenScanAsync(repository.FullName, downloaded.Folder, downloaded.Environments, subject, opening, downloaded.Folder).ConfigureAwait(true);
    }

    internal async Task AnalyzeFolderAsync(string folder)
    {
        folder = Path.GetFullPath(folder);
        var opening = ++_opening;
        _populating = true;
        SelectedRepository = null;
        _populating = false;
        _workspace.RecentFolders.Remove(folder);
        _workspace.RecentFolders.Insert(0, folder);
        if (_workspace.RecentFolders.Count > 10)
        {
            _workspace.RecentFolders.RemoveRange(10, _workspace.RecentFolders.Count - 10);
        }

        var subject = new Subject(Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)), folder, IsGitHub: false);
        await OpenScanAsync(folder, folder, [], subject, opening, download: null).ConfigureAwait(true);
    }

    // download is the folder this open downloaded, if any. It is deleted when the open stops, fails or a newer one takes
    // its place. The download of the repository on show goes only when this open takes over, so until then a trace of
    // that repository still finds its files.
    private async Task OpenScanAsync(string key, string folder, IReadOnlyList<string> gitHubEnvironments, Subject subject, int opening, string? download)
    {
        RepositoryScan? scan = null;
        await RunAsync("Looking for configuration…", _ => Task.FromResult(scan = RepositoryScanner.Scan(folder)), isOpen: true).ConfigureAwait(true);
        if (scan is null || opening != _opening)
        {
            EndOpenWithoutScan(opening, download);
            return;
        }

        DeleteDownload();
        _currentDownload = download;
        SubjectTitle = subject.Title;
        SubjectDetail = subject.Detail;
        IsGitHubSubject = subject.IsGitHub;
        _scan = scan;
        _currentKey = key;
        if (!_workspace.Repositories.TryGetValue(key, out var choices))
        {
            choices = new AnalysisChoices();
            _workspace.Repositories[key] = choices;
        }

        RepositoryAnalyzer.SuggestChoices(scan, choices, gitHubEnvironments);

        _populating = true;
        try
        {
            Fill(Apps, scan.Apps.Select(a => a.RelativePath));
            SelectedApp = choices.App;
            RefreshEnvironmentChoices();
            SelectedEnvironment = Environments.FirstOrDefault(e => string.Equals(e, choices.Environment, StringComparison.OrdinalIgnoreCase)) ?? SelectedEnvironment;
            var chart = scan.HelmCharts.FirstOrDefault(c => c.RelativePath == choices.HelmChart);
            HasHelm = chart is not null;
            Fill(ValuesFiles, [None, .. chart?.ValuesFiles ?? []]);
            SelectedValuesFile = choices.HelmValues is { } values && ValuesFiles.Contains(values) ? values : None;
            Fill(GitHubEnvironments, [None, .. gitHubEnvironments]);
            SelectedGitHubEnvironment = choices.GitHubEnvironment ?? None;
        }
        finally
        {
            _populating = false;
        }

        await LoadOctopusChoicesAsync().ConfigureAwait(true);
        if (opening == _opening)
        {
            await RetraceAsync().ConfigureAwait(true);
        }
    }

    // An open that got no download or scan, because it failed, a trace (a changed choice) stopped it or a newer open took
    // its place. Its own download goes. When it was the newest, the view keeps the repository it shows, whose files are
    // still there, and selects that one again, so picking the failed one opens it again and Refresh downloads the one on
    // show; a failure's reason is in the status line.
    private void EndOpenWithoutScan(int opening, string? download)
    {
        DeleteFolder(download);
        if (_disposed)
        {
            // A download stopped by closing can have made the download root again while it unpacked.
            DeleteOwnedDownloadRoot();
        }

        if (opening != _opening)
        {
            return;
        }

        _populating = true;
        SelectedRepository = ShownRepository() is { } shown && Repositories.Contains(shown) ? shown : null;
        _populating = false;
    }

    // The GitHub repository on show, or null when a folder or nothing is.
    private RepositoryRow? ShownRepository() =>
        IsGitHubSubject ? _allRepositories.FirstOrDefault(r => string.Equals(r.FullName, _currentKey, StringComparison.Ordinal)) : null;

    private async Task LoadOctopusChoicesAsync()
    {
        // Another repository can open while Octopus answers; its choices and lists are then left alone.
        var current = _currentKey;
        if (!IsOctopusConnected || current is null || !_workspace.Repositories.TryGetValue(current, out var choices))
        {
            return;
        }

        try
        {
            var key = ReadCredential(OctopusAccountName, out var serverKey);
            if (string.IsNullOrEmpty(key))
            {
                IsOctopusConnected = false;
                return;
            }

            var octopus = new OctopusAccount(_http, OctopusAddress(OctopusServer, serverKey), key, OctopusSpace);
            var projects = await octopus.ListProjectsAsync().ConfigureAwait(true);
            if (!string.Equals(_currentKey, current, StringComparison.Ordinal))
            {
                return;
            }

            var environments = await octopus.ListEnvironmentsAsync().ConfigureAwait(true);
            if (!string.Equals(_currentKey, current, StringComparison.Ordinal))
            {
                return;
            }

            // A project named like the repository is most likely the one that deploys it.
            var repoName = current.Split('/', '\\').Last();
            choices.OctopusProject ??= projects.FirstOrDefault(p => string.Equals(p.Name, repoName, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Slug, repoName, StringComparison.OrdinalIgnoreCase))?.Name;
            choices.OctopusEnvironment ??= environments.FirstOrDefault(e => string.Equals(e, choices.Environment, StringComparison.OrdinalIgnoreCase));

            _populating = true;
            try
            {
                Fill(OctopusProjects, [None, .. projects.Select(p => p.Name)]);
                SelectedOctopusProject = choices.OctopusProject ?? None;
                Fill(OctopusEnvironments, [AnyEnvironment, .. environments]);
                SelectedOctopusEnvironment = choices.OctopusEnvironment ?? AnyEnvironment;
            }
            finally
            {
                _populating = false;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or CredentialStoreException or ArgumentException or UriFormatException or TaskCanceledException or JsonException)
        {
            StatusText = "Octopus: " + ex.Message;
        }
    }

    private void RetraceOnChoice()
    {
        if (!_populating && _scan is not null)
        {
            _ = RetraceAsync();
        }
    }

    internal async Task RetraceAsync()
    {
        if (_scan is not { } scan || _currentKey is null)
        {
            return;
        }

        // Counted before the choices are checked, so a trace still running for earlier choices leaves a refusal alone.
        var tracing = ++_tracing;
        if (RefuseChoices(scan, out var environment, out var valuesFile) is { } refusal)
        {
            ClearResult();
            ShowDiscoveries(scan, null);
            StatusText = refusal;
            Problems.Add(new ProblemRow("error", refusal));
            ProblemCount = Problems.Count;

            // Like any changed choice, a refused one stops the work still running, whose answer would no longer be shown.
            if (_work is not null)
            {
                await _work.CancelAsync().ConfigureAwait(true);
            }

            return;
        }

        var choices = _workspace.Repositories[_currentKey];
        choices.App = SelectedApp;
        choices.Environment = environment;
        choices.HelmValues = valuesFile;
        choices.GitHubEnvironment = SelectedGitHubEnvironment is null or None ? null : SelectedGitHubEnvironment;
        choices.OctopusProject = SelectedOctopusProject is null or None ? null : SelectedOctopusProject;
        choices.OctopusEnvironment = SelectedOctopusEnvironment is null or AnyEnvironment ? null : SelectedOctopusEnvironment;
        Save();

        var connections = new AnalysisConnections(
            IsGitHubSubject ? _currentKey : null,
            _addresses.GitHubApiUrl,
            GitHubAccountName,
            IsOctopusConnected ? _addresses.OctopusServer : string.Empty,
            _addresses.OctopusSpace,
            OctopusAccountName);
        var reveal = RevealSecrets;
        var notes = new List<string>();
        var runner = _runner;
        if (_isSharedServer)
        {
            (connections, runner) = PinConnections(connections, notes);
        }

        Profile? profile = null;
        var result = await RunAsync("Tracing the configuration…", async cancellationToken =>
        {
            profile = _analyzer.BuildProfile(scan, choices, connections, notes, cancellationToken);
            foreach (var source in profile.Sources.Where(s => SourceFamily(s) is { } family && choices.TurnedOff.Contains(family, StringComparer.Ordinal)))
            {
                source.Enabled = false;
            }

            return await runner.RunAsync(profile, reveal, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(true);

        if (tracing != _tracing || !ReferenceEquals(_scan, scan))
        {
            return;
        }

        ShowDiscoveries(scan, profile);
        if (result is not null)
        {
            ShowResult(result, notes, reveal);
        }
    }

    // On a shared server, reads each credential once, before the trace starts, and fixes the address it goes to, so a
    // credential of the server's can't reach an address a visitor named, even if the visitor disconnects meanwhile. A
    // connection whose credential may not go to its address is left out, with a note.
    private (AnalysisConnections Connections, TraceRunner Runner) PinConnections(AnalysisConnections connections, List<string> notes)
    {
        var credentials = new InMemoryCredentialStore();
        try
        {
            if (connections.GitHubRepository is not null && ReadCredential(GitHubAccountName, out var serverToken) is { Length: > 0 } token)
            {
                connections = connections with { GitHubApiUrl = GitHubAddress(connections.GitHubApiUrl, serverToken).AbsoluteUri };
                credentials.Write(GitHubAccountName, token);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or CredentialStoreException)
        {
            notes.Add(ex.Message);
            connections = connections with { GitHubRepository = null };
        }

        try
        {
            if (connections.OctopusServer.Length > 0 && ReadCredential(OctopusAccountName, out var serverKey) is { Length: > 0 } key)
            {
                connections = connections with { OctopusServer = OctopusAddress(connections.OctopusServer, serverKey).AbsoluteUri };
                credentials.Write(OctopusAccountName, key);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or CredentialStoreException)
        {
            notes.Add(ex.Message);
            connections = connections with { OctopusServer = string.Empty };
        }

        return (connections, new TraceRunner(credentials, httpClientFactory: _traceHttp));
    }

    // isOpen: the work opens a repository or folder. Its failure goes to the status line only, because the problems
    // belong to the repository on show, which stays.
    private async Task<T?> RunAsync<T>(string what, Func<CancellationToken, Task<T>> work, bool isOpen = false)
    {
        if (_work is not null)
        {
            await _work.CancelAsync().ConfigureAwait(true);
        }

        using var cancellation = new CancellationTokenSource();
        _work = cancellation;
        IsBusy = true;
        BusyText = what;
        try
        {
            var result = await Task.Run(() => work(cancellation.Token), cancellation.Token).ConfigureAwait(true);

            // Newer work took over while this finished: what it found belongs to a view that is gone.
            return cancellation.IsCancellationRequested ? default : result;
        }
        catch (Exception ex) when (cancellation.IsCancellationRequested && ex is OperationCanceledException or TraceSetupException or HttpRequestException
            or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or CredentialStoreException or JsonException)
        {
            return default;
        }
        catch (TraceSetupException ex)
        {
            ClearResult();
            Problems.Add(new ProblemRow("error", ex.Message));
            ProblemCount = Problems.Count;
            StatusText = ex.Message;
            return default;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or CredentialStoreException or JsonException or TaskCanceledException)
        {
            StatusText = ex.Message;
            if (!isOpen)
            {
                Problems.Add(new ProblemRow("error", ex.Message));
                ProblemCount = Problems.Count;
            }

            return default;
        }
        finally
        {
            if (ReferenceEquals(_work, cancellation))
            {
                _work = null;
                IsBusy = false;
                BusyText = null;
            }
        }
    }

    /// <summary>The result on show, or null. For tests.</summary>
    internal TraceResult? LastResult => _lastResult;

    internal void ShowResult(TraceResult result, IReadOnlyList<string> notes, bool revealed)
    {
        // The result keeps its sources for the life of the tab, and nothing reads the rendered manifests after the trace.
        foreach (var kubernetes in result.Sources.Select(s => s.Source).OfType<KubernetesSource>())
        {
            kubernetes.Rendered = null;
        }

        _lastResult = result;
        _lastResultRevealed = revealed;
        var previousKey = SelectedRow?.Key ?? _selectedKeyBeforeMasking;
        _selectedKeyBeforeMasking = null;
        var dead = result.DeadSettings.ToDictionary(d => d.Key, d => d.Reason, StringComparer.OrdinalIgnoreCase);
        var ratings = result.Secrets.Where(s => s.Rating != SecretRating.NotSecret)
            .GroupBy(s => s.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Min(s => s.Rating == SecretRating.Exposed ? 0 : s.Rating == SecretRating.Check ? 1 : 2), StringComparer.Ordinal);
        _allRows = result.Report.Settings.Select(s => new SettingRow(s)
        {
            DeadReason = dead.GetValueOrDefault(s.Key),
            Rating = ratings.TryGetValue(s.Key, out var worst) ? worst switch { 0 => SecretRating.Exposed, 1 => SecretRating.Check, _ => SecretRating.Protected } : null,
        }).ToList();
        TotalCount = _allRows.Count;
        OverriddenCount = _allRows.Count(r => r.IsOverridden);
        UpstreamCount = _allRows.Count(r => r.OriginSource.Length > 0);

        SecretRows.Clear();
        foreach (var row in result.Secrets.Where(s => s.Rating != SecretRating.NotSecret).Select(s => new SecretRow(s)).OrderBy(r => r.Order).ThenBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
        {
            SecretRows.Add(row);
        }

        ProtectedCount = result.Secrets.Count(s => s.Rating == SecretRating.Protected);
        CheckCount = result.Secrets.Count(s => s.Rating == SecretRating.Check);
        ExposedCount = result.Secrets.Count(s => s.Rating == SecretRating.Exposed);

        DeadCount = _allRows.Count(r => r.IsDead) + result.DeadHelmValues.Count + result.UnusedVariables.Count;
        WorkflowOnlyCount = result.WorkflowVariables.Count;
        _allRows.AddRange(VariableRows(result));
        RowCount = _allRows.Count;
        foreach (var group in _allRows.GroupBy(r => r.NormalizedName, StringComparer.Ordinal))
        {
            var count = group.Count();
            foreach (var row in group)
            {
                row.NameCount = count;
            }
        }

        DuplicateCount = _allRows.Count(r => r.IsDuplicate);
        foreach (var filter in ColumnFilters)
        {
            filter.Load(_allRows);
        }

        DeadScanText = result.CodeFilesScanned switch
        {
            null => "No app code in this trace, so settings were not checked against code.",
            0 => "No C# code found in the app folder, so settings were not checked against code.",
            var n => $"Read {n} C# {(n == 1 ? "file" : "files")} for the keys the app reads."
                + (result.DynamicReads.Count > 0 ? $" {result.DynamicReads.Count} {(result.DynamicReads.Count == 1 ? "read builds its key" : "reads build their key")} at run time, so check before deleting." : string.Empty),
        };

        Problems.Clear();
        foreach (var source in result.Sources.Where(s => s.Problem is not null))
        {
            Problems.Add(new ProblemRow("error", $"{source.Source.Name}: {source.Problem}"));
        }

        if (result.DynamicReads.Count > 0)
        {
            Problems.Add(new ProblemRow("info", $"Code reads configuration with a key built at run time in {string.Join(", ", result.DynamicReads.Take(5))}{(result.DynamicReads.Count > 5 ? $" and {result.DynamicReads.Count - 5} more" : string.Empty)}. A setting marked dead may still be read there."));
        }

        if (DeadScanText is not null)
        {
            Problems.Add(new ProblemRow("info", DeadScanText));
        }

        foreach (var note in notes.Concat(result.Notes))
        {
            Problems.Add(new ProblemRow("info", note));
        }

        foreach (var warning in result.Report.Warnings)
        {
            Problems.Add(new ProblemRow("warning", warning));
        }

        ProblemCount = Problems.Count;
        HasResult = true;
        ApplyFilter();
        SelectedRow = Rows.FirstOrDefault(r => string.Equals(r.Key, previousKey, StringComparison.OrdinalIgnoreCase)) ?? Rows.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedSecret));
        var used = result.Sources.Count(s => s.Problem is null);
        StatusText = string.Create(
            CultureInfo.CurrentCulture,
            $"Traced {TotalCount} settings from {used} of {result.Sources.Count} sources at {DateTime.Now:t}.{(revealed ? " Secret values are shown." : string.Empty)}");
    }

    // The kinds of source the Sources drop-down turns on and off. App settings are the trace's starting point, so null.
    private static string? SourceFamily(SourceSettings source) => source switch
    {
        HelmSource => "Helm chart",
        KustomizeSource => "Kustomize",
        KubernetesSource => "Kubernetes manifests",
        GitHubSource => "GitHub Actions",
        OctopusSource => "Octopus Deploy",
        _ => null,
    };

    private void ShowDiscoveries(RepositoryScan scan, Profile? profile)
    {
        var sources = profile?.Sources.Where(s => s.Enabled).ToList() ?? [];
        var turnedOff = _currentKey is not null && _workspace.Repositories.TryGetValue(_currentKey, out var choices) ? choices.TurnedOff : [];
        Discoveries.Clear();
        if (scan.Apps.Count > 0)
        {
            Discoveries.Add(new SourceToggle("dotnet", "IconBraces", "App settings", string.Join(", ", scan.Apps.Select(a => a.RelativePath)), scan.Apps.Any(a => a.RelativePath == SelectedApp)));
        }

        if (scan.HelmCharts.Count > 0)
        {
            Add("helm", "IconAnchor", "Helm chart", string.Join(", ", scan.HelmCharts.Select(c => c.RelativePath)), sources.OfType<HelmSource>().Any());
        }

        if (scan.Kustomizations.Count > 0)
        {
            Add("kustomize", "IconLayers", "Kustomize", string.Join(", ", scan.Kustomizations), sources.OfType<KustomizeSource>().Any());
        }

        if (scan.Manifests.Count > 0)
        {
            Add("k8s", "IconHexagon", "Kubernetes manifests", scan.Manifests.Count == 1 ? scan.Manifests[0] : $"{scan.Manifests.Count} files", sources.OfType<KubernetesSource>().Any());
        }

        if (scan.Workflows.Count > 0 || IsGitHubSubject)
        {
            var detail = scan.Workflows.Count == 0 ? "Repository variables and secrets" : scan.Workflows.Count == 1 ? "Variables, secrets and 1 workflow" : $"Variables, secrets and {scan.Workflows.Count} workflows";
            Add("github", "IconBranch", "GitHub Actions", detail, sources.OfType<GitHubSource>().Any());
        }

        var octopus = sources.OfType<OctopusSource>().FirstOrDefault();
        if (octopus is not null || scan.HasOctopusConfigAsCode || turnedOff.Contains("Octopus Deploy", StringComparer.Ordinal))
        {
            var detail = octopus is not null ? $"Project {octopus.Project}" : scan.HasOctopusConfigAsCode && !IsOctopusConnected ? "Config as code found; connect Octopus to read the variables" : "Octopus project variables";
            Add("octopus", "IconRocket", "Octopus Deploy", detail, octopus is not null);
        }

        var on = Discoveries.Count(d => d.IsOn);
        SourcesText = on == Discoveries.Count
            ? string.Create(CultureInfo.CurrentCulture, $"Sources · {Discoveries.Count}")
            : string.Create(CultureInfo.CurrentCulture, $"Sources · {on} of {Discoveries.Count}");

        void Add(string css, string icon, string title, string detail, bool used) =>
            Discoveries.Add(new SourceToggle(css, icon, title, detail, used, !turnedOff.Contains(title, StringComparer.Ordinal), OnSourceToggled));
    }

    // Turning a source on or off saves the choice for this repository and traces again.
    private void OnSourceToggled(SourceToggle toggle)
    {
        if (_currentKey is null || !_workspace.Repositories.TryGetValue(_currentKey, out var choices))
        {
            return;
        }

        choices.TurnedOff.Remove(toggle.Title);
        if (!toggle.IsOn)
        {
            choices.TurnedOff.Add(toggle.Title);
        }

        RetraceOnChoice();
    }

    private void ClearResult()
    {
        _lastResult = null;
        _lastResultRevealed = false;
        _selectedKeyBeforeMasking = null;
        _allRows = [];
        Rows.Clear();
        Problems.Clear();
        SecretRows.Clear();

        // The tick lists are built from the rows: the value one holds every value shown, secret ones too.
        foreach (var filter in ColumnFilters)
        {
            filter.Load([]);
        }
        TotalCount = RowCount = OverriddenCount = UpstreamCount = ProtectedCount = CheckCount = ExposedCount = ProblemCount = DeadCount = WorkflowOnlyCount = DuplicateCount = 0;
        DeadScanText = null;
        SelectedRow = null;
        HasResult = false;
        ShownText = null;
        OnPropertyChanged(nameof(ShowNoMatches));
    }

    // GitHub and Octopus variables no setting picks up, and Helm values no template reads, as rows of their own so the
    // one table holds everything: workflow-only variables tagged "workflow", the rest dead.
    private static IEnumerable<SettingRow> VariableRows(TraceResult result)
    {
        foreach (var variable in result.WorkflowVariables)
        {
            yield return SettingRow.ForVariable(
                LayerKind.GitHubActions,
                variable.IsSecret ? "GitHub Actions secret" : "GitHub Actions variable",
                variable.Name,
                variable.Value,
                variable.IsSecret || variable.NameLooksSecret,
                "GitHub Actions " + variable.Detail,
                variable.UsedBy,
                workflowUse: variable.UsedBy);
        }

        foreach (var variable in result.UnusedVariables)
        {
            var github = variable.Tool.StartsWith("GitHub", StringComparison.Ordinal);
            yield return SettingRow.ForVariable(
                github ? LayerKind.GitHubActions : LayerKind.OctopusDeploy,
                $"{variable.Tool} {(variable.IsSecret ? (github ? "secret" : "sensitive variable") : "variable")}",
                variable.Name,
                variable.Value,
                variable.IsSecret || variable.NameLooksSecret,
                $"{variable.Tool} {variable.Detail}",
                deadReason: github ? "No setting in this trace or workflow uses it" : "No setting in this trace uses it");
        }

        foreach (var value in result.DeadHelmValues)
        {
            yield return SettingRow.ForVariable(LayerKind.Helm, "Helm value", value.Path, null, false, value.File, deadReason: "No template in the chart reads it");
        }
    }

    private bool PassesTile(SettingRow row) => ActiveTile switch
    {
        TileFilter.Overridden => row.IsOverridden,
        TileFilter.Upstream => row.OriginSource.Length > 0,
        TileFilter.Protected => row.Rating == SecretRating.Protected,
        TileFilter.Check => row.Rating == SecretRating.Check,
        TileFilter.Exposed => row.Rating == SecretRating.Exposed,
        TileFilter.Dead => row.IsDead,
        TileFilter.Workflow => row.IsWorkflowOnly,
        TileFilter.Duplicate => row.IsDuplicate,
        _ => true,
    };

    private void ApplyFilter()
    {
        if (_clearing)
        {
            return;
        }

        var selected = SelectedRow;
        IEnumerable<SettingRow> shown = _allRows.Where(r => r.Matches(FilterText) && PassesTile(r) && ColumnFilters.All(c => c.Matches(r)));
        if (SortColumn is null && ActiveTile == TileFilter.Duplicate)
        {
            shown = shown.OrderBy(r => r.NormalizedName, StringComparer.Ordinal);
        }

        if (SortColumn is { } column)
        {
            Func<SettingRow, string> text = column switch
            {
                TableColumn.Value => r => r.Value,
                TableColumn.ComesFrom => r => r.ComesFrom,
                TableColumn.Layers => r => string.Join(" ", r.Kinds.Select(k => k.Label)),
                _ => r => r.Key,
            };
            shown = SortDescending
                ? shown.OrderByDescending(text, StringComparer.OrdinalIgnoreCase)
                : shown.OrderBy(text, StringComparer.OrdinalIgnoreCase);
        }

        Rows.Clear();
        foreach (var row in shown)
        {
            Rows.Add(row);
        }

        ShownText = Rows.Count == _allRows.Count
            ? string.Create(CultureInfo.CurrentCulture, $"{Rows.Count} rows")
            : string.Create(CultureInfo.CurrentCulture, $"{Rows.Count} of {_allRows.Count} rows");
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(ShowNoMatches));
        if (selected is not null && Rows.Contains(selected))
        {
            SelectedRow = selected;
        }
    }

    private void ApplyRepositoryFilter()
    {
        var selected = SelectedRepository;
        _populating = true;
        try
        {
            Repositories.Clear();
            foreach (var repository in _allRepositories.Where(r => r.Matches(RepositoryFilter)))
            {
                Repositories.Add(repository);
            }

            if (selected is not null && Repositories.Contains(selected))
            {
                SelectedRepository = selected;
            }
        }
        finally
        {
            _populating = false;
        }

        RepositoryCountText = _allRepositories.Count == 0
            ? null
            : Repositories.Count == _allRepositories.Count ? $"{Repositories.Count} repositories" : $"{Repositories.Count} of {_allRepositories.Count} repositories";
    }

    // Each app of a repository can have its own chart and overlay, so they are suggested again for the app picked, and the
    // values files are the new chart's.
    private void RefreshChartChoices()
    {
        if (_scan is not { } scan || _currentKey is null || !_workspace.Repositories.TryGetValue(_currentKey, out var choices))
        {
            return;
        }

        var before = choices.HelmChart;
        choices.App = SelectedApp;
        choices.Environment = string.IsNullOrWhiteSpace(SelectedEnvironment) ? choices.Environment : SelectedEnvironment;
        RepositoryAnalyzer.SuggestDeployment(scan, choices);
        if (choices.HelmChart == before)
        {
            return;
        }

        var chart = scan.HelmCharts.FirstOrDefault(c => c.RelativePath == choices.HelmChart);
        var populating = _populating;
        _populating = true;
        HasHelm = chart is not null;
        Fill(ValuesFiles, [None, .. chart?.ValuesFiles ?? []]);
        SelectedValuesFile = choices.HelmValues ?? None;
        _populating = populating;
    }

    private void RefreshEnvironmentChoices()
    {
        var app = _scan?.Apps.FirstOrDefault(a => a.RelativePath == SelectedApp);
        // A name with spaces around it (appsettings. Staging.json) would be read as another file, so it is not offered.
        var environments = (app?.Environments ?? []).Where(e => string.Equals(e, e.Trim(), StringComparison.Ordinal)).ToList();
        foreach (var standard in new[] { "Production", "Staging", "Development" })
        {
            if (!environments.Contains(standard, StringComparer.OrdinalIgnoreCase))
            {
                environments.Add(standard);
            }
        }

        var current = SelectedEnvironment;
        var populating = _populating;
        _populating = true;
        Fill(Environments, environments);
        SelectedEnvironment = environments.FirstOrDefault(e => string.Equals(e, current, StringComparison.OrdinalIgnoreCase)) ?? environments[0];
        _populating = populating;
    }

    // The environment and values file picked, as this repository offers them. Both name a file to read, and in the web
    // app a pick is whatever the browser sends, so one that was not offered, or whose file leads out of the repository,
    // is refused with the reason.
    private string? RefuseChoices(RepositoryScan scan, out string environment, out string? valuesFile)
    {
        var picked = string.IsNullOrWhiteSpace(SelectedEnvironment) ? "Production" : SelectedEnvironment;
        environment = Environments.FirstOrDefault(e => string.Equals(e, picked, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        valuesFile = SelectedValuesFile is null or None ? null : ValuesFiles.FirstOrDefault(v => v != None && string.Equals(v, SelectedValuesFile, StringComparison.Ordinal));
        var app = scan.Apps.FirstOrDefault(a => a.RelativePath == SelectedApp);
        // The trace reads appsettings.{the name, trimmed}.json, so a name with spaces around it would be checked as one
        // file and read as another.
        if (environment.Length == 0
            || !string.Equals(environment, environment.Trim(), StringComparison.Ordinal)
            || (app is not null && !Kustomization.IsInFolder(Path.Join(app.Folder, $"appsettings.{environment}.json"), scan.Root)))
        {
            return "Pick an environment from the list. The one picked is not one of this app's, or its settings file is outside the repository.";
        }

        if (SelectedValuesFile is not (null or None) && (valuesFile is null || !Kustomization.IsInFolder(scan.Full(valuesFile), scan.Root)))
        {
            return "Pick a Helm values file from the list. The one picked is not one of this chart's, or it is outside the repository.";
        }

        return null;
    }

    private async Task ExportAsync(string suggestedName, Func<ProvenanceReport, string> render)
    {
        LastExportHoldsSecrets = false;
        if (Dialogs is null || _lastResult is not { } result)
        {
            return;
        }

        var holdsSecrets = _lastResultRevealed;

        var path = await Dialogs.SaveFileAsync("Export the report", suggestedName).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        // The result on show can change while the dialog is open, for example when the switch goes off. Then nothing is
        // written, so the file holds no values the view no longer shows.
        if (!ReferenceEquals(result, _lastResult))
        {
            StatusText = "The trace changed while you chose where to save. Export again.";
            return;
        }

        LastExportHoldsSecrets = holdsSecrets;
        try
        {
            await File.WriteAllTextAsync(path, render(result.Report)).ConfigureAwait(true);
            StatusText = $"Saved {path}." + (holdsSecrets ? " It holds secret values: don't share it." : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not save {path}: {ex.Message}";
        }
    }

    private GitHubAccount CreateGitHub(string token, bool serverToken = false) => new(_http, GitHubAddress(GitHubApiUrl, serverToken), token);

    // Reads a credential, and says whether it is the server's rather than the user's own (see IsSharedServer).
    private string? ReadCredential(string account, out bool serverOwned)
    {
        if (_isSharedServer && _credentials is EnvironmentFallbackCredentialStore fallback)
        {
            return fallback.Read(account, out serverOwned);
        }

        serverOwned = false;
        return _credentials.Read(account);
    }

    // The user's own token goes to the address they typed; the server's only to api.github.com.
    private static Uri GitHubAddress(string address, bool serverToken)
    {
        var uri = string.IsNullOrWhiteSpace(address) ? GitHubAccount.DefaultApiBase : ParseUrl(address.Trim());
        return serverToken
            ? Pinned(uri, GitHubAccount.DefaultApiBase, "The server's GitHub token is only sent to api.github.com. Connect with your own token to use another API address.")
            : uri;
    }

    // The user's own API key goes to the server they typed; the server's only to its own Octopus server (OCTOPUS_URL).
    private Uri OctopusAddress(string address, bool serverKey)
    {
        var uri = ParseUrl(address);
        return serverKey
            ? Pinned(uri, _octopusServerFallback is null ? null : ParseUrl(_octopusServerFallback), "The server's Octopus API key is only sent to the Octopus server it was set up with (OCTOPUS_URL). Connect with your own API key to use another server.")
            : uri;
    }

    private static Uri Pinned(Uri requested, Uri? allowed, string refusal) =>
        allowed is not null && Uri.Compare(requested, allowed, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0
            ? allowed
            : throw new ArgumentException(refusal);

    private static Uri ParseUrl(string text) =>
        Uri.TryCreate(text.EndsWith('/') ? text : text + "/", UriKind.Absolute, out var uri)
            ? uri
            : throw new ArgumentException($"'{text}' is not an absolute URL.");

    private bool HasCredential(string account)
    {
        try
        {
            return !string.IsNullOrEmpty(_credentials.Read(account));
        }
        catch (CredentialStoreException ex)
        {
            StatusText = ex.Message;
            return false;
        }
    }

    private void TryDelete(string account)
    {
        try
        {
            _credentials.Delete(account);
        }
        catch (CredentialStoreException ex)
        {
            StatusText = ex.Message;
        }
    }

    private void Save()
    {
        try
        {
            _store.Save(_workspace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not save {_store.Path}: {ex.Message}";
        }
    }

    private void DeleteDownload()
    {
        DeleteFolder(_currentDownload);
        _currentDownload = null;
    }

    private void DeleteOwnedDownloadRoot()
    {
        if (!_ownsDownloadRoot || _downloadRoot is null)
        {
            return;
        }

        try
        {
            Directory.Delete(_downloadRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the system's temp cleanup.
        }
    }

    private static void DeleteFolder(string? folder)
    {
        if (folder is null)
        {
            return;
        }

        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover download in the temp folder is harmless.
        }
    }

    private sealed record Download(string Folder, IReadOnlyList<string> Environments);

    // What an open shows as its title once its scan is done.
    private sealed record Subject(string Title, string Detail, bool IsGitHub);

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    /// <summary>
    /// The HTTP handler the app talks to GitHub and Octopus with. It does not follow redirects: the Octopus API key is a
    /// custom header that the runtime would pass on to whatever host a redirect names, and <see cref="GitHubAccount"/>
    /// follows GitHub's redirects itself without the token.
    /// </summary>
    internal static SocketsHttpHandler CreateHttpHandler() => new() { AllowAutoRedirect = false };

    // A folder only this user can open (mkdtemp, mode 0700 on Unix), made on first use. A fixed name under a shared /tmp
    // could be created first by another user, who could then read the private repositories or swap folders for links.
    private string DownloadRoot()
    {
        if (_downloadRoot is null)
        {
            _downloadRoot = Directory.CreateTempSubdirectory("configferret-repos-").FullName;
        }
        else
        {
            Directory.CreateDirectory(_downloadRoot);
        }

        return _downloadRoot;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // An open still on its way then finds a newer one and deletes its own download rather than taking over.
        _opening++;
        _disposed = true;
        _work?.Cancel();
        _repositoryLoad?.Cancel();
        DeleteDownload();
        DeleteOwnedDownloadRoot();
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}

/// <summary>The stat tiles, each a filter on the table.</summary>
public enum TileFilter
{
    /// <summary>Every row.</summary>
    All,

    /// <summary>Keys set by more than one .NET layer.</summary>
    Overridden,

    /// <summary>Values that come from further upstream than the app, such as Helm or Octopus.</summary>
    Upstream,

    /// <summary>Secrets kept in a secret store.</summary>
    Protected,

    /// <summary>Secrets to check.</summary>
    Check,

    /// <summary>Secrets in plain configuration.</summary>
    Exposed,

    /// <summary>Settings and variables nothing reads.</summary>
    Dead,

    /// <summary>GitHub variables only a workflow uses.</summary>
    Workflow,

    /// <summary>Names that more than one row has.</summary>
    Duplicate,
}
