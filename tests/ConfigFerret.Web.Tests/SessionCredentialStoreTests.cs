using ConfigFerret.Desktop.Credentials;
using ConfigFerret.Desktop.ViewModels;
using ConfigFerret.Web.Services;

namespace ConfigFerret.Web.Tests;

public sealed class SessionCredentialStoreTests
{
    [Fact]
    public void Keeps_credentials_until_deleted()
    {
        var store = new SessionCredentialStore();

        Assert.Null(store.Read(MainWindowViewModel.GitHubAccountName));
        store.Write(MainWindowViewModel.GitHubAccountName, "ghp_one");
        store.Write(MainWindowViewModel.GitHubAccountName, "ghp_two");
        Assert.Equal("ghp_two", store.Read(MainWindowViewModel.GitHubAccountName));

        store.Delete(MainWindowViewModel.GitHubAccountName);
        Assert.Null(store.Read(MainWindowViewModel.GitHubAccountName));
        store.Delete(MainWindowViewModel.GitHubAccountName);
    }

    [Fact]
    public void Each_tab_has_its_own_store()
    {
        var first = new SessionCredentialStore();
        var second = new SessionCredentialStore();

        first.Write(MainWindowViewModel.GitHubAccountName, "ghp_first");

        Assert.Null(second.Read(MainWindowViewModel.GitHubAccountName));
    }

    [Fact]
    public void A_typed_token_wins_over_the_environment_and_disconnecting_falls_back_to_it()
    {
        var environment = new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_server" };
        var store = new EnvironmentFallbackCredentialStore(
            new SessionCredentialStore(),
            new Dictionary<string, string[]> { [MainWindowViewModel.GitHubAccountName] = ["GITHUB_TOKEN"] },
            environment.GetValueOrDefault);

        Assert.Equal("ghp_server", store.Read(MainWindowViewModel.GitHubAccountName));
        store.Write(MainWindowViewModel.GitHubAccountName, "ghp_typed");
        Assert.Equal("ghp_typed", store.Read(MainWindowViewModel.GitHubAccountName));
        store.Delete(MainWindowViewModel.GitHubAccountName);
        Assert.Equal("ghp_server", store.Read(MainWindowViewModel.GitHubAccountName));
    }

    [Fact]
    public void Says_where_credentials_are_kept() =>
        Assert.Equal("server's memory for this browser tab", new SessionCredentialStore().DisplayName);
}
