using ConfigFerret.Desktop.Services;

namespace ConfigFerret.Desktop.Tests;

public class RepositoryAnalyzerChoicesTests
{
    private static AppFolder App(string path, params string[] environments) => new("/repo/" + path, path, environments);

    private static HelmChartInfo Chart(string path, params string[] values) => new("/repo/" + path, path, values);

    private static RepositoryScan Scan(AppFolder[]? apps = null, HelmChartInfo[]? charts = null, string[]? kustomizations = null) =>
        new("/repo", apps ?? [], charts ?? [], kustomizations ?? [], [], [], false);

    [Fact]
    public void Choices_start_in_production_with_nothing_picked()
    {
        var choices = new AnalysisChoices();

        Assert.Equal("Production", choices.Environment);
        Assert.Null(choices.App);
        Assert.Null(choices.HelmChart);
        Assert.Null(choices.HelmValues);
        Assert.Null(choices.Kustomization);
        Assert.Null(choices.GitHubEnvironment);
        Assert.Null(choices.OctopusProject);
        Assert.Null(choices.OctopusEnvironment);
    }

    [Fact]
    public void Suggesting_needs_a_scan_choices_and_a_list_of_environments()
    {
        Assert.Throws<ArgumentNullException>(() => RepositoryAnalyzer.SuggestChoices(null!, new AnalysisChoices(), []));
        Assert.Throws<ArgumentNullException>(() => RepositoryAnalyzer.SuggestChoices(Scan(), null!, []));
        Assert.Throws<ArgumentNullException>(() => RepositoryAnalyzer.SuggestChoices(Scan(), new AnalysisChoices(), null!));
    }

    [Fact]
    public void Suggesting_from_an_empty_scan_leaves_everything_unset()
    {
        var choices = new AnalysisChoices { App = "gone", HelmChart = "gone", Kustomization = "gone", GitHubEnvironment = "gone" };

        RepositoryAnalyzer.SuggestChoices(Scan(), choices, []);

        Assert.Null(choices.App);
        Assert.Null(choices.HelmChart);
        Assert.Null(choices.Kustomization);
        Assert.Null(choices.GitHubEnvironment);
        Assert.Equal("Production", choices.Environment);
    }

    [Fact]
    public void An_app_that_is_still_there_is_kept_and_a_missing_one_gives_way_to_the_first()
    {
        var scan = Scan(apps: [App("a"), App("b")]);
        var kept = new AnalysisChoices { App = "b" };
        var replaced = new AnalysisChoices { App = "gone" };
        var none = new AnalysisChoices();

        RepositoryAnalyzer.SuggestChoices(scan, kept, []);
        RepositoryAnalyzer.SuggestChoices(scan, replaced, []);
        RepositoryAnalyzer.SuggestChoices(scan, none, []);

        Assert.Equal("b", kept.App);
        Assert.Equal("a", replaced.App);
        Assert.Equal("a", none.App);
    }

    [Theory]
    [InlineData("Staging", new[] { "Development", "Staging", "Production" }, "Staging")]
    [InlineData("staging", new[] { "Development", "Staging" }, "staging")]
    [InlineData("Qa", new[] { "Development", "Staging", "Production" }, "Production")]
    [InlineData("Qa", new[] { "Development", "Staging" }, "Staging")]
    [InlineData("Qa", new[] { "development" }, "Qa")]
    [InlineData("Qa", new string[0], "Qa")]
    public void The_environment_follows_what_the_app_has_settings_for(string current, string[] available, string expected)
    {
        var choices = new AnalysisChoices { App = "a", Environment = current };

        RepositoryAnalyzer.SuggestChoices(Scan(apps: [App("a", available)]), choices, []);

        Assert.Equal(expected, choices.Environment);
    }

    [Fact]
    public void The_environment_is_left_alone_when_there_is_no_app()
    {
        var choices = new AnalysisChoices { Environment = "Qa" };

        RepositoryAnalyzer.SuggestChoices(Scan(), choices, []);

        Assert.Equal("Qa", choices.Environment);
    }

