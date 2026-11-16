using System.Net;
using ConfigFerret.Desktop.ViewModels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ConfigFerret.Web.Tests;

public sealed class HostTests : IClassFixture<HostTests.App>
{
    private readonly App _app;

    public HostTests(App app) => _app = app;

    [Fact]
    public async Task Serves_the_page_with_security_headers()
    {
        using var client = _app.CreateClient();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<title>ConfigFerret</title>", html, StringComparison.Ordinal);
        Assert.Contains("_framework/blazor.web.js", html, StringComparison.Ordinal);
        var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", policy, StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }

    [Fact]
    public async Task Answers_the_health_check()
    {
        using var client = _app.CreateClient();

        Assert.Equal("ok", await client.GetStringAsync(new Uri("/healthz", UriKind.Relative), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:8080")]
    [InlineData("http://127.0.0.1:5080")]
    [InlineData("http://[::1]:8080")]
    public async Task Answers_the_loopback_host_names(string address)
    {
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(address) });

        using var page = await client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);
        using var circuit = await NegotiateCircuitAsync(client);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(HttpStatusCode.OK, circuit.StatusCode);
    }

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("http://evil.example:8080")]
    [InlineData("http://localhost.evil.example:8080")]
    [InlineData("http://192.168.1.20:8080")]
    public async Task Refuses_other_host_names(string address)
    {
        // A page whose name is re-pointed at 127.0.0.1 is the same origin as the app to the browser, but its requests
        // still carry its own name.
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(address) });

        using var page = await client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);
        using var script = await client.GetAsync(new Uri("/_framework/blazor.web.js", UriKind.Relative), TestContext.Current.CancellationToken);
        using var circuit = await NegotiateCircuitAsync(client);

        Assert.Equal(HttpStatusCode.BadRequest, page.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, script.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, circuit.StatusCode);
    }

    [Fact]
    public async Task Answers_the_host_names_set_in_AllowedHosts()
    {
        using var app = _app.WithWebHostBuilder(builder => builder.UseSetting("AllowedHosts", "ferret.corp"));
        using var named = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://ferret.corp") });
        using var local = app.CreateClient();

        using var namedPage = await named.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);
        using var localPage = await local.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, namedPage.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, localPage.StatusCode);
    }

    [Fact]
    public async Task A_star_in_AllowedHosts_answers_any_host_name()
    {
        using var app = _app.WithWebHostBuilder(builder => builder.UseSetting("AllowedHosts", "*"));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://evil.example") });

        using var page = await client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    [Fact]
    public async Task Answers_the_health_check_for_any_host_name_and_nothing_else()
    {
        // Kubernetes and load balancers probe the container by its IP address.
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://10.1.2.3:8080") });

        using var health = await client.GetAsync(new Uri("/healthz", UriKind.Relative), TestContext.Current.CancellationToken);
        using var below = await client.GetAsync(new Uri("/healthz/x", UriKind.Relative), TestContext.Current.CancellationToken);
        using var post = await client.PostAsync(new Uri("/healthz", UriKind.Relative), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("ok", await health.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("nosniff", Assert.Single(health.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(HttpStatusCode.BadRequest, below.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Theory]
    [InlineData("/brand/icon.png")]
    [InlineData("/brand/logo.png")]
    public async Task Serves_the_logo_and_icon(string path)
    {
        using var client = _app.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes[..4]);
    }

    [Fact]
    public void Each_tab_gets_its_own_view_model_and_dialogs()
    {
        using var first = _app.Services.CreateScope();
        using var second = _app.Services.CreateScope();

        var viewModel = first.ServiceProvider.GetRequiredService<MainWindowViewModel>();

        Assert.Same(viewModel, first.ServiceProvider.GetRequiredService<MainWindowViewModel>());
        Assert.NotSame(viewModel, second.ServiceProvider.GetRequiredService<MainWindowViewModel>());
        Assert.Same(first.ServiceProvider.GetRequiredService<Services.WebDialogs>(), viewModel.Dialogs);
        Assert.Equal("server's memory for this browser tab", viewModel.CredentialStoreName);
    }

    [Fact]
    public void The_servers_tokens_are_kept_to_the_servers_own_addresses()
    {
        using var scope = _app.Services.CreateScope();

        var viewModel = scope.ServiceProvider.GetRequiredService<MainWindowViewModel>();

        Assert.True(viewModel.IsSharedServer);
        Assert.Equal(string.Empty, viewModel.GitHubApiUrl);
    }

    [Fact]
    public void The_workspace_file_goes_in_the_data_folder()
    {
        var options = new Services.WebAppOptions { DataFolder = "/data" };

        Assert.Equal(Path.Join("/data", "workspace.json"), options.WorkspacePath);
        Assert.Equal(Desktop.Services.WorkspaceStore.DefaultPath, new Services.WebAppOptions().WorkspacePath);
    }

    /// <summary>Starts a Blazor circuit's connection, the request the page's script makes first.</summary>
    private static Task<HttpResponseMessage> NegotiateCircuitAsync(HttpClient client) =>
        client.PostAsync(new Uri("/_blazor/negotiate?negotiateVersion=1", UriKind.Relative), content: null, TestContext.Current.CancellationToken);

    /// <summary>The app, with its data in a temporary folder.</summary>
    public sealed class App : WebApplicationFactory<Program>
    {
        private readonly DirectoryInfo _data = Directory.CreateTempSubdirectory("configferret-web-data-");

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseSetting("ConfigFerret:DataFolder", _data.FullName).UseEnvironment("Development");

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _data.Delete(recursive: true);
            }
        }
    }
}
