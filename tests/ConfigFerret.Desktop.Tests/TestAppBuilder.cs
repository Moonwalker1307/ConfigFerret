using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(ConfigFerret.Desktop.Tests.TestAppBuilder))]

namespace ConfigFerret.Desktop.Tests;

/// <summary>Runs the app's real theme and views headless, drawn with Skia so screenshots work.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}
