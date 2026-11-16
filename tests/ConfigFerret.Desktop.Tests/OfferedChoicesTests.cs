using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ConfigFerret.Desktop.Credentials;
using ConfigFerret.Desktop.Services;
using ConfigFerret.Desktop.ViewModels;

namespace ConfigFerret.Desktop.Tests;

/// <summary>
/// The environment and Helm values picks name files to read. In the web app they are whatever the browser sends, so
/// only the ones offered for the repository are traced, and only from inside it.
/// </summary>
public sealed class OfferedChoicesTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("configferret-choices-");

    public OfferedChoicesTests()
    {
        Write("repo/src/Api/appsettings.json", """{ "Db": { "Host": "localhost" } }""");
        Write("repo/deploy/chart/Chart.yaml", "apiVersion: v2\nname: api\nversion: 1.0.0\n");
        Write("repo/deploy/chart/values.yaml", "replicas: 1\n");
        Write("repo/deploy/chart/templates/configmap.yaml", "kind: ConfigMap\n");
        Write("repo/deploy/values-prod.yaml", "prod:\n  note: offered\n");
        Write("outside/private.json", """{ "OtherTeam": { "Endpoint": "https://internal.example.test" } }""");
        Write("outside/values.yaml", "outside:\n  note: not-offered\n");
    }

    public void Dispose() => _root.Delete(recursive: true);

    private string Repo => Path.Join(_root.FullName, "repo");

    private string WorkspacePath => Path.Join(_root.FullName, "workspace.json");

    private void Write(string relativePath, string text)
    {
        var file = Path.Join(_root.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    private async Task<MainWindowViewModel> OpenAsync(bool traced = true)
    {
        var viewModel = new MainWindowViewModel(
            new WorkspaceStore(WorkspacePath),
            new InMemoryCredentialStore(),
            analyzer: new RepositoryAnalyzer(render: (_, _) => null),
            downloadRoot: Path.Join(_root.FullName, "downloads"));
        await viewModel.AnalyzeFolderAsync(Repo);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(traced, viewModel.HasResult);
        return viewModel;
    }

    // Picking a choice starts a trace; awaiting a second one waits for it (it cancels the first).
    private static async Task PickAsync(MainWindowViewModel viewModel, Action pick)
    {
        pick();
        await viewModel.RetraceAsync();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task An_environment_the_app_does_not_offer_is_not_traced()
    {
        using var viewModel = await OpenAsync();

        await PickAsync(viewModel, () => viewModel.SelectedEnvironment = "x/../../../../outside/private");

        Assert.DoesNotContain(viewModel.Rows, r => r.Key.StartsWith("OtherTeam", StringComparison.Ordinal));
        Assert.Contains(viewModel.Problems, p => p.Text.Contains("environment", StringComparison.Ordinal));
        Assert.DoesNotContain(_root.FullName, viewModel.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("outside", File.ReadAllText(WorkspacePath), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task An_offered_environment_is_traced_whatever_its_case()
    {
        Write("repo/src/Api/appsettings.Staging.json", """{ "Db": { "Host": "staging-db" } }""");
        using var viewModel = await OpenAsync();

        await PickAsync(viewModel, () => viewModel.SelectedEnvironment = "staging");

        Assert.Equal("staging-db", viewModel.Rows.Single(r => r.Key == "Db:Host").Setting.Value);
    }

    [AvaloniaFact]
    public async Task A_values_file_the_chart_does_not_offer_is_not_traced()
    {
        using var viewModel = await OpenAsync();
        var offered = viewModel.ValuesFiles.Single(v => v.EndsWith("values-prod.yaml", StringComparison.Ordinal));

        await PickAsync(viewModel, () => viewModel.SelectedValuesFile = "../outside/values.yaml");

        Assert.DoesNotContain(viewModel.Rows, r => r.Key.StartsWith("outside", StringComparison.Ordinal));
        Assert.Contains(viewModel.Problems, p => p.Text.Contains("values file", StringComparison.Ordinal));

        await PickAsync(viewModel, () => viewModel.SelectedValuesFile = offered);
        Assert.Contains(viewModel.Rows, r => r.Key == "prod.note");
    }

    [AvaloniaFact]
    public async Task A_values_file_that_links_out_of_the_repository_is_not_traced()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.CreateSymbolicLink(Path.Join(Repo, "deploy", "values-linked.yaml"), Path.Join(_root.FullName, "outside", "values.yaml"));
        using var viewModel = await OpenAsync();
        var linked = viewModel.ValuesFiles.Single(v => v.EndsWith("values-linked.yaml", StringComparison.Ordinal));

        await PickAsync(viewModel, () => viewModel.SelectedValuesFile = linked);

        Assert.DoesNotContain(viewModel.Rows, r => r.Key.StartsWith("outside", StringComparison.Ordinal));
        Assert.Contains(viewModel.Problems, p => p.Text.Contains("values file", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task An_environment_whose_settings_link_out_of_the_repository_is_not_traced()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Write("repo/src/Api/appsettings.Production.json", "{}");
        File.CreateSymbolicLink(Path.Join(Repo, "src", "Api", "appsettings.Linked.json"), Path.Join(_root.FullName, "outside", "private.json"));
        using var viewModel = await OpenAsync();
        Assert.Contains("Linked", viewModel.Environments);

        await PickAsync(viewModel, () => viewModel.SelectedEnvironment = "Linked");

        Assert.DoesNotContain(viewModel.Rows, r => r.Key.StartsWith("OtherTeam", StringComparison.Ordinal));
        Assert.Contains(viewModel.Problems, p => p.Text.Contains("environment", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task An_environment_named_with_spaces_is_not_offered()
    {
        Write("repo/src/Api/appsettings. Staging.json", "{}");

        using var viewModel = await OpenAsync();

        Assert.DoesNotContain(viewModel.Environments, e => e.Contains(' ', StringComparison.Ordinal));
        Assert.DoesNotContain(viewModel.Problems, p => p.Severity == "error");
    }

    [AvaloniaFact]
    public async Task An_environment_named_with_spaces_is_not_checked_as_one_file_and_read_as_another()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Write("repo/src/Api/appsettings. Production.json", "{}");
        File.CreateSymbolicLink(Path.Join(Repo, "src", "Api", "appsettings.Production.json"), Path.Join(_root.FullName, "outside", "private.json"));
        using var viewModel = await OpenAsync(traced: false);
        Assert.DoesNotContain(" Production", viewModel.Environments);

        await PickAsync(viewModel, () => viewModel.SelectedEnvironment = " Production");

        Assert.DoesNotContain(viewModel.Rows, r => r.Key.StartsWith("OtherTeam", StringComparison.Ordinal));
        Assert.False(viewModel.HasResult);
        Assert.Contains(viewModel.Problems, p => p.Text.Contains("environment", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task A_refused_pick_does_not_leave_the_last_repositorys_result_on_screen()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Write("other/src/Web/appsettings.json", """{ "From": "OTHER" }""");
        File.CreateSymbolicLink(Path.Join(_root.FullName, "other", "src", "Web", "appsettings.Production.json"), Path.Join(_root.FullName, "outside", "private.json"));
        using var viewModel = await OpenAsync();
        Assert.Contains(viewModel.Rows, r => r.Key == "Db:Host");

        await viewModel.AnalyzeFolderAsync(Path.Join(_root.FullName, "other"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("other", viewModel.SubjectTitle);
        Assert.False(viewModel.HasResult);
        Assert.Empty(viewModel.Rows);
        Assert.Equal(0, viewModel.RowCount);
        Assert.False(viewModel.ExportJsonCommand.CanExecute(null));
        Assert.DoesNotContain(viewModel.Discoveries, d => d.Title == "Helm chart");
        Assert.Contains(viewModel.Problems, p => p.Text.Contains("environment", StringComparison.Ordinal));
    }
}
