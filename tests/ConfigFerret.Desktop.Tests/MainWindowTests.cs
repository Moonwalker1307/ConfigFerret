using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ConfigFerret.Desktop.Credentials;
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

        // Secrets: the password from a Kubernetes Secret is green, the Octopus-style placeholder needs a check, and the
        // API key committed in appsettings.Production.json is red.
        Assert.Contains(viewModel.SecretRows, s => s.Key == "Db:Password" && s.Css == "good");
        Assert.Contains(viewModel.SecretRows, s => s.Key == "Smtp:Password" && s.Css == "check");
        Assert.Contains(viewModel.SecretRows, s => s.Key == "Payments:ApiKey" && s.Css == "bad");
        Assert.Equal("bad", viewModel.SecretRows[0].Css);

        // Unused: overridden values and GitHub variables no setting picks up.
        Assert.Contains(viewModel.Unused, u => u.Name == "LEGACY_ENDPOINT");
        Assert.Contains(viewModel.Unused, u => u.Name == "OLD_DEPLOY_KEY");

        // The token went to GitHub only, and never into the workspace file.
        Assert.All(github.Requests, r => Assert.Equal("api.github.com", r.RequestUri!.Host));
        var saved = File.ReadAllText(Path.Join(_temp.FullName, "workspace.json"));
        Assert.DoesNotContain(FakeGitHub.Token, saved, StringComparison.Ordinal);

        await Screenshots.SaveAsync(window, "repository-overview");
        viewModel.SelectedTab = 1;
        viewModel.SelectedRow = timeout;
        Dispatcher.UIThread.RunJobs();
        await Screenshots.SaveAsync(window, "configuration");
        viewModel.SelectedTab = 2;
        Dispatcher.UIThread.RunJobs();
        await Screenshots.SaveAsync(window, "secrets");
        viewModel.SelectedTab = 3;
        Dispatcher.UIThread.RunJobs();
        await Screenshots.SaveAsync(window, "unused");
        viewModel.IsDarkTheme = false;
        viewModel.SelectedTab = 1;
        Dispatcher.UIThread.RunJobs();
        await Screenshots.SaveAsync(window, "configuration-light");
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
