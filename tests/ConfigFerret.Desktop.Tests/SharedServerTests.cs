using System.Net;
using System.Text;
using ConfigFerret.Desktop.Credentials;
using ConfigFerret.Desktop.Model;
using ConfigFerret.Desktop.Services;
using ConfigFerret.Desktop.ViewModels;

namespace ConfigFerret.Desktop.Tests;

/// <summary>
/// On a server for other people (the web app), the tokens in the server's environment belong to whoever runs it: they
/// go only to the addresses the server was set up with, whatever a visitor types.
/// </summary>
public sealed class SharedServerTests : IDisposable
{
    private const string ServerToken = "server-token-for-tests";
    private const string ServerKey = "server-octopus-key-for-tests";
    private const string OwnKey = "visitor-octopus-key-for-tests";
    private const string ServerOctopus = "https://octopus.example.test/";

    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("configferret-shared-server-");

    public void Dispose() => _temp.Delete(recursive: true);

    private string WorkspacePath => Path.Join(_temp.FullName, "workspace.json");

    private static EnvironmentFallbackCredentialStore Credentials(string? gitHubToken = null, string? octopusKey = null)
    {
        var environment = new Dictionary<string, string?> { ["GITHUB_TOKEN"] = gitHubToken, ["OCTOPUS_API_KEY"] = octopusKey };
        return new EnvironmentFallbackCredentialStore(
            new InMemoryCredentialStore(),
            new Dictionary<string, string[]>
            {
                [MainWindowViewModel.GitHubAccountName] = ["GITHUB_TOKEN"],
                [MainWindowViewModel.OctopusAccountName] = ["OCTOPUS_API_KEY"],
            },
            name => environment.GetValueOrDefault(name));
    }

    private MainWindowViewModel CreateViewModel(HttpMessageHandler github, ICredentialStore credentials, bool sharedServer = true) =>
        new(new WorkspaceStore(WorkspacePath), credentials, github, new RepositoryAnalyzer(render: (_, _) => null), Path.Join(_temp.FullName, "downloads"))
        {
            IsSharedServer = sharedServer,
            OctopusServerFallback = ServerOctopus,
        };

