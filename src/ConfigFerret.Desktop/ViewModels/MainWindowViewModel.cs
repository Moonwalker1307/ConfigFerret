using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConfigFerret.Desktop.Credentials;
using ConfigFerret.Desktop.Model;
using ConfigFerret.Desktop.Services;

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
    private readonly string _downloadRoot;
    private List<RepositoryRow> _allRepositories = [];
    private List<SettingRow> _allRows = [];
    private readonly List<UnusedRow> _allUnused = [];
    private RepositoryScan? _scan;
    private string? _currentKey;
    private string? _currentDownload;
    private TraceResult? _lastResult;
    private bool _populating;
    private CancellationTokenSource? _work;

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
        _http = httpHandler is null ? new HttpClient() : new HttpClient(httpHandler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _runner = new TraceRunner(
            credentials,
            httpClientFactory: httpHandler is null ? null : () => new HttpClient(httpHandler, disposeHandler: false));
        _analyzer = analyzer ?? new RepositoryAnalyzer();
        _downloadRoot = downloadRoot ?? Path.Join(Path.GetTempPath(), "configferret-repos");

        try
        {
            _workspace = store.Load();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _workspace = new Workspace();
            StatusText = $"Could not read {store.Path}: {ex.Message}. Started fresh; the file is replaced when settings change.";
        }

        GitHubApiUrl = _workspace.GitHubApiUrl;
        OctopusServer = _workspace.OctopusServer;
        OctopusSpace = _workspace.OctopusSpace;
        IsDarkTheme = _workspace.DarkTheme;
        IsSidebarOpen = !_workspace.SidebarHidden;
        LayerFilters = [new LayerFilter(null), .. Enum.GetValues<LayerKind>().Select(k => new LayerFilter(k))];
        SelectedLayerFilter = LayerFilters[0];
        IsOctopusConnected = OctopusServer.Length > 0 && HasCredential(OctopusAccountName);
    }

    /// <summary>The Octopus server to use when none is saved, for example from OCTOPUS_URL.</summary>
    public string? OctopusServerFallback
    {
        get => null;
        init
        {
            if (OctopusServer.Length == 0 && !string.IsNullOrWhiteSpace(value))
            {
                OctopusServer = value.Trim();
                _workspace.OctopusServer = OctopusServer;
                IsOctopusConnected = HasCredential(OctopusAccountName);
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
    public ObservableCollection<DiscoveryCard> Discoveries { get; } = [];

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

    /// <summary>Dead variables and values that never win, as filtered by <see cref="OnlyDead"/>.</summary>
    public ObservableCollection<UnusedRow> Unused { get; } = [];

    /// <summary>Shows only dead variables in the Unused tab, not values that are overridden.</summary>
    [ObservableProperty]
    public partial bool OnlyDead { get; set; }

    /// <summary>Variables nothing reads: settings no code reads, Helm values no template reads, and GitHub or Octopus variables no setting uses.</summary>
    [ObservableProperty]
    public partial int DeadCount { get; set; }

    /// <summary>Values that a later layer always overrides.</summary>
    [ObservableProperty]
    public partial int NeverWinsCount { get; set; }

    /// <summary>What the dead-variable scan read, such as "Read 12 C# files".</summary>
    [ObservableProperty]
    public partial string? DeadScanText { get; set; }

    /// <summary>Warnings and source problems from the last trace.</summary>
    public ObservableCollection<ProblemRow> Problems { get; } = [];

    /// <summary>The choices in the layer filter.</summary>
    public IReadOnlyList<LayerFilter> LayerFilters { get; }

    /// <summary>Search text: matches keys, values and source names.</summary>
    [ObservableProperty]
    public partial string? FilterText { get; set; }

    /// <summary>Shows only keys set by more than one layer.</summary>
    [ObservableProperty]
    public partial bool OnlyOverridden { get; set; }

    /// <summary>Shows only secret values.</summary>
    [ObservableProperty]
    public partial bool OnlySecrets { get; set; }

    /// <summary>Shows only settings that no code reads.</summary>
    [ObservableProperty]
    public partial bool OnlyDeadSettings { get; set; }

    /// <summary>Shows only keys whose history includes this kind of layer.</summary>
    [ObservableProperty]
    public partial LayerFilter? SelectedLayerFilter { get; set; }

    /// <summary>Shows secret values instead of ***. Traces again when changed.</summary>
    [ObservableProperty]
    public partial bool RevealSecrets { get; set; }

    /// <summary>A line about the last action.</summary>
    [ObservableProperty]
    public partial string? StatusText { get; set; }

    /// <summary>The dark theme is on.</summary>
    [ObservableProperty]
    public partial bool IsDarkTheme { get; set; } = true;

    /// <summary>The repository sidebar is shown. Hiding it gives the results the whole window.</summary>
    [ObservableProperty]
    public partial bool IsSidebarOpen { get; set; } = true;

    /// <summary>How many variables the trace found.</summary>
    [ObservableProperty]
    public partial int TotalCount { get; set; }

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

    /// <summary>Possibly unused values and variables.</summary>
    [ObservableProperty]
    public partial int UnusedCount { get; set; }

    /// <summary>Warnings and problems.</summary>
    [ObservableProperty]
    public partial int ProblemCount { get; set; }

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
        _ = RetraceAsync();
    }

    partial void OnSelectedEnvironmentChanged(string? value) => RetraceOnChoice();

    partial void OnSelectedValuesFileChanged(string? value) => RetraceOnChoice();

    partial void OnSelectedGitHubEnvironmentChanged(string? value) => RetraceOnChoice();

    partial void OnSelectedOctopusProjectChanged(string? value) => RetraceOnChoice();

    partial void OnSelectedOctopusEnvironmentChanged(string? value) => RetraceOnChoice();

    partial void OnFilterTextChanged(string? value) => ApplyFilter();

    partial void OnOnlyOverriddenChanged(bool value) => ApplyFilter();

    partial void OnOnlyDeadChanged(bool value) => ApplyUnusedFilter();

    partial void OnOnlySecretsChanged(bool value) => ApplyFilter();

    partial void OnOnlyDeadSettingsChanged(bool value) => ApplyFilter();

    partial void OnSelectedLayerFilterChanged(LayerFilter? value) => ApplyFilter();

    partial void OnRevealSecretsChanged(bool value) => RetraceOnChoice();

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
            _workspace.GitHubApiUrl = GitHubApiUrl.Trim();
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
            _workspace.OctopusServer = OctopusServer.Trim();
            _workspace.OctopusSpace = OctopusSpace.Trim();
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
        if (SelectedRepository is { } repository)
        {
            await AnalyzeRepositoryAsync(repository).ConfigureAwait(true);
        }
        else if (_currentKey is not null && Directory.Exists(_currentKey))
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

    /// <summary>Clears the search and the filters.</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        FilterText = null;
        OnlyOverridden = false;
        OnlySecrets = false;
        OnlyDeadSettings = false;
        SelectedLayerFilter = LayerFilters[0];
    }

    /// <summary>Shows the variable a secrets or unused row is about.</summary>
    [RelayCommand]
    private void ShowKey(string? key)
    {
        if (key is null)
        {
            return;
        }

        ClearFilters();
        SelectedRow = Rows.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
        SelectedTab = 1;
    }

    /// <summary>The selected tab: 0 overview, 1 configuration, 2 secrets, 3 unused, 4 problems.</summary>
    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    private bool IsIdle() => !IsBusy;

    private bool CanExport() => HasResult && !IsBusy;

    // ---- Work --------------------------------------------------------------------------------------------------

    internal async Task LoadRepositoriesAsync()
    {
        string? token;
        try
        {
            token = _credentials.Read(GitHubAccountName);
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

        IsLoadingRepositories = true;
        try
        {
            var account = CreateGitHub(token);
            GitHubLogin ??= await account.GetLoginAsync().ConfigureAwait(true);
            var repositories = await account.ListRepositoriesAsync().ConfigureAwait(true);
            _allRepositories = repositories.Select(r => new RepositoryRow(r)).ToList();
            ApplyRepositoryFilter();
            StatusText = $"Signed in to GitHub as {GitHubLogin}.";
        }
        catch (Exception ex) when (ex is HttpRequestException or ArgumentException or UriFormatException or TaskCanceledException or JsonException)
        {
            StatusText = ex.Message;
            ConnectionError = ex.Message;
            ShowConnections = true;
        }
        finally
        {
            IsLoadingRepositories = false;
        }
    }

    internal async Task AnalyzeRepositoryAsync(RepositoryRow repository)
    {
        var token = await RunAsync($"Downloading {repository.FullName}…", async cancellationToken =>
        {
            var github = CreateGitHub(_credentials.Read(GitHubAccountName) ?? throw new HttpRequestException("Connect GitHub first."));
            var environments = await github.ListEnvironmentsAsync(repository.FullName, cancellationToken).ConfigureAwait(true);
            Directory.CreateDirectory(_downloadRoot);
            var folder = await github.DownloadAsync(repository.FullName, repository.Info.DefaultBranch, _downloadRoot, cancellationToken).ConfigureAwait(true);
            DeleteDownload();
            _currentDownload = folder;
            return new Download(folder, environments);
        }).ConfigureAwait(true);

        if (token is not { } downloaded)
        {
            return;
        }

        SubjectTitle = repository.FullName;
        SubjectDetail = $"{repository.Info.DefaultBranch} · {(repository.IsPrivate ? "private" : "public")}{(repository.HasLanguage ? " · " + repository.Language : string.Empty)}";
        IsGitHubSubject = true;
        await OpenScanAsync(repository.FullName, downloaded.Folder, downloaded.Environments).ConfigureAwait(true);
    }

    internal async Task AnalyzeFolderAsync(string folder)
    {
        folder = Path.GetFullPath(folder);
        _populating = true;
        SelectedRepository = null;
        _populating = false;
        SubjectTitle = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        SubjectDetail = folder;
        IsGitHubSubject = false;
        _workspace.RecentFolders.Remove(folder);
        _workspace.RecentFolders.Insert(0, folder);
        if (_workspace.RecentFolders.Count > 10)
        {
            _workspace.RecentFolders.RemoveRange(10, _workspace.RecentFolders.Count - 10);
        }

        await OpenScanAsync(folder, folder, []).ConfigureAwait(true);
    }

    private async Task OpenScanAsync(string key, string folder, IReadOnlyList<string> gitHubEnvironments)
    {
        RepositoryScan? scan = null;
        await RunAsync("Looking for configuration…", _ => Task.FromResult(scan = RepositoryScanner.Scan(folder))).ConfigureAwait(true);
        if (scan is null)
        {
            return;
        }

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
            SelectedEnvironment = choices.Environment;
            var chart = scan.HelmCharts.FirstOrDefault(c => c.RelativePath == choices.HelmChart);
            HasHelm = chart is not null;
            Fill(ValuesFiles, [None, .. chart?.ValuesFiles ?? []]);
            SelectedValuesFile = choices.HelmValues ?? None;
            Fill(GitHubEnvironments, [None, .. gitHubEnvironments]);
            SelectedGitHubEnvironment = choices.GitHubEnvironment ?? None;
        }
        finally
        {
            _populating = false;
        }

        await LoadOctopusChoicesAsync().ConfigureAwait(true);
        SelectedTab = 0;
        await RetraceAsync().ConfigureAwait(true);
    }

    private async Task LoadOctopusChoicesAsync()
    {
        if (!IsOctopusConnected || _currentKey is null || !_workspace.Repositories.TryGetValue(_currentKey, out var choices))
        {
            return;
        }

        try
        {
            var key = _credentials.Read(OctopusAccountName);
            if (string.IsNullOrEmpty(key))
            {
                IsOctopusConnected = false;
                return;
            }

            var octopus = new OctopusAccount(_http, ParseUrl(OctopusServer), key, OctopusSpace);
            var projects = await octopus.ListProjectsAsync().ConfigureAwait(true);
            var environments = await octopus.ListEnvironmentsAsync().ConfigureAwait(true);

            // A project named like the repository is most likely the one that deploys it.
            var repoName = _currentKey.Split('/', '\\').Last();
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

        var choices = _workspace.Repositories[_currentKey];
        choices.App = SelectedApp;
        choices.Environment = string.IsNullOrWhiteSpace(SelectedEnvironment) ? "Production" : SelectedEnvironment;
        choices.HelmValues = SelectedValuesFile is null or None ? null : SelectedValuesFile;
        choices.GitHubEnvironment = SelectedGitHubEnvironment is null or None ? null : SelectedGitHubEnvironment;
        choices.OctopusProject = SelectedOctopusProject is null or None ? null : SelectedOctopusProject;
        choices.OctopusEnvironment = SelectedOctopusEnvironment is null or AnyEnvironment ? null : SelectedOctopusEnvironment;
        Save();

        var connections = new AnalysisConnections(
            IsGitHubSubject ? _currentKey : null,
            _workspace.GitHubApiUrl,
            GitHubAccountName,
            IsOctopusConnected ? _workspace.OctopusServer : string.Empty,
            _workspace.OctopusSpace,
            OctopusAccountName);
        var reveal = RevealSecrets;
        var notes = new List<string>();
        Profile? profile = null;
        var result = await RunAsync("Tracing the configuration…", async cancellationToken =>
        {
            profile = _analyzer.BuildProfile(scan, choices, connections, notes);
            return await _runner.RunAsync(profile, reveal, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(true);

        ShowDiscoveries(scan, profile);
        if (result is not null)
        {
            ShowResult(result, notes);
        }
    }

    private async Task<T?> RunAsync<T>(string what, Func<CancellationToken, Task<T>> work)
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
            return await Task.Run(() => work(cancellation.Token), cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return default;
        }
        catch (TraceSetupException ex)
        {
            ClearResult();
            Problems.Add(new ProblemRow("error", ex.Message));
            ProblemCount = Problems.Count;
            StatusText = ex.Message;
            SelectedTab = 0;
            return default;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or CredentialStoreException or JsonException or TaskCanceledException)
        {
            StatusText = ex.Message;
            Problems.Add(new ProblemRow("error", ex.Message));
            ProblemCount = Problems.Count;
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

    internal void ShowResult(TraceResult result, IReadOnlyList<string> notes)
    {
        _lastResult = result;
        var previousKey = SelectedRow?.Key;
        var dead = result.DeadSettings.ToDictionary(d => d.Key, d => d.Reason, StringComparer.OrdinalIgnoreCase);
        _allRows = result.Report.Settings.Select(s => new SettingRow(s) { DeadReason = dead.GetValueOrDefault(s.Key) }).ToList();
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

        _allUnused.Clear();
        foreach (var setting in result.DeadSettings)
        {
            _allUnused.Add(new UnusedRow("bad", "Setting", setting.Key, string.Join(", ", setting.SetBy), setting.Reason, IsDead: true));
        }

        foreach (var value in result.DeadHelmValues)
        {
            _allUnused.Add(new UnusedRow("helm", "Helm value", value.Path, value.File, "No template in the chart reads it", IsDead: true) { CanShow = false });
        }

        foreach (var variable in result.UnusedVariables)
        {
            _allUnused.Add(new UnusedRow(
                variable.Tool.StartsWith("GitHub", StringComparison.Ordinal) ? "github" : "octopus",
                variable.Tool,
                variable.Name,
                variable.Detail,
                "No setting in this trace uses it",
                IsDead: true) { CanShow = false });
        }

        foreach (var value in result.Unused.NeverWinning)
        {
            _allUnused.Add(new UnusedRow(
                "dotnet",
                "Overridden",
                value.Key,
                value.Step.Source + (value.Step.Location is null ? string.Empty : $" ({value.Step.Location})"),
                (value.SameValueAsWinner ? "Same value as " : "Always overridden by ") + value.WonBy.Source));
        }

        DeadCount = _allUnused.Count(u => u.IsDead);
        NeverWinsCount = _allUnused.Count - DeadCount;
        UnusedCount = _allUnused.Count;
        DeadScanText = result.CodeFilesScanned switch
        {
            null => "No app code in this trace, so settings were not checked against code.",
            0 => "No C# code found in the app folder, so settings were not checked against code.",
            var n => $"Read {n} C# {(n == 1 ? "file" : "files")} for the keys the app reads."
                + (result.DynamicReads.Count > 0 ? $" {result.DynamicReads.Count} {(result.DynamicReads.Count == 1 ? "read builds its key" : "reads build their key")} at run time, so check before deleting." : string.Empty),
        };
        ApplyUnusedFilter();

        Problems.Clear();
        foreach (var source in result.Sources.Where(s => s.Problem is not null))
        {
            Problems.Add(new ProblemRow("error", $"{source.Source.Name}: {source.Problem}"));
        }

        if (result.DynamicReads.Count > 0)
        {
            Problems.Add(new ProblemRow("info", $"Code reads configuration with a key built at run time in {string.Join(", ", result.DynamicReads.Take(5))}{(result.DynamicReads.Count > 5 ? $" and {result.DynamicReads.Count - 5} more" : string.Empty)}. A setting marked dead may still be read there."));
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
            $"Traced {TotalCount} settings from {used} of {result.Sources.Count} sources at {DateTime.Now:t}.{(RevealSecrets ? " Secret values are shown." : string.Empty)}");
    }

    private void ShowDiscoveries(RepositoryScan scan, Profile? profile)
    {
        var sources = profile?.Sources ?? [];
        Discoveries.Clear();
        foreach (var app in scan.Apps)
        {
            var detail = app.Environments.Count == 0 ? app.RelativePath : $"{app.RelativePath} · {string.Join(", ", app.Environments)}";
            Discoveries.Add(new DiscoveryCard("dotnet", "IconBraces", "App settings", detail, app.RelativePath == SelectedApp));
        }

        foreach (var chart in scan.HelmCharts)
        {
            var detail = chart.ValuesFiles.Count == 0 ? chart.RelativePath : $"{chart.RelativePath} · {chart.ValuesFiles.Count} values files";
            Discoveries.Add(new DiscoveryCard("helm", "IconAnchor", "Helm chart", detail, sources.OfType<HelmSource>().Any(h => h.ChartFolder == chart.Folder)));
        }

        foreach (var kustomization in scan.Kustomizations)
        {
            Discoveries.Add(new DiscoveryCard("kustomize", "IconLayers", "Kustomize", kustomization, sources.OfType<KustomizeSource>().Any(k => k.Folder == scan.Full(kustomization))));
        }

        if (scan.Manifests.Count > 0)
        {
            Discoveries.Add(new DiscoveryCard("k8s", "IconHexagon", "Kubernetes manifests", scan.Manifests.Count == 1 ? scan.Manifests[0] : $"{scan.Manifests.Count} files", sources.OfType<KubernetesSource>().Any(k => k.Name == "Kubernetes manifests")));
        }

        if (scan.Workflows.Count > 0 || IsGitHubSubject)
        {
            var detail = scan.Workflows.Count == 0 ? "Repository variables and secrets" : scan.Workflows.Count == 1 ? "Variables, secrets and 1 workflow" : $"Variables, secrets and {scan.Workflows.Count} workflows";
            Discoveries.Add(new DiscoveryCard("github", "IconBranch", "GitHub Actions", detail, sources.OfType<GitHubSource>().Any()));
        }

        var octopus = sources.OfType<OctopusSource>().FirstOrDefault();
        if (octopus is not null || scan.HasOctopusConfigAsCode)
        {
            var detail = octopus is not null ? $"Project {octopus.Project}" : "Config as code found; connect Octopus to read the variables";
            Discoveries.Add(new DiscoveryCard("octopus", "IconRocket", "Octopus Deploy", detail, octopus is not null));
        }
    }

    private void ClearResult()
    {
        _lastResult = null;
        _allRows = [];
        Rows.Clear();
        Unused.Clear();
        _allUnused.Clear();
        Problems.Clear();
        SecretRows.Clear();
        TotalCount = OverriddenCount = UpstreamCount = ProtectedCount = CheckCount = ExposedCount = UnusedCount = ProblemCount = DeadCount = NeverWinsCount = 0;
        DeadScanText = null;
        SelectedRow = null;
        HasResult = false;
        ShownText = null;
    }

    private void ApplyUnusedFilter()
    {
        Unused.Clear();
        foreach (var row in _allUnused.Where(u => !OnlyDead || u.IsDead))
        {
            Unused.Add(row);
        }
    }

    private void ApplyFilter()
    {
        var kind = SelectedLayerFilter?.Kind;
        var selected = SelectedRow;
        Rows.Clear();
        foreach (var row in _allRows)
        {
            if (row.Matches(FilterText)
                && (!OnlyOverridden || row.IsOverridden)
                && (!OnlySecrets || row.IsSensitive)
                && (!OnlyDeadSettings || row.IsDead)
                && (kind is null || row.AllKinds.Contains(kind.Value)))
            {
                Rows.Add(row);
            }
        }

        ShownText = Rows.Count == _allRows.Count
            ? string.Create(CultureInfo.CurrentCulture, $"{Rows.Count} settings")
            : string.Create(CultureInfo.CurrentCulture, $"{Rows.Count} of {_allRows.Count} settings");
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

    private void RefreshEnvironmentChoices()
    {
        var app = _scan?.Apps.FirstOrDefault(a => a.RelativePath == SelectedApp);
        var environments = (app?.Environments ?? []).ToList();
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

    private async Task ExportAsync(string suggestedName, Func<ProvenanceReport, string> render)
    {
        if (Dialogs is null || _lastResult is null)
        {
            return;
        }

        var path = await Dialogs.SaveFileAsync("Export the report", suggestedName).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, render(_lastResult.Report)).ConfigureAwait(true);
            StatusText = $"Saved {path}." + (RevealSecrets ? " It holds secret values: don't share it." : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not save {path}: {ex.Message}";
        }
    }

    private GitHubAccount CreateGitHub(string token) =>
        new(_http, string.IsNullOrWhiteSpace(GitHubApiUrl) ? GitHubAccount.DefaultApiBase : ParseUrl(GitHubApiUrl.Trim()), token);

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
        if (_currentDownload is null)
        {
            return;
        }

        try
        {
            Directory.Delete(_currentDownload, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover download in the temp folder is harmless.
        }

        _currentDownload = null;
    }

    private sealed record Download(string Folder, IReadOnlyList<string> Environments);

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _work?.Cancel();
        DeleteDownload();
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