    [Fact]
    public void A_chart_that_is_still_there_is_kept_and_a_missing_one_gives_way_to_the_first()
    {
        var scan = Scan(charts: [Chart("a"), Chart("b")]);
        var kept = new AnalysisChoices { HelmChart = "b" };
        var replaced = new AnalysisChoices { HelmChart = "gone" };
        var none = new AnalysisChoices();

        RepositoryAnalyzer.SuggestChoices(scan, kept, []);
        RepositoryAnalyzer.SuggestChoices(scan, replaced, []);
        RepositoryAnalyzer.SuggestChoices(scan, none, []);

        Assert.Equal("b", kept.HelmChart);
        Assert.Equal("a", replaced.HelmChart);
        Assert.Equal("a", none.HelmChart);
    }

    [Fact]
    public void A_values_file_of_the_chart_is_kept_and_anything_else_is_replaced()
    {
        var scan = Scan(charts: [Chart("c", "v/values-dev.yaml", "v/values-prod.yaml")]);
        var kept = new AnalysisChoices { HelmValues = "v/values-dev.yaml" };
        var replaced = new AnalysisChoices { HelmValues = "v/values-gone.yaml" };
        var none = new AnalysisChoices();

        RepositoryAnalyzer.SuggestChoices(scan, kept, []);
        RepositoryAnalyzer.SuggestChoices(scan, replaced, []);
        RepositoryAnalyzer.SuggestChoices(scan, none, []);

        Assert.Equal("v/values-dev.yaml", kept.HelmValues);
        Assert.Equal("v/values-prod.yaml", replaced.HelmValues);
        Assert.Equal("v/values-prod.yaml", none.HelmValues);
    }

    [Fact]
    public void Values_files_are_matched_by_file_name_not_folder()
    {
        var scan = Scan(charts: [Chart("c", "prod/values-eu.yaml", "other/values-prod.yaml")]);
        var choices = new AnalysisChoices();

        RepositoryAnalyzer.SuggestChoices(scan, choices, []);

        Assert.Equal("other/values-prod.yaml", choices.HelmValues);
    }

    [Theory]
    [InlineData("Production", "values-production.yaml", "values-prod.yaml")]
    [InlineData("Production", "values-prod.yaml", "values-eu.yaml")]
    [InlineData("Development", "values-dev.yaml", "values-eu.yaml")]
    [InlineData("Staging", "values-stag.yaml", "values-eu.yaml")]
    [InlineData("Staging", "values-staging.yaml", "values-stag.yaml")]
    [InlineData("Qa", "values-qa.yaml", "values-eu.yaml")]
    public void A_values_file_is_matched_by_the_environment_or_its_short_form(string environment, string expected, string other)
    {
        var scan = Scan(charts: [Chart("c", other, expected)]);
        var choices = new AnalysisChoices { Environment = environment };

        RepositoryAnalyzer.SuggestChoices(scan, choices, []);

        Assert.Equal(expected, choices.HelmValues);
    }

    [Fact]
    public void The_full_environment_name_beats_its_short_form()
    {
        var scan = Scan(charts: [Chart("c", "values-prod.yaml", "values-production.yaml")]);
        var choices = new AnalysisChoices();

        RepositoryAnalyzer.SuggestChoices(scan, choices, []);

        Assert.Equal("values-production.yaml", choices.HelmValues);
    }

    [Fact]
    public void No_values_file_is_picked_when_none_matches()
    {
        var scan = Scan(charts: [Chart("c", "values-eu.yaml")]);
        var choices = new AnalysisChoices { HelmValues = "values-gone.yaml" };

        RepositoryAnalyzer.SuggestChoices(scan, choices, []);

        Assert.Null(choices.HelmValues);
    }

    [Fact]
    public void A_kustomization_that_is_still_there_is_kept()
    {
        var choices = new AnalysisChoices { Kustomization = "k8s/base" };

        RepositoryAnalyzer.SuggestChoices(Scan(kustomizations: ["k8s/overlays/prod", "k8s/base"]), choices, []);

        Assert.Equal("k8s/base", choices.Kustomization);
    }

    [Theory]
    [InlineData("Production", "k8s/overlays/prod")]
    [InlineData("Development", "k8s/overlays/dev")]
    [InlineData("Qa", "k8s/overlays/prod")]
    public void A_missing_kustomization_is_matched_by_environment_else_the_first(string environment, string expected)
    {
        var choices = new AnalysisChoices { Kustomization = "k8s/gone", Environment = environment };

        RepositoryAnalyzer.SuggestChoices(Scan(kustomizations: ["k8s/overlays/prod", "k8s/overlays/dev", "k8s/base"]), choices, []);

        Assert.Equal(expected, choices.Kustomization);
    }

