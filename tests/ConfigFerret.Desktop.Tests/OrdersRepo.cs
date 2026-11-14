namespace ConfigFerret.Desktop.Tests;

/// <summary>Paths into the orders-repo test folder.</summary>
internal static class OrdersRepo
{
    public static string Root { get; } = Path.Join(AppContext.BaseDirectory, "TestData", "orders-repo");

    public static string Full(string relativePath) => Path.GetFullPath(Path.Join(Root, relativePath));
}
