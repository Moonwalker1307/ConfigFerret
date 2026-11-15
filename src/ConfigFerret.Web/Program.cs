using ConfigFerret.Desktop.Credentials;
using ConfigFerret.Desktop.Services;
using ConfigFerret.Desktop.ViewModels;
using ConfigFerret.Web;
using ConfigFerret.Web.Components;
using ConfigFerret.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

// `ConfigFerret.Web healthcheck` asks the running server if it is up. The image's HEALTHCHECK uses it, as the image has
// no curl or wget.
if (args is ["healthcheck"])
{
    return await HealthCheck.RunAsync().ConfigureAwait(false);
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<WebAppOptions>(builder.Configuration.GetSection(WebAppOptions.Section));
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Keeps the keys that protect Blazor's circuit state next to the workspace, so a restart doesn't break open tabs'
// reconnects. Without a data folder they stay in memory and the default location.
var dataFolder = builder.Configuration.GetSection(WebAppOptions.Section).Get<WebAppOptions>()?.DataFolder;
if (!string.IsNullOrWhiteSpace(dataFolder))
{
    builder.Services.AddDataProtection()
        .SetApplicationName("ConfigFerret.Web")
        .PersistKeysToFileSystem(new DirectoryInfo(Path.Join(dataFolder, "keys")));
}

// One view model per browser tab (a Blazor circuit), the same one the desktop app uses. Disposing the circuit disposes
// it, which deletes the tab's downloaded repositories.
builder.Services.AddScoped<WebDialogs>();
builder.Services.AddScoped(services =>
{
    var options = services.GetRequiredService<IOptions<WebAppOptions>>().Value;

    // Credentials typed in a tab stay in that tab's memory. A token in GITHUB_TOKEN (or an Octopus key in
    // OCTOPUS_API_KEY) is used by every tab when none is typed in.
    var credentials = new EnvironmentFallbackCredentialStore(
        new SessionCredentialStore(),
        new Dictionary<string, string[]>
        {
            [MainWindowViewModel.GitHubAccountName] = ["GITHUB_TOKEN", "GH_TOKEN"],
            [MainWindowViewModel.OctopusAccountName] = ["OCTOPUS_API_KEY", "OCTOPUS_CLI_API_KEY"],
        });
    return new MainWindowViewModel(new WorkspaceStore(options.WorkspacePath), credentials)
    {
        OctopusServerFallback = Environment.GetEnvironmentVariable("OCTOPUS_URL") ?? Environment.GetEnvironmentVariable("OCTOPUS_CLI_SERVER"),
        Dialogs = services.GetRequiredService<WebDialogs>(),
    };
});
builder.Services.AddSingleton(services => new FolderBrowser(services.GetRequiredService<IOptions<WebAppOptions>>().Value.Folders));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

app.UseSecurityHeaders();
app.UseAntiforgery();

app.MapGet("/healthz", () => Results.Text("ok"));
app.MapBrandAssets();
app.MapStaticAssets();
// The security headers already forbid framing (frame-ancestors 'none'), so Blazor's own, looser policy is left out.
app.MapRazorComponents<App>().AddInteractiveServerRenderMode(options => options.ContentSecurityFrameAncestorsPolicy = null);

await app.RunAsync().ConfigureAwait(false);
return 0;

/// <summary>The entry point, public so the tests can start the app.</summary>
public partial class Program;