    [Fact]
    public void An_unset_kustomization_is_matched_too_and_stays_unset_without_any()
    {
        var unset = new AnalysisChoices();
        var none = new AnalysisChoices { Kustomization = "k8s/gone" };

        RepositoryAnalyzer.SuggestChoices(Scan(kustomizations: ["k8s/base", "k8s/prod"]), unset, []);
        RepositoryAnalyzer.SuggestChoices(Scan(), none, []);

        Assert.Equal("k8s/prod", unset.Kustomization);
        Assert.Null(none.Kustomization);
    }

    // Two apps, each with its own chart and overlay. Every chart is offered every values file but its own values.yaml.
    private static RepositoryScan TwoApps() => Scan(
        apps: [App("src/Api", "Production"), App("src/Worker", "Production")],
        charts:
        [
            Chart("charts/api", "charts/api/values-prod.yaml", "charts/worker/values-prod.yaml"),
            Chart("charts/worker", "charts/api/values-prod.yaml", "charts/worker/values-prod.yaml"),
        ],
        kustomizations: ["deploy/api/overlays/production", "deploy/worker/overlays/production", "deploy/api/base", "deploy/worker/base"]);

    [Theory]
    [InlineData("src/Api", "charts/api", "deploy/api/overlays/production")]
    [InlineData("src/Worker", "charts/worker", "deploy/worker/overlays/production")]
    public void Each_app_gets_the_chart_values_file_and_overlay_named_like_it(string app, string chart, string overlay)
    {
        var choices = new AnalysisChoices { App = app };

        RepositoryAnalyzer.SuggestChoices(TwoApps(), choices, []);

        Assert.Equal((chart, $"{chart}/values-prod.yaml", overlay), (choices.HelmChart, choices.HelmValues, choices.Kustomization));
    }

    [Fact]
    public void Picking_another_app_moves_the_chart_values_file_and_overlay_along()
    {
        var choices = new AnalysisChoices { App = "src/Api" };
        RepositoryAnalyzer.SuggestChoices(TwoApps(), choices, []);

        choices.App = "src/Worker";
        RepositoryAnalyzer.SuggestChoices(TwoApps(), choices, []);

        Assert.Equal(("charts/worker", "charts/worker/values-prod.yaml", "deploy/worker/overlays/production"), (choices.HelmChart, choices.HelmValues, choices.Kustomization));
    }

    [Fact]
    public void The_charts_own_values_files_come_before_other_charts_ones()
    {
        var choices = new AnalysisChoices { HelmChart = "charts/worker" };

        RepositoryAnalyzer.SuggestChoices(Scan(charts: TwoApps().HelmCharts.ToArray()), choices, []);

        Assert.Equal("charts/worker/values-prod.yaml", choices.HelmValues);
    }

    [Theory]
    [InlineData("src/Orders.Api", "charts/orders")]
    [InlineData("src/OrdersApi", "charts/orders")]
    [InlineData("src/billing", "charts/billing")]
    [InlineData("src/Shipping", "charts/billing")]
    public void Charts_are_matched_by_the_words_of_the_app_folder_else_the_first_is_used(string app, string expected)
    {
        var choices = new AnalysisChoices();

        RepositoryAnalyzer.SuggestChoices(Scan(apps: [App(app)], charts: [Chart("charts/billing"), Chart("charts/orders")]), choices, []);

        Assert.Equal(expected, choices.HelmChart);
    }

    [Fact]
    public void A_base_is_not_taken_for_an_app_whose_name_ends_in_base()
    {
        var choices = new AnalysisChoices { App = "src/Database" };

        RepositoryAnalyzer.SuggestChoices(Scan(apps: [App("src/Database")], kustomizations: ["k8s/overlays/prod", "k8s/base"]), choices, []);

        Assert.Equal("k8s/overlays/prod", choices.Kustomization);
    }

