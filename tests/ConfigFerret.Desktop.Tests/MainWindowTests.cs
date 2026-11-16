using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ConfigFerret.Desktop.Credentials;
using ConfigFerret.Desktop.Model;
using ConfigFerret.Desktop.Services;
using ConfigFerret.Desktop.ViewModels;
using ConfigFerret.Desktop.Views;
using ConfigFerret.Tests.Shared;

namespace ConfigFerret.Desktop.Tests;

/// <summary>Drives the real window headless against a fake GitHub, from sign-in to the secrets audit.</summary>
public sealed class MainWindowTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("configferret-desktop-");

    public void Dispose() => _temp.Delete(recursive: true);

    private MainWindowViewModel CreateViewModel(HttpMessageHandler http, InMemoryCredentialStore credentials) =>
        new(
            new WorkspaceStore(Path.Join(_temp.FullName, "workspace.json")),
            credentials,
            http,
            new RepositoryAnalyzer(render: (_, _) => null),
            Path.Join(_temp.FullName, "downloads"));

    // Turning a source on or off starts a trace; awaiting a second one waits for it (it cancels the first).
    private static async Task WaitForTraceAsync(MainWindowViewModel viewModel)
    {
        await viewModel.RetraceAsync();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Signs_in_lists_repositories_and_traces_the_selected_one()
    {
        var github = new FakeGitHub();
        var credentials = new InMemoryCredentialStore();
        using var viewModel = CreateViewModel(github, credentials);
        var window = new MainWindow { DataContext = viewModel, Width = 1440, Height = 900 };
        window.Show();

        await viewModel.InitializeAsync();
        Assert.True(viewModel.ShowConnections);

        viewModel.GitHubTokenInput = FakeGitHub.Token;
        await viewModel.ConnectGitHubCommand.ExecuteAsync(null);
        Assert.Equal("ferret-fan", viewModel.GitHubLogin);
        Assert.Equal(string.Empty, viewModel.GitHubTokenInput);
        Assert.Equal(FakeGitHub.Token, credentials.Read(MainWindowViewModel.GitHubAccountName));
        Assert.Equal(3, viewModel.Repositories.Count);

        await viewModel.AnalyzeRepositoryAsync(viewModel.Repositories[0]);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.HasResult, string.Join(Environment.NewLine, viewModel.Problems.Select(p => p.Text)));
        Assert.Equal("src/Orders.Api", viewModel.SelectedApp);
        Assert.Equal("Production", viewModel.SelectedEnvironment);
        Assert.Equal("production", viewModel.SelectedGitHubEnvironment);

        // The Helm value reaches the app's setting through the rendered ConfigMap.
        var timeout = viewModel.Rows.Single(r => r.Key == "Db:Timeout");
        Assert.Equal("60", timeout.Setting.Value);
        Assert.Contains(timeout.Kinds, k => k.Kind == LayerKind.Helm);

        // Secrets: the maps key the deploy fills into the Secret is green, the Octopus-style placeholder needs a check, and
        // the API key committed in appsettings.Production.json and the password in the Secret manifest in deploy/rendered.yaml are red.
        Assert.Contains(viewModel.SecretRows, s => s.Key == "Maps:ApiKey" && s.Css == "good");
        Assert.Contains(viewModel.SecretRows, s => s.Key == "Db:Password" && s.Css == "bad");
        Assert.Contains(viewModel.SecretRows, s => s.Key == "Smtp:Password" && s.Css == "check");
        Assert.Contains(viewModel.SecretRows, s => s.Key == "Payments:ApiKey" && s.Css == "bad");
        Assert.Equal("bad", viewModel.SecretRows[0].Css);

        // One table holds everything: GitHub variables no setting picks up are rows of their own, marked dead.
        var legacy = viewModel.Rows.Single(r => r.Key == "LEGACY_ENDPOINT");
        Assert.False(legacy.IsSetting);
        Assert.True(legacy.IsDead);
        Assert.Contains(legacy.Kinds, k => k.Kind == LayerKind.GitHubActions);
        Assert.Contains(viewModel.Rows, r => r.Key == "OLD_DEPLOY_KEY" && r.IsDead);
        Assert.Contains(viewModel.Rows, r => r.Key == "features.recommendations" && r.IsDead && r.What == "Helm value");
        Assert.Equal(viewModel.RowCount, viewModel.Rows.Count);

        // Dead variables: a setting no code reads is flagged in the table.
        Assert.True(viewModel.Rows.Single(r => r.Key == "Payments:ApiKey").IsDead);
        Assert.False(viewModel.Rows.Single(r => r.Key == "Cache:Ttl").IsDead);

        // The ORDERS_ variable also sets its whole name; the app reads it without the prefix, so neither row is dead.
        Assert.False(viewModel.Rows.Single(r => r.Key == "Db:Password").IsDead);
        Assert.False(viewModel.Rows.Single(r => r.Key == "ORDERS_Db:Password").IsDead);

        // The stat tiles are the filters; picking the active one again shows every row.
        viewModel.SelectTileCommand.Execute("Dead");
        Assert.True(viewModel.IsDeadTile);
        Assert.All(viewModel.Rows, r => Assert.True(r.IsDead));
        Assert.DoesNotContain(viewModel.Rows, r => r.Key == "ORDERS_Db:Password");
        Assert.Equal(viewModel.DeadCount, viewModel.Rows.Count);
        // The host's unprefixed provider also reads the ORDERS_ variables under their whole name.
        viewModel.SelectTileCommand.Execute("Exposed");
        Assert.Equal(["Db:Password", "ORDERS_Db:Password", "Payments:ApiKey"], viewModel.Rows.Select(r => r.Key).Order(StringComparer.Ordinal));
        viewModel.SelectTileCommand.Execute("Protected");
        Assert.True(viewModel.ProtectedCount > 0);
        Assert.Equal(["Maps:ApiKey", "ORDERS_Maps:ApiKey"], viewModel.Rows.Select(r => r.Key).Order(StringComparer.Ordinal));
        Assert.Equal(viewModel.ProtectedCount, viewModel.Rows.Count);
        viewModel.SelectTileCommand.Execute("Overridden");
        Assert.All(viewModel.Rows, r => Assert.True(r.IsOverridden));
        viewModel.SelectTileCommand.Execute("Overridden");
        Assert.True(viewModel.IsAllTile);
        Assert.Equal(viewModel.RowCount, viewModel.Rows.Count);

        // Column filters: tick the values to show, search, sort.
        viewModel.LayersFilter.Only("GitHub");
        Assert.All(viewModel.Rows, r => Assert.Contains(r.Kinds, k => k.Kind == LayerKind.GitHubActions));
        Assert.True(viewModel.IsFiltered);
        viewModel.LayersFilter.Clear();
        viewModel.KeyFilter.Search = "Db";
        Assert.All(viewModel.Rows, r => Assert.Contains("Db", r.Key, StringComparison.OrdinalIgnoreCase));
        viewModel.KeyFilter.SortDescendingCommand.Execute(null);
        Assert.Equal(viewModel.Rows.Select(r => r.Key).OrderDescending(StringComparer.OrdinalIgnoreCase), viewModel.Rows.Select(r => r.Key));
        Assert.True(viewModel.KeyFilter.IsSorted);
        viewModel.ClearFiltersCommand.Execute(null);
        Assert.False(viewModel.IsFiltered);

        // Turning a source off in the Sources drop-down traces again without it, and the choice is kept.
        Assert.False(viewModel.Discoveries.Single(d => d.Title == "App settings").CanToggle);
        var helm = viewModel.Discoveries.Single(d => d.Title == "Helm chart");
        Assert.Equal("in use", helm.State);
        helm.IsOn = false;
        await WaitForTraceAsync(viewModel);
        Assert.DoesNotContain(viewModel.Rows.Single(r => r.Key == "Db:Timeout").Kinds, k => k.Kind == LayerKind.Helm);
        Assert.Equal("off", viewModel.Discoveries.Single(d => d.Title == "Helm chart").State);
        Assert.Contains("Helm chart", File.ReadAllText(Path.Join(_temp.FullName, "workspace.json")), StringComparison.Ordinal);
        viewModel.Discoveries.Single(d => d.Title == "Helm chart").IsOn = true;
        await WaitForTraceAsync(viewModel);
        timeout = viewModel.Rows.Single(r => r.Key == "Db:Timeout");
        Assert.Contains(timeout.Kinds, k => k.Kind == LayerKind.Helm);
        Assert.Null(viewModel.SortColumn);
        Assert.Equal(viewModel.RowCount, viewModel.Rows.Count);

        // The token went to GitHub only, and never into the workspace file.
        Assert.All(github.Requests, r => Assert.Equal("api.github.com", r.RequestUri!.Host));
        var saved = File.ReadAllText(Path.Join(_temp.FullName, "workspace.json"));
        Assert.DoesNotContain(FakeGitHub.Token, saved, StringComparison.Ordinal);

        viewModel.SelectedRow = timeout;
        Dispatcher.UIThread.RunJobs();
        await Screenshots.SaveAsync(window, "configuration");
        viewModel.SelectTileCommand.Execute("Exposed");
        viewModel.SelectedRow = viewModel.Rows[0];
        Dispatcher.UIThread.RunJobs();
        await Screenshots.SaveAsync(window, "secrets");
        viewModel.SelectTileCommand.Execute("Dead");
        viewModel.SelectedRow = viewModel.Rows.First(r => r.Key == "LEGACY_ENDPOINT");
        Dispatcher.UIThread.RunJobs();
        await Screenshots.SaveAsync(window, "unused");
        viewModel.ClearFiltersCommand.Execute(null);
        viewModel.SelectedRow = timeout;
        Dispatcher.UIThread.RunJobs();
        var layersButton = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("headerfilter")).ElementAt(3);
        layersButton.Flyout!.ShowAt(layersButton);
        Dispatcher.UIThread.RunJobs();
        await Screenshots.SaveAsync(window, "column-filter");
        layersButton.Flyout.Hide();
        viewModel.IsDarkTheme = false;
        Dispatcher.UIThread.RunJobs();
        await Screenshots.SaveAsync(window, "configuration-light");

        // Ctrl+B hides the sidebar, and the choice is kept for the next start.
        viewModel.IsDarkTheme = true;
        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.B, Avalonia.Input.RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.IsSidebarOpen);
        Assert.Contains("\"sidebarHidden\": true", File.ReadAllText(Path.Join(_temp.FullName, "workspace.json")), StringComparison.Ordinal);
        await Screenshots.SaveAsync(window, "sidebar-hidden");
        using (var reopened = CreateViewModel(new FakeGitHub(), new InMemoryCredentialStore()))
        {
            Assert.False(reopened.IsSidebarOpen);
        }

        viewModel.ToggleSidebarCommand.Execute(null);
        Assert.True(viewModel.IsSidebarOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Shows_the_welcome_and_the_connections_dialog_before_sign_in()
    {
        using var viewModel = CreateViewModel(new FakeGitHub(), new InMemoryCredentialStore());
        var window = new MainWindow { DataContext = viewModel, Width = 1440, Height = 900 };
        window.Show();
        await viewModel.InitializeAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.ShowWelcome);
        Assert.True(viewModel.ShowConnections);
        await Screenshots.SaveAsync(window, "connections");
        viewModel.ShowConnections = false;
        Dispatcher.UIThread.RunJobs();
        await Screenshots.SaveAsync(window, "welcome");
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_result_on_show_does_not_keep_the_rendered_manifests()
    {
        var repo = Path.Join(_temp.FullName, "shop");
        Directory.CreateDirectory(Path.Join(repo, "src", "Api"));
        File.WriteAllText(Path.Join(repo, "src", "Api", "appsettings.json"), """{ "Mode": "dev" }""");
        Directory.CreateDirectory(Path.Join(repo, "chart"));
        File.WriteAllText(Path.Join(repo, "chart", "Chart.yaml"), "name: api\n");
        const string rendered = "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: api\nspec:\n  template:\n    spec:\n      containers:\n        - name: api\n          image: api:1\n          env:\n            - name: Mode\n              value: rendered\n";
        using var viewModel = new MainWindowViewModel(
            new WorkspaceStore(Path.Join(_temp.FullName, "workspace.json")),
            new InMemoryCredentialStore(),
            analyzer: new RepositoryAnalyzer((tool, _) => tool == "helm" ? rendered : null),
            downloadRoot: Path.Join(_temp.FullName, "downloads"));

        await viewModel.AnalyzeFolderAsync(repo);
        Dispatcher.UIThread.RunJobs();

        // The trace used the rendered text, and the result shown for the life of the tab no longer holds it.
        Assert.Contains(viewModel.Rows, r => r.Key == "Mode" && r.Value == "rendered");
        var kubernetes = Assert.Single(Assert.IsType<TraceResult>(viewModel.LastResult).Sources.Select(s => s.Source).OfType<KubernetesSource>());
        Assert.Null(kubernetes.Rendered);
    }

    // No profile is saved: the workspace keeps the folder that was opened, and opening it again after a restart builds the
    // profile from it again, so a sibling project is followed both times.
    [AvaloniaFact]
    public async Task A_reference_to_a_sibling_project_is_followed_after_the_workspace_is_saved_and_loaded()
    {
        var repo = Path.Join(_temp.FullName, "shop");
        Directory.CreateDirectory(Path.Join(repo, "src", "Api"));
        Directory.CreateDirectory(Path.Join(repo, "src", "Shared"));
        File.WriteAllText(Path.Join(repo, "src", "Api", "Api.csproj"), """<Project><ItemGroup><ProjectReference Include="../Shared/Shared.csproj" /></ItemGroup></Project>""");
        File.WriteAllText(Path.Join(repo, "src", "Api", "appsettings.json"), """{ "Shared": { "Key": "x" }, "Unused": "y" }""");
        File.WriteAllText(Path.Join(repo, "src", "Api", "Program.cs"), """var a = configuration["A:B"];""");
        File.WriteAllText(Path.Join(repo, "src", "Shared", "Shared.csproj"), "<Project />");
        File.WriteAllText(Path.Join(repo, "src", "Shared", "Reader.cs"), """var s = configuration["Shared:Key"];""");
        var workspace = Path.Join(_temp.FullName, "workspace.json");

        using (var before = CreateViewModel(new FakeGitHub(), new InMemoryCredentialStore()))
        {
            await before.AnalyzeFolderAsync(repo);
            Dispatcher.UIThread.RunJobs();
            AssertSharedIsRead(before);
        }

        var saved = new WorkspaceStore(workspace).Load();
        Assert.Equal(Path.GetFullPath(repo), Assert.Single(saved.RecentFolders));
        using var after = CreateViewModel(new FakeGitHub(), new InMemoryCredentialStore());
        await after.InitializeAsync();
        await after.AnalyzeFolderAsync(saved.RecentFolders[0]);
        Dispatcher.UIThread.RunJobs();
        AssertSharedIsRead(after);

        static void AssertSharedIsRead(MainWindowViewModel viewModel)
        {
            Assert.True(viewModel.HasResult, string.Join(Environment.NewLine, viewModel.Problems.Select(p => p.Text)));
            Assert.False(viewModel.Rows.Single(r => r.Key == "Shared:Key").IsDead);
            Assert.True(viewModel.Rows.Single(r => r.Key == "Unused").IsDead);
            Assert.DoesNotContain(Assert.IsType<TraceResult>(viewModel.LastResult).Notes, n => n.Contains("outside", StringComparison.Ordinal));
        }
    }

    [AvaloniaFact]
    public async Task A_refused_token_is_not_saved()
    {
        var credentials = new InMemoryCredentialStore();
        using var viewModel = CreateViewModel(new FakeGitHub(), credentials);
        viewModel.GitHubTokenInput = "ghp_wrong";

        await viewModel.ConnectGitHubCommand.ExecuteAsync(null);

        Assert.Null(viewModel.GitHubLogin);
        Assert.Null(credentials.Read(MainWindowViewModel.GitHubAccountName));
        Assert.Contains("refused", viewModel.ConnectionError, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_wrong", viewModel.ConnectionError, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Picking_another_app_renders_its_own_chart_with_its_own_values()
    {
        var repo = Path.Join(_temp.FullName, "shop");
        foreach (var file in new[] { "src/Api/appsettings.json", "src/Worker/appsettings.json" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Join(repo, file))!);
            File.WriteAllText(Path.Join(repo, file), "{}");
        }

        foreach (var file in new[] { "charts/api/Chart.yaml", "charts/api/values-prod.yaml", "charts/worker/Chart.yaml", "charts/worker/values-prod.yaml" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Join(repo, file))!);
            File.WriteAllText(Path.Join(repo, file), "name: x\n");
        }

        var rendered = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var viewModel = new MainWindowViewModel(
            new WorkspaceStore(Path.Join(_temp.FullName, "workspace.json")),
            new InMemoryCredentialStore(),
            analyzer: new RepositoryAnalyzer((_, arguments) =>
            {
                rendered.Enqueue(string.Join(' ', arguments));
                return null;
            }),
            downloadRoot: Path.Join(_temp.FullName, "downloads"));
        await viewModel.AnalyzeFolderAsync(repo);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("charts/api/values-prod.yaml", viewModel.SelectedValuesFile);

        viewModel.SelectedApp = "src/Worker";
        await WaitForTraceAsync(viewModel);

        Assert.Equal("charts/worker/values-prod.yaml", viewModel.SelectedValuesFile);
        var worker = Path.GetFullPath(Path.Join(repo, "charts", "worker"));
        Assert.Equal($"template release {worker} -f {Path.Join(worker, "values-prod.yaml")}", rendered.Last());
    }

    [AvaloniaFact]
    public async Task The_new_apps_values_file_is_matched_to_the_environment_shown()
    {
        var repo = Path.Join(_temp.FullName, "shop");
        foreach (var file in new[] { "src/Api/appsettings.json", "src/Api/appsettings.Qa.json", "src/Worker/appsettings.json", "src/Worker/appsettings.Production.json" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Join(repo, file))!);
            File.WriteAllText(Path.Join(repo, file), "{}");
        }

        foreach (var file in new[] { "charts/api/Chart.yaml", "charts/worker/Chart.yaml", "charts/worker/values-qa.yaml", "charts/worker/values-prod.yaml" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Join(repo, file))!);
            File.WriteAllText(Path.Join(repo, file), "name: x\n");
        }

        using var viewModel = new MainWindowViewModel(
            new WorkspaceStore(Path.Join(_temp.FullName, "workspace.json")),
            new InMemoryCredentialStore(),
            analyzer: new RepositoryAnalyzer((_, _) => null),
            downloadRoot: Path.Join(_temp.FullName, "downloads"));
        await viewModel.AnalyzeFolderAsync(repo);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Qa", viewModel.SelectedEnvironment);

        // The Worker has no Qa settings, so the environment moves to Production, and the values file with it.
        viewModel.SelectedApp = "src/Worker";
        await WaitForTraceAsync(viewModel);

        Assert.Equal("Production", viewModel.SelectedEnvironment);
        Assert.Equal("charts/worker/values-prod.yaml", viewModel.SelectedValuesFile);
    }

    [AvaloniaFact]
    public async Task The_environment_the_container_sets_is_traced_until_one_is_picked_and_the_pick_is_kept()
    {
        var repo = Path.Join(_temp.FullName, "shop");
        foreach (var (file, text) in new[]
        {
            ("src/Api/appsettings.json", """{"Db":{"Timeout":"5"}}"""),
            ("src/Api/appsettings.Production.json", """{"Db":{"Timeout":"60"}}"""),
            ("src/Api/appsettings.Staging.json", """{"Db":{"Timeout":"15"}}"""),
            ("k8s/api.yaml", "apiVersion: v1\nkind: Pod\nmetadata:\n  name: api\nspec:\n  containers:\n    - name: api\n      image: api:1\n      env:\n        - name: ASPNETCORE_ENVIRONMENT\n          value: Staging\n"),
        })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Join(repo, file))!);
            File.WriteAllText(Path.Join(repo, file), text);
        }

        MainWindowViewModel Open() => new(
            new WorkspaceStore(Path.Join(_temp.FullName, "workspace.json")),
            new InMemoryCredentialStore(),
            analyzer: new RepositoryAnalyzer((_, _) => null),
            downloadRoot: Path.Join(_temp.FullName, "downloads"));
        string Timeout(MainWindowViewModel vm) => vm.Rows.Single(r => r.Key == "Db:Timeout").Setting.Value!;

        using (var viewModel = Open())
        {
            await viewModel.AnalyzeFolderAsync(repo);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("15", Timeout(viewModel));
            Assert.Equal("Staging", viewModel.SelectedEnvironment);
            Assert.Contains(viewModel.Problems, p => p.Text.Contains("traced in Staging, not Production", StringComparison.Ordinal));

            viewModel.SelectedEnvironment = "Production";
            await WaitForTraceAsync(viewModel);
            Assert.Equal("60", Timeout(viewModel));
        }

        using (var reopened = Open())
        {
            await reopened.AnalyzeFolderAsync(repo);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Production", reopened.SelectedEnvironment);
            Assert.Equal("60", Timeout(reopened));
        }
    }

    [AvaloniaFact]
    public async Task An_environment_the_container_takes_from_a_secret_is_shown_nowhere()
    {
        const string fromSecret = "Qa7FromTheSecret";
        var repo = Path.Join(_temp.FullName, "shop");
        foreach (var (file, text) in new[]
        {
            ("src/Api/appsettings.json", """{"Db":{"Timeout":"5"}}"""),
            ("src/Api/appsettings.Production.json", """{"Db":{"Timeout":"60"}}"""),
            ("k8s/api.yaml", $"apiVersion: v1\nkind: Secret\nmetadata:\n  name: env-secret\nstringData:\n  env: {fromSecret}\n---\n"
                + "apiVersion: v1\nkind: Pod\nmetadata:\n  name: api\nspec:\n  containers:\n    - name: api\n      image: api:1\n      env:\n        - name: ASPNETCORE_ENVIRONMENT\n          valueFrom:\n            secretKeyRef:\n              name: env-secret\n              key: env\n"),
        })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Join(repo, file))!);
            File.WriteAllText(Path.Join(repo, file), text);
        }

        var workspace = Path.Join(_temp.FullName, "workspace.json");
        using var viewModel = new MainWindowViewModel(
            new WorkspaceStore(workspace),
            new InMemoryCredentialStore(),
            analyzer: new RepositoryAnalyzer((_, _) => null),
            downloadRoot: Path.Join(_temp.FullName, "downloads"));
        await viewModel.AnalyzeFolderAsync(repo);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Production", viewModel.SelectedEnvironment);
        Assert.DoesNotContain(viewModel.Environments, e => e.Contains(fromSecret, StringComparison.Ordinal));
        Assert.Equal("60", viewModel.Rows.Single(r => r.Key == "Db:Timeout").Setting.Value);
        Assert.Contains(viewModel.Problems, p => p.Text.Contains("ASPNETCORE_ENVIRONMENT in the app's environment comes from a Secret", StringComparison.Ordinal));
        Assert.All(viewModel.Problems, p => Assert.DoesNotContain(fromSecret, p.Text, StringComparison.Ordinal));
        var shown = viewModel.Rows.SelectMany(r => r.Setting.Steps.Prepend(r.Setting.EffectiveStep!)).SelectMany(s => new[] { s?.Source, s?.Location, s?.Note, s?.Value });
        Assert.All(shown, text => Assert.DoesNotContain(fromSecret, text ?? string.Empty, StringComparison.Ordinal));
        Assert.Equal("Production", viewModel.LastResult!.Environment);
        Assert.DoesNotContain(fromSecret, File.ReadAllText(workspace), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_value_that_looks_like_a_credential_is_hidden_in_the_table_layers_and_export_until_revealed()
    {
        const string accountKey = "hunter2-fake-account-key";
        var repo = Path.Join(_temp.FullName, "jobs");
        Directory.CreateDirectory(Path.Join(repo, "src", "Jobs"));
        File.WriteAllText(
            Path.Join(repo, "src", "Jobs", "appsettings.json"),
            $$"""{"AzureWebJobsStorage": "DefaultEndpointsProtocol=https;AccountName=fake;AccountKey={{accountKey}};EndpointSuffix=core.windows.net"}""");
        var export = Path.Join(_temp.FullName, "report.json");
        var workspace = Path.Join(_temp.FullName, "workspace.json");
        using var viewModel = new MainWindowViewModel(
            new WorkspaceStore(workspace),
            new InMemoryCredentialStore(),
            analyzer: new RepositoryAnalyzer((_, _) => null),
            downloadRoot: Path.Join(_temp.FullName, "downloads"))
        {
            Dialogs = new SaveTo(export),
        };
        await viewModel.AnalyzeFolderAsync(repo);
        Dispatcher.UIThread.RunJobs();

        viewModel.SelectedRow = viewModel.Rows.Single(r => r.Key == "AzureWebJobsStorage");
        Assert.Equal("***", viewModel.SelectedRow.Value);
        Assert.All(Flatten(viewModel.Details), node => Assert.DoesNotContain(accountKey, node.Value, StringComparison.Ordinal));
        await viewModel.ExportJsonCommand.ExecuteAsync(null);
        Assert.DoesNotContain(accountKey, File.ReadAllText(export), StringComparison.Ordinal);
        Assert.False(viewModel.LastExportHoldsSecrets);
        Assert.All(viewModel.Problems, p => Assert.DoesNotContain(accountKey, p.Text, StringComparison.Ordinal));
        Assert.DoesNotContain(accountKey, File.ReadAllText(workspace), StringComparison.Ordinal);

        viewModel.RevealSecrets = true;
        await WaitForTraceAsync(viewModel);

        Assert.Contains(accountKey, viewModel.Rows.Single(r => r.Key == "AzureWebJobsStorage").Value, StringComparison.Ordinal);
        await viewModel.ExportJsonCommand.ExecuteAsync(null);
        Assert.Contains(accountKey, File.ReadAllText(export), StringComparison.Ordinal);
        Assert.True(viewModel.LastExportHoldsSecrets);

        static IEnumerable<StepNode> Flatten(IEnumerable<StepNode> nodes) => nodes.SelectMany(n => Flatten(n.Children).Prepend(n));
    }

    /// <summary>A save dialog that always picks the same file.</summary>
    private sealed class SaveTo(string path) : IDialogService
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedName) => Task.FromResult<string?>(path);
    }

    [Theory]
    [InlineData("[]", "did not say who")]
    [InlineData("\"ferret-fan\"", "did not say who")]
    [InlineData("""{"login":"\ud800"}""", "not valid UTF-16")]
    [InlineData("<html>", "not JSON")]
    public async Task An_answer_that_is_not_a_user_is_shown_instead_of_ending_the_app(string body, string expected)
    {
        var credentials = new InMemoryCredentialStore();
        using var viewModel = CreateViewModel(new FakeGitHub().Json("/user", body), credentials);
        viewModel.GitHubTokenInput = FakeGitHub.Token;

        await viewModel.ConnectGitHubCommand.ExecuteAsync(null);

        Assert.Null(viewModel.GitHubLogin);
        Assert.Null(credentials.Read(MainWindowViewModel.GitHubAccountName));
        Assert.Contains(expected, viewModel.ConnectionError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_saved_token_whose_answer_is_not_a_user_shows_the_connections()
    {
        var credentials = new InMemoryCredentialStore();
        credentials.Write(MainWindowViewModel.GitHubAccountName, FakeGitHub.Token);
        using var viewModel = CreateViewModel(new FakeGitHub().Json("/user", "[]"), credentials);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.ShowConnections);
        Assert.Contains("did not say who", viewModel.ConnectionError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_octopus_answer_that_is_not_a_list_of_spaces_is_shown_instead_of_ending_the_app()
    {
        var octopus = new FakeHttpHandler().Respond("/api/spaces/all", "[1]");
        using var viewModel = CreateViewModel(octopus, new InMemoryCredentialStore());
        viewModel.OctopusServer = "https://octopus.example.test/";
        viewModel.OctopusKeyInput = "fake-api-key";

        await viewModel.ConnectOctopusCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsOctopusConnected);
        Assert.Contains("no default space", viewModel.ConnectionError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_answer_larger_than_the_limit_is_refused_instead_of_buffered()
    {
        const long limit = ConfigFerret.Shared.JsonApi.MaxResponseBytes;
        using var handler = new LargeAnswerHandler(2 * limit);
        using var viewModel = CreateViewModel(handler, new InMemoryCredentialStore());
        viewModel.GitHubTokenInput = "fake-token";

        await viewModel.ConnectGitHubCommand.ExecuteAsync(null);

        Assert.Null(viewModel.GitHubLogin);
        Assert.NotNull(viewModel.ConnectionError);
        Assert.True(handler.BytesRead <= limit + (1024 * 1024), $"{handler.BytesRead} bytes were read");
    }

    [Fact]
    public async Task Reloading_the_repository_list_stops_the_load_still_running()
    {
        using var github = new FirstListWaits(new FakeGitHub());
        var credentials = new InMemoryCredentialStore();
        credentials.Write(MainWindowViewModel.GitHubAccountName, FakeGitHub.Token);
        using var viewModel = CreateViewModel(github, credentials);

        var first = viewModel.LoadRepositoriesAsync();
        await github.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await viewModel.LoadRepositoriesAsync();
        await first.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(3, viewModel.Repositories.Count);
        Assert.False(viewModel.IsLoadingRepositories);
        Assert.Null(viewModel.ConnectionError);
    }

    /// <summary>Holds the first request for the repository list until it is cancelled.</summary>
    private sealed class FirstListWaits(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int _lists;

        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/user/repos" && Interlocked.Increment(ref _lists) == 1)
            {
                Waiting.SetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Answers every request with a JSON user padded to more than <paramref name="padding"/> bytes, sent without a length.</summary>
    private sealed class LargeAnswerHandler(long padding) : HttpMessageHandler
    {
        private readonly List<PaddedJsonStream> _answers = [];

        /// <summary>How much of the answers was read, all requests together.</summary>
        public long BytesRead => _answers.Sum(a => a.Position);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Answer());

        // The client that sent the request owns the answer and disposes it.
        private HttpResponseMessage Answer()
        {
            var answer = new PaddedJsonStream(padding);
            _answers.Add(answer);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(answer) };
        }
    }

    /// <summary>{"login":"ferret-fan","padding":"xxx…"}, made as it is read, so the test holds no copy of it.</summary>
    private sealed class PaddedJsonStream(long padding) : Stream
    {
        private static readonly byte[] Start = "{\"login\":\"ferret-fan\",\"padding\":\""u8.ToArray();
        private static readonly byte[] End = "\"}"u8.ToArray();
        private readonly long _length = Start.Length + padding + End.Length;
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, _length - _position);
            for (var i = 0; i < count; i++, _position++)
            {
                buffer[i] = _position < Start.Length ? Start[_position]
                    : _position >= _length - End.Length ? End[_position - (_length - End.Length)]
                    : (byte)'x';
            }

            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Saves window screenshots to the folder in CONFIGFERRET_SCREENSHOTS, when it is set.</summary>
internal static class Screenshots
{
    public static Task SaveAsync(Window window, string name)
    {
        var folder = Environment.GetEnvironmentVariable("CONFIGFERRET_SCREENSHOTS");
        if (string.IsNullOrEmpty(folder))
        {
            return Task.CompletedTask;
        }

        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Directory.CreateDirectory(folder);
#pragma warning disable CS0618 // The replacement overload's options type differs between Avalonia versions.
        frame?.Save(Path.Join(folder, name + ".png"));
#pragma warning restore CS0618
        return Task.CompletedTask;
    }
}
