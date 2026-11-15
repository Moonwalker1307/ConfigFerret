using ConfigFerret.Desktop.Services;
using ConfigFerret.Desktop.ViewModels;

namespace ConfigFerret.Desktop.Tests;

public class RowsTests
{
    private static RepositoryRow Row(string fullName = "contoso/orders", string description = "", string language = "", DateTimeOffset? pushed = null) =>
        new(new RepositoryInfo(fullName, description, "main", false, language, pushed));

    [Fact]
    public void Repository_row_splits_the_full_name()
    {
        var row = Row("contoso/orders", "Orders API", "C#");

        Assert.Equal("contoso", row.Owner);
        Assert.Equal("orders", row.Name);
        Assert.Equal("contoso/orders", row.FullName);
        Assert.Equal("Orders API", row.Description);
        Assert.True(row.HasDescription);
        Assert.Equal("C#", row.Language);
        Assert.True(row.HasLanguage);
        Assert.Equal("O", row.Initial);
    }

    [Fact]
    public void Repository_row_reports_missing_description_and_language()
    {
        var row = Row();

        Assert.False(row.HasDescription);
        Assert.False(row.HasLanguage);
    }

    [Fact]
    public void Repository_row_reports_private()
    {
        var row = new RepositoryRow(new RepositoryInfo("a/b", "", "main", true, "", null));

        Assert.True(row.IsPrivate);
        Assert.False(Row().IsPrivate);
    }

    [Fact]
    public void Repository_row_needs_a_repository()
    {
        Assert.Throws<ArgumentNullException>(() => new RepositoryRow(null!));
    }

    [Fact]
    public void Initial_is_a_question_mark_for_an_empty_name()
    {
        Assert.Equal("?", Row("contoso/").Initial);
    }

    [Theory]
    [InlineData(0, "updated just now")]
    [InlineData(59, "updated just now")]
    [InlineData(61, "updated 1h ago")]
    [InlineData(23 * 60, "updated 23h ago")]
    [InlineData(25 * 60, "updated 1d ago")]
    [InlineData(30 * 24 * 60, "updated 30d ago")]
    [InlineData(59 * 24 * 60, "updated 59d ago")]
    [InlineData(61 * 24 * 60, "updated 2mo ago")]
    [InlineData(400 * 24 * 60, "updated 13mo ago")]
    [InlineData(729 * 24 * 60, "updated 24mo ago")]
    [InlineData(800 * 24 * 60, "updated 2y ago")]
    [InlineData(1500 * 24 * 60, "updated 4y ago")]
    public void Pushed_tells_how_long_ago(int minutesAgo, string expected)
    {
        var row = Row(pushed: DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).AddSeconds(-20));

        Assert.Equal(expected, row.Pushed);
    }

    [Fact]
    public void Pushed_says_empty_for_a_repository_never_pushed_to()
    {
        Assert.Equal("empty", Row().Pushed);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("ORDERS", true)]
    [InlineData("contoso/ord", true)]
    [InlineData("billing api", true)]
    [InlineData("nothing", false)]
    public void Repository_row_matches_the_search_text(string? filter, bool expected)
    {
        var row = Row("contoso/orders", "The Billing API");

        Assert.Equal(expected, row.Matches(filter));
    }

    [Fact]
    public void Discovery_card_state_follows_use()
    {
        Assert.Equal("in use", new SourceToggle("css", "icon", "Helm chart", "deploy/chart", true).State);
        Assert.Equal("found", new SourceToggle("css", "icon", "Helm chart", "deploy/chart", false).State);
        Assert.Equal("off", new SourceToggle("css", "icon", "Helm chart", "deploy/chart", true, isOn: false, changed: _ => { }).State);
        Assert.False(new SourceToggle("css", "icon", "App settings", "src/Api", true).CanToggle);
    }

    [Theory]
    [InlineData(SecretRating.Exposed, "bad", "Exposed", 0)]
    [InlineData(SecretRating.Check, "check", "Check", 1)]
    [InlineData(SecretRating.Protected, "good", "Secret store", 2)]
    [InlineData(SecretRating.NotSecret, "none", "Not secret", 3)]
    public void Secret_row_shows_the_rating(SecretRating rating, string css, string label, int order)
    {
        var row = new SecretRow(new SecretFinding("Db:Password", rating, "because", "appsettings.json"));

        Assert.Equal(css, row.Css);
        Assert.Equal(label, row.Label);
        Assert.Equal(order, row.Order);
        Assert.Equal("Db:Password", row.Key);
        Assert.Equal("because", row.Reason);
        Assert.Equal("appsettings.json", row.Where);
    }

    [Fact]
    public void Secret_row_needs_a_finding()
    {
        Assert.Throws<ArgumentNullException>(() => new SecretRow(null!));
    }

    [Theory]
    [InlineData(LayerKind.DotNetProvider, ".NET", "dotnet")]
    [InlineData(LayerKind.KubernetesWorkload, "Workload", "k8s")]
    [InlineData(LayerKind.KubernetesConfigMap, "ConfigMap", "k8s")]
    [InlineData(LayerKind.KubernetesSecret, "Secret", "secret")]
    [InlineData(LayerKind.KubernetesField, "Field", "k8s")]
    [InlineData(LayerKind.Helm, "Helm", "helm")]
    [InlineData(LayerKind.Kustomize, "Kustomize", "kustomize")]
    [InlineData(LayerKind.GitHubActions, "GitHub", "github")]
    [InlineData(LayerKind.OctopusDeploy, "Octopus", "octopus")]
    public void Layer_kinds_have_a_label_and_colour(LayerKind kind, string label, string css)
    {
        Assert.Equal(label, LayerKinds.ShortName(kind));
        Assert.Equal(css, LayerKinds.CssClass(kind));
        var badge = new KindBadge(kind);
        Assert.Equal(label, badge.Label);
        Assert.Equal(css, badge.Css);
    }

    [Fact]
    public void Unknown_layer_kinds_fall_back()
    {
        Assert.Equal("99", LayerKinds.ShortName((LayerKind)99));
        Assert.Equal("dotnet", LayerKinds.CssClass((LayerKind)99));
    }
}
