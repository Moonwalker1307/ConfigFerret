// Compiled into the desktop app only when it is built for Stryker.NET (see ConfigFerret.Desktop.csproj).
//
// MainWindow.InitializeComponent is written by Avalonia's source generator, and Stryker.NET compiles the mutated
// project with Roslyn alone, so the generator does not run and the project would not compile. The real generated
// instance method wins over this extension method, so a normal build is not affected.
namespace ConfigFerret.Desktop.Views;

internal static class MainWindowStub
{
    internal static void InitializeComponent(this MainWindow window)
    {
    }
}