    private static List<HttpRequestMessage> To(FakeGitHub github, string host)
    {
        lock (github.Requests)
        {
            return github.Requests.Where(r => string.Equals(r.RequestUri!.Host, host, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    private static bool Carries(HttpRequestMessage request, string secret) =>
        request.Headers.Authorization?.Parameter == secret
        || (request.Headers.TryGetValues("X-Octopus-ApiKey", out var keys) && keys.Contains(secret, StringComparer.Ordinal));

    /// <summary>An Octopus server that knows the visitor's own key, in front of the fake GitHub.</summary>
    private sealed class WithOctopus(FakeGitHub github) : DelegatingHandler(github)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!Carries(request, OwnKey))
            {
                return base.SendAsync(request, cancellationToken);
            }

            var body = request.RequestUri!.AbsolutePath == "/api/spaces/all" ? """[{"Id":"Spaces-1","Name":"Default","IsDefault":true}]""" : "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task The_servers_token_is_not_sent_to_an_address_a_visitor_types()
    {
        var github = new FakeGitHub();
        using var viewModel = CreateViewModel(github, Credentials(gitHubToken: FakeGitHub.Token));

        viewModel.GitHubApiUrl = "https://other.example/";
        await viewModel.ReloadRepositoriesCommand.ExecuteAsync(null);

        Assert.DoesNotContain(To(github, "other.example"), r => Carries(r, FakeGitHub.Token));
        Assert.Contains("api.github.com", viewModel.ConnectionError, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeGitHub.Token, viewModel.ConnectionError, StringComparison.Ordinal);
        Assert.Empty(viewModel.Repositories);
    }

    [Fact]
    public async Task The_servers_token_still_works_with_github()
    {
        var github = new FakeGitHub();
        using var viewModel = CreateViewModel(github, Credentials(gitHubToken: FakeGitHub.Token));

        await viewModel.InitializeAsync();

        Assert.Equal("ferret-fan", viewModel.GitHubLogin);
        Assert.Equal(3, viewModel.Repositories.Count);
        Assert.All(github.Requests, r => Assert.Equal("api.github.com", r.RequestUri!.Host));
    }

    [Fact]
    public async Task A_visitors_own_token_goes_to_the_address_they_type()
    {
        var github = new FakeGitHub();
        using var viewModel = CreateViewModel(github, Credentials(gitHubToken: ServerToken));

        viewModel.GitHubApiUrl = "https://ghe.example.test/";
        viewModel.GitHubTokenInput = FakeGitHub.Token;
        await viewModel.ConnectGitHubCommand.ExecuteAsync(null);

        Assert.Equal("ferret-fan", viewModel.GitHubLogin);
        Assert.Equal(3, viewModel.Repositories.Count);
        Assert.All(github.Requests, r => Assert.Equal("ghe.example.test", r.RequestUri!.Host));
        Assert.DoesNotContain(github.Requests, r => Carries(r, ServerToken));
    }

    [Fact]
    public async Task One_visitors_address_does_not_reach_another_tab()
    {
        using var repo = new TempFolder();
        repo.Write("src/Api/appsettings.json", """{ "Db": { "Host": "localhost" } }""");
        using var first = new WithOctopus(new FakeGitHub());
        using (var viewModel = CreateViewModel(first, Credentials(gitHubToken: FakeGitHub.Token, octopusKey: ServerKey)))
        {
            viewModel.GitHubApiUrl = "https://ghe.example.test/";
            viewModel.GitHubTokenInput = FakeGitHub.Token;
            await viewModel.ConnectGitHubCommand.ExecuteAsync(null);
            Assert.Equal("ferret-fan", viewModel.GitHubLogin);
            viewModel.OctopusServer = "https://octopus.other.example/";
            viewModel.OctopusKeyInput = OwnKey;
            await viewModel.ConnectOctopusCommand.ExecuteAsync(null);
            Assert.True(viewModel.IsOctopusConnected, viewModel.ConnectionError);
        }

        var second = new FakeGitHub();
        using var next = CreateViewModel(second, Credentials(gitHubToken: FakeGitHub.Token, octopusKey: ServerKey));
        await next.InitializeAsync();
        await next.AnalyzeFolderAsync(repo.Path);

        Assert.Equal(string.Empty, next.GitHubApiUrl);
        Assert.Equal(ServerOctopus, next.OctopusServer);
        Assert.Contains(second.Requests, r => r.RequestUri!.Host == "api.github.com");
        Assert.DoesNotContain(second.Requests, r => r.RequestUri!.Host == "ghe.example.test");
        Assert.Contains(second.Requests, r => Carries(r, ServerKey));
        Assert.All(second.Requests.Where(r => Carries(r, ServerKey)), r => Assert.Equal("octopus.example.test", r.RequestUri!.Host));
        var saved = File.ReadAllText(WorkspacePath);
        Assert.DoesNotContain("ghe.example.test", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("octopus.other.example", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void Addresses_an_older_version_saved_are_dropped_from_the_workspace_file()
    {
        new WorkspaceStore(WorkspacePath).Save(new Workspace
        {
            GitHubApiUrl = "https://ghe.example.test/",
            OctopusServer = "https://octopus.other.example/",
            OctopusSpace = "space-from-an-older-version",
        });
        using var viewModel = CreateViewModel(new FakeGitHub(), Credentials());

        viewModel.ToggleThemeCommand.Execute(null);

        var saved = File.ReadAllText(WorkspacePath);
        Assert.DoesNotContain("ghe.example.test", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("octopus.other.example", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("space-from-an-older-version", saved, StringComparison.Ordinal);
        Assert.Equal(string.Empty, viewModel.OctopusSpace);
    }

    [Fact]
    public async Task The_servers_octopus_key_is_only_sent_to_the_servers_octopus()
    {
        using var repo = new TempFolder();
        repo.Write("src/Api/appsettings.json", """{ "Db": { "Host": "localhost" } }""");
        var github = new FakeGitHub();
        using var viewModel = CreateViewModel(github, Credentials(octopusKey: ServerKey));
        Assert.True(viewModel.IsOctopusConnected);

        viewModel.OctopusServer = "https://other.example/";
        await viewModel.AnalyzeFolderAsync(repo.Path);

        Assert.DoesNotContain(To(github, "other.example"), r => Carries(r, ServerKey));

        viewModel.OctopusServer = ServerOctopus;
        await viewModel.AnalyzeFolderAsync(repo.Path);
        Assert.Contains(To(github, "octopus.example.test"), r => Carries(r, ServerKey));
    }

    [Fact]
    public async Task Disconnecting_does_not_send_the_servers_token_to_the_visitors_address()
    {
        var github = new FakeGitHub();
        var credentials = Credentials(gitHubToken: ServerToken);
        using var viewModel = CreateViewModel(github, credentials);
        viewModel.GitHubApiUrl = "https://ghe.example.test/";
        viewModel.GitHubTokenInput = FakeGitHub.Token;
        await viewModel.ConnectGitHubCommand.ExecuteAsync(null);
        await viewModel.AnalyzeRepositoryAsync(viewModel.Repositories[0]);
        Assert.True(viewModel.HasResult, string.Join(Environment.NewLine, viewModel.Problems.Select(p => p.Text)));

        viewModel.DisconnectGitHubCommand.Execute(null);
        await viewModel.RetraceAsync();

        Assert.DoesNotContain(github.Requests, r => Carries(r, ServerToken));
        Assert.Contains(viewModel.Problems, p => p.Text.Contains("api.github.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task On_the_desktop_the_environment_token_is_the_users_own()
    {
        var github = new FakeGitHub();
        using var viewModel = CreateViewModel(github, Credentials(gitHubToken: FakeGitHub.Token), sharedServer: false);

        viewModel.GitHubApiUrl = "https://ghe.example.test/";
        await viewModel.ReloadRepositoriesCommand.ExecuteAsync(null);

        Assert.Equal(3, viewModel.Repositories.Count);
        Assert.All(github.Requests, r => Assert.Equal("ghe.example.test", r.RequestUri!.Host));
    }
}
