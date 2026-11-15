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
    public void The_workspace_file_goes_in_the_data_folder()
    {
        var options = new Services.WebAppOptions { DataFolder = "/data" };

        Assert.Equal(Path.Join("/data", "workspace.json"), options.WorkspacePath);
        Assert.Equal(Desktop.Services.WorkspaceStore.DefaultPath, new Services.WebAppOptions().WorkspacePath);
    }

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
