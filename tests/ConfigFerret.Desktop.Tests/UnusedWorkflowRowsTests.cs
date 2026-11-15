using ConfigFerret.Desktop.Credentials;
using ConfigFerret.Desktop.Services;
using ConfigFerret.Desktop.ViewModels;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Desktop.Tests;

public sealed class UnusedWorkflowRowsTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("configferret-workflow-rows-");

    public void Dispose() => _temp.Delete(recursive: true);

    [Fact]
    public void Github_variables_only_a_workflow_uses_are_listed_as_workflow_rows_not_dead_or_overridden()
    {
        var report = ConfigTracer.For(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["A"] = "1" }).Build()).Trace();
        var result = new TraceResult(report, report.FindUnused(), [], [], [], [new UnusedVariable("GitHub Actions", "OLD_KEY", "repository secret (contoso/web)")])
        {
            WorkflowVariables = [new WorkflowVariable("AZURE_WEBAPP_PUBLISH_PROFILE", "environment secret (production)", "deploy.yml line 31")],
        };
        using var viewModel = new MainWindowViewModel(new WorkspaceStore(Path.Join(_temp.FullName, "workspace.json")), new InMemoryCredentialStore());

        viewModel.ShowResult(result, []);

        var row = viewModel.Unused.Single(u => u.Name == "AZURE_WEBAPP_PUBLISH_PROFILE");
        Assert.True(row.IsWorkflowOnly);
        Assert.False(row.IsDead);
        Assert.False(row.IsNeverWins);
        Assert.False(row.CanShow);
        Assert.Equal("environment secret (production)", row.Where);
        Assert.Equal("Used by deploy.yml line 31, not by an app setting", row.Detail);
        Assert.Equal(1, viewModel.DeadCount);
        Assert.Equal(0, viewModel.NeverWinsCount);
        Assert.Equal(1, viewModel.WorkflowOnlyCount);
        Assert.Equal(1, viewModel.UnusedCount);
        viewModel.OnlyDead = true;
        Assert.Equal("OLD_KEY", Assert.Single(viewModel.Unused).Name);
    }
}
