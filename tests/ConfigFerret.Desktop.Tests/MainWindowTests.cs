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

namespace ConfigFerret.Desktop.Tests;

/// <summary>Drives the real window headless against a fake GitHub, from sign-in to the secrets audit.</summary>
public sealed class MainWindowTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("configferret-desktop-");

    public void Dispose() => _temp.Delete(recursive: true);

    private MainWindowViewModel CreateViewModel(FakeGitHub github, InMemoryCredentialStore credentials) =>
        new(
            new WorkspaceStore(Path.Join(_temp.FullName, "workspace.json")),
            credentials,
            github,
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

        // The stat tiles are the filters; picking the active one again shows every row.
        viewModel.SelectTileCommand.Execute("Dead");
        Assert.True(viewModel.IsDeadTile);
        Assert.All(viewModel.Rows, r => Assert.True(r.IsDead));
        Assert.Equal(viewModel.DeadCount, viewModel.Rows.Count);
        viewModel.SelectTileCommand.Execute("Exposed");
        Assert.Equal(["Db:Password", "Payments:ApiKey"], viewModel.Rows.Select(r => r.Key).Order(StringComparer.Ordinal));
        viewModel.SelectTileCommand.Execute("Protected");
        Assert.True(viewModel.ProtectedCount > 0);
        Assert.Equal("Maps:ApiKey", Assert.Single(viewModel.Rows).Key);
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
