using ConfigFerret.Web.Services;

namespace ConfigFerret.Web.Tests;

public sealed class FolderBrowserTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("configferret-web-folders-");
    private readonly DirectoryInfo _outside = Directory.CreateTempSubdirectory("configferret-web-outside-");

    public FolderBrowserTests()
    {
        Directory.CreateDirectory(Path.Join(_root.FullName, "orders", "src"));
        Directory.CreateDirectory(Path.Join(_root.FullName, "billing"));
        Directory.CreateDirectory(Path.Join(_root.FullName, ".git"));
        File.WriteAllText(Path.Join(_root.FullName, "README.md"), "not a folder");
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Join(_root.FullName, "escape"), _outside.FullName);
        }
    }

    [Fact]
    public void Lists_folders_by_name_without_hidden_folders_files_or_links()
    {
        var browser = new FolderBrowser(_root.FullName);

        Assert.Equal(["billing", "orders"], browser.List(string.Empty));
        Assert.Equal(["src"], browser.List("orders"));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("orders", true)]
    [InlineData("orders/src", true)]
    [InlineData("orders\\src", true)]
    [InlineData("..", false)]
    [InlineData("orders/../..", false)]
    [InlineData("./orders", false)]
    [InlineData("missing", false)]
    [InlineData("README.md", false)]
    [InlineData("C:/Windows", false)]
    public void Resolves_only_folders_under_the_root(string relative, bool allowed)
    {
        var browser = new FolderBrowser(_root.FullName);

        var resolved = browser.Resolve(relative);

        if (allowed)
        {
            Assert.NotNull(resolved);
            Assert.StartsWith(browser.Root!, resolved, StringComparison.Ordinal);
            Assert.True(Directory.Exists(resolved));
        }
        else
        {
            Assert.Null(resolved);
        }
    }

    [Fact]
    public void Absolute_paths_stay_under_the_root()
    {
        var browser = new FolderBrowser(_root.FullName);

        // A leading slash is an empty first segment, so the path is read relative to the root.
        Assert.Null(browser.Resolve(_outside.FullName));
        Assert.Empty(browser.List(_outside.FullName));
    }

    [Fact]
    public void Does_not_follow_links_out_of_the_root()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a link needs extra rights on Windows.");
        var browser = new FolderBrowser(_root.FullName);

        Assert.Null(browser.Resolve("escape"));
        Assert.DoesNotContain("escape", browser.List(string.Empty));
    }

    [Fact]
    public void Gives_paths_relative_to_the_root()
    {
        var browser = new FolderBrowser(_root.FullName);

        Assert.Equal(string.Empty, browser.Relative(browser.Root!));
        Assert.Equal("orders/src", browser.Relative(browser.Resolve("orders/src")!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_root_nothing_can_be_opened(string? root)
    {
        var browser = new FolderBrowser(root);

        Assert.False(browser.IsEnabled);
        Assert.Null(browser.Resolve(string.Empty));
        Assert.Empty(browser.List(string.Empty));
    }

    [Fact]
    public void A_root_that_does_not_exist_allows_nothing()
    {
        var browser = new FolderBrowser(Path.Join(_root.FullName, "nope"));

        Assert.False(browser.IsEnabled);
        Assert.Null(browser.Resolve(string.Empty));
    }

    public void Dispose()
    {
        _root.Delete(recursive: true);
        _outside.Delete(recursive: true);
    }
}