    [Theory]
    [InlineData("src/Api", "k8s/base/api")]
    [InlineData("src/Orders", "k8s/orders")]
    public void The_overlay_for_the_environment_wins_over_a_base_named_like_the_app(string app, string appBase)
    {
        // One base per app and overlays shared by them: only the base carries the app's name.
        var scan = Scan(apps: [App(app, "Production")], kustomizations: ["k8s/overlays/production", "k8s/overlays/staging", appBase, "k8s/base/worker"]);
        var unset = new AnalysisChoices { App = app };
        var saved = new AnalysisChoices { App = app, Kustomization = "k8s/overlays/production" };

        RepositoryAnalyzer.SuggestChoices(scan, unset, []);
        RepositoryAnalyzer.SuggestChoices(scan, saved, []);

        Assert.Equal("k8s/overlays/production", unset.Kustomization);
        Assert.Equal("k8s/overlays/production", saved.Kustomization);
    }

    [Theory]
    [InlineData("src/Products", "Production", "k8s/products")]
    [InlineData("src/DevPortal", "Development", "k8s/dev-portal")]
    public void A_base_whose_app_name_holds_the_short_environment_name_does_not_beat_the_overlay(string app, string environment, string appBase)
    {
        // prod is part of products and dev of dev-portal, but only the overlay is named for the environment.
        var overlay = $"k8s/overlays/{environment.ToLowerInvariant()}";
        var scan = Scan(apps: [App(app, environment)], kustomizations: [overlay, "k8s/overlays/staging", appBase]);
        var unset = new AnalysisChoices { App = app, Environment = environment };
        var saved = new AnalysisChoices { App = app, Environment = environment, Kustomization = overlay };

        RepositoryAnalyzer.SuggestChoices(scan, unset, []);
        RepositoryAnalyzer.SuggestChoices(scan, saved, []);

        Assert.Equal(overlay, unset.Kustomization);
        Assert.Equal(overlay, saved.Kustomization);
    }

    [Fact]
    public void A_values_file_named_for_the_environment_beats_one_next_to_the_chart_named_for_the_app()
    {
        var choices = new AnalysisChoices { App = "src/Products", Environment = "Production" };

        RepositoryAnalyzer.SuggestChoices(Scan(apps: [App("src/Products", "Production")], charts: [Chart("charts/products", "charts/products/products-values.yaml", "deploy/values-production.yaml")]), choices, []);

        Assert.Equal("deploy/values-production.yaml", choices.HelmValues);
    }

    [Fact]
    public void An_app_at_the_root_is_not_matched_by_the_name_of_the_folder_it_was_downloaded_to()
    {
        // A downloaded repository's folder ends in a random id, whose letters and digits would count as words.
        foreach (var folder in new[] { "contoso_shop-be0123456789abcdef0123456789abcdef", "contoso_shop-fe0123456789abcdef0123456789abcdef" })
        {
            var scan = new RepositoryScan("/tmp/" + folder, [new AppFolder("/tmp/" + folder, ".", [])], [Chart("charts/be", "charts/be/values-prod.yaml"), Chart("charts/fe", "charts/fe/values-prod.yaml")], [], [], [], false);
            var choices = new AnalysisChoices { App = ".", HelmChart = "charts/fe", HelmValues = "charts/fe/values-prod.yaml" };

            RepositoryAnalyzer.SuggestChoices(scan, choices, []);

            Assert.Equal(("charts/fe", "charts/fe/values-prod.yaml"), (choices.HelmChart, choices.HelmValues));
        }
    }

    [Fact]
    public void A_github_environment_that_still_exists_is_kept_and_others_are_matched()
    {
        var kept = new AnalysisChoices { GitHubEnvironment = "qa" };
        var matched = new AnalysisChoices { GitHubEnvironment = "gone" };
        var unset = new AnalysisChoices();

        RepositoryAnalyzer.SuggestChoices(Scan(), kept, ["staging", "qa", "production"]);
        RepositoryAnalyzer.SuggestChoices(Scan(), matched, ["staging", "production"]);
        RepositoryAnalyzer.SuggestChoices(Scan(), unset, ["staging", "prod-eu"]);

        Assert.Equal("qa", kept.GitHubEnvironment);
        Assert.Equal("production", matched.GitHubEnvironment);
        Assert.Equal("prod-eu", unset.GitHubEnvironment);
    }
}
