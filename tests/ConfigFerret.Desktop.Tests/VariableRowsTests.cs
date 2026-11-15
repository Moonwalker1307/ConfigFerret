using ConfigFerret.Desktop.Credentials;
using ConfigFerret.Desktop.Services;
using ConfigFerret.Desktop.ViewModels;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Desktop.Tests;

/// <summary>GitHub and Octopus variables no setting picks up, and Helm values no template reads, are rows in the one table.</summary>
public sealed class VariableRowsTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("configferret-variable-rows-");

    public void Dispose() => _temp.Delete(recursive: true);

    private MainWindowViewModel Show(TraceResult result)
    {
        var viewModel = new MainWindowViewModel(new WorkspaceStore(Path.Join(_temp.FullName, "workspace.json")), new InMemoryCredentialStore());
        viewModel.ShowResult(result, []);
        return viewModel;
    }

    private static TraceResult Result()
    {
        var report = ConfigTracer.For(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["A"] = "1" }).Build()).Trace();
        return new TraceResult(
            report,
            report.FindUnused(),
            [],
            [],
            [],
            [
                new UnusedVariable("GitHub Actions", "OLD_KEY", "repository secret (contoso/web)") { IsSecret = true },
                new UnusedVariable("Octopus Deploy", "Legacy.Url", "Web") { Value = "https://old" },
            ])
        {
            WorkflowVariables =
            [
                new WorkflowVariable("AZURE_WEBAPP_PUBLISH_PROFILE", "environment secret (production)", "deploy.yml line 31") { IsSecret = true },
                new WorkflowVariable("AZURE_WEBAPP_NAME", "repository variable (contoso/web)", "deploy.yml line 12") { Value = "elwiz" },
            ],
            DeadHelmValues = [new DeadHelmValue("features.old", "values.yaml")],
        };
    }

    [Fact]
    public void Github_variables_only_a_workflow_uses_are_workflow_rows_not_dead()
    {
        using var viewModel = Show(Result());

        var row = viewModel.Rows.Single(r => r.Key == "AZURE_WEBAPP_PUBLISH_PROFILE");
        Assert.False(row.IsSetting);
        Assert.True(row.IsWorkflowOnly);
        Assert.False(row.IsDead);
        Assert.True(row.IsSensitive);
        Assert.Equal("***", row.Value);
        Assert.Equal("GITHUB ACTIONS SECRET", row.Caption);
        Assert.Equal("GitHub Actions environment secret (production)", row.ComesFrom);
        Assert.Equal("Used by deploy.yml line 31, not by an app setting.", row.WorkflowText);
        Assert.Equal(LayerKind.GitHubActions, Assert.Single(row.Kinds).Kind);
        Assert.Equal("elwiz", viewModel.Rows.Single(r => r.Key == "AZURE_WEBAPP_NAME").Value);
        Assert.Equal(2, viewModel.WorkflowOnlyCount);
        Assert.True(viewModel.HasWorkflowOnly);

        viewModel.SelectTileCommand.Execute("Workflow");
        Assert.Equal(["AZURE_WEBAPP_PUBLISH_PROFILE", "AZURE_WEBAPP_NAME"], viewModel.Rows.Select(r => r.Key));
    }

    [Fact]
    public void Unused_variables_and_helm_values_are_dead_rows()
    {
        using var viewModel = Show(Result());

        Assert.Equal(6, viewModel.RowCount);
        Assert.Equal(1, viewModel.TotalCount);
        Assert.Equal(3, viewModel.DeadCount);
        viewModel.SelectTileCommand.Execute("Dead");
        Assert.Equal(["OLD_KEY", "Legacy.Url", "features.old"], viewModel.Rows.Select(r => r.Key));
        var octopus = viewModel.Rows.Single(r => r.Key == "Legacy.Url");
        Assert.Equal("https://old", octopus.Value);
        Assert.Equal("No setting in this trace uses it", octopus.DeadReason);
        Assert.Equal("Dead: nothing uses it", octopus.DeadLabel);
        Assert.Equal(LayerKind.OctopusDeploy, Assert.Single(octopus.Kinds).Kind);
        var helm = viewModel.Rows.Single(r => r.Key == "features.old");
        Assert.Equal("Helm value", helm.What);
        Assert.Equal("values.yaml", helm.ComesFrom);
        Assert.Equal("No setting in this trace or workflow uses it", viewModel.Rows.Single(r => r.Key == "OLD_KEY").DeadReason);
        Assert.Equal(viewModel.DeadCount, viewModel.Rows.Count);
    }

    [Fact]
    public void A_tick_list_unticked_before_a_retrace_stays_unticked()
    {
        using var viewModel = Show(Result());
        var octopus = viewModel.LayersFilter.AllChoices.Single(c => c.Text == "Octopus");
        octopus.IsChecked = false;
        Assert.DoesNotContain(viewModel.Rows, r => r.Key == "Legacy.Url");
        Assert.True(viewModel.LayersFilter.IsActive);
        Assert.Equal("3 of 4 ticked", viewModel.LayersFilter.TickedText);

        viewModel.ShowResult(Result(), []);

        Assert.False(viewModel.LayersFilter.AllChoices.Single(c => c.Text == "Octopus").IsChecked);
        Assert.DoesNotContain(viewModel.Rows, r => r.Key == "Legacy.Url");
    }

    [Fact]
    public void Column_search_narrows_the_tick_list_and_the_rows()
    {
        using var viewModel = Show(Result());
        viewModel.ComesFromFilter.Search = "github";

        Assert.All(viewModel.ComesFromFilter.Choices, c => Assert.Contains("GitHub", c.Text, StringComparison.Ordinal));
        Assert.Equal(3, viewModel.Rows.Count);

        viewModel.ComesFromFilter.SelectNoneCommand.Execute(null);
        Assert.Empty(viewModel.Rows);
        Assert.True(viewModel.ShowNoMatches);
        viewModel.ComesFromFilter.SelectAllCommand.Execute(null);
        Assert.Equal(3, viewModel.Rows.Count);
        viewModel.ComesFromFilter.Clear();
        Assert.Equal(viewModel.RowCount, viewModel.Rows.Count);
        Assert.False(viewModel.ShowNoMatches);
    }

    [Fact]
    public void Sorting_by_a_column_and_back_to_the_trace_order()
    {
        using var viewModel = Show(Result());
        var traceOrder = viewModel.Rows.Select(r => r.Key).ToList();

        viewModel.KeyFilter.SortAscendingCommand.Execute(null);
        Assert.Equal(traceOrder.Order(StringComparer.OrdinalIgnoreCase), viewModel.Rows.Select(r => r.Key));
        Assert.Equal(TableColumn.Key, viewModel.SortColumn);
        Assert.False(viewModel.SortDescending);

        viewModel.ValueFilter.SortDescendingCommand.Execute(null);
        Assert.False(viewModel.KeyFilter.IsSorted);
        Assert.True(viewModel.ValueFilter.IsDescending);
        Assert.Equal(viewModel.Rows.Select(r => r.Value).OrderDescending(StringComparer.OrdinalIgnoreCase), viewModel.Rows.Select(r => r.Value));

        viewModel.ClearFiltersCommand.Execute(null);
        Assert.Equal(traceOrder, viewModel.Rows.Select(r => r.Key));
    }

    [Fact]
    public void The_search_finds_github_variables_by_name()
    {
        using var viewModel = Show(Result());

        viewModel.FilterText = "azure_webapp";

        Assert.Equal(["AZURE_WEBAPP_PUBLISH_PROFILE", "AZURE_WEBAPP_NAME"], viewModel.Rows.Select(r => r.Key));
        viewModel.FilterText = "deploy.yml";
        Assert.Equal(2, viewModel.Rows.Count);
    }
}
