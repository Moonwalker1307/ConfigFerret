using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using ConfigFerret.Desktop.Credentials;
using ConfigFerret.Desktop.Services;
using ConfigFerret.Desktop.ViewModels;
using ConfigFerret.Desktop.Views;

namespace ConfigFerret.Desktop;

/// <summary>The application.</summary>
public partial class App : Application
{
    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // A token in GITHUB_TOKEN (or an Octopus key in OCTOPUS_API_KEY) works without saving it.
            var credentials = new EnvironmentFallbackCredentialStore(
                CredentialStores.ForCurrentPlatform(),
                new Dictionary<string, string[]>
                {
                    [MainWindowViewModel.GitHubAccountName] = ["GITHUB_TOKEN", "GH_TOKEN"],
                    [MainWindowViewModel.OctopusAccountName] = ["OCTOPUS_API_KEY", "OCTOPUS_CLI_API_KEY"],
                });
            var viewModel = new MainWindowViewModel(new WorkspaceStore(WorkspaceStore.DefaultPath), credentials)
            {
                OctopusServerFallback = Environment.GetEnvironmentVariable("OCTOPUS_URL") ?? Environment.GetEnvironmentVariable("OCTOPUS_CLI_SERVER"),
            };
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => viewModel.Dispose();
            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Switches between the dark and light themes.</summary>
    public static void ApplyTheme(bool dark)
    {
        if (Current is { } app)
        {
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }
}
