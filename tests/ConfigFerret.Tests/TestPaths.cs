namespace ConfigFerret.Tests;

internal static class TestPaths
{
    public static string Data(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "TestData", .. parts]);
}
