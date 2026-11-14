using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using ConfigFerret.Desktop.Services;

namespace ConfigFerret.Desktop.Tests;

public class GitHubAccountExtractTests : IDisposable
{
    private readonly DirectoryInfo _parent = Directory.CreateTempSubdirectory("configferret-extract-");
    private readonly string _target;

    public GitHubAccountExtractTests()
    {
        _target = Directory.CreateDirectory(Path.Join(_parent.FullName, "target")).FullName;
    }

    public void Dispose()
    {
        _parent.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Drops_the_top_folder()
    {
        var tarball = Tar(t =>
        {
            t.Directory("top/");
            t.File("top/README.md", "readme");
            t.File("top/src/app/appsettings.json", "{}");
        });

        await Extract(tarball);

        Assert.Equal("readme", File.ReadAllText(Path.Join(_target, "README.md")));
        Assert.Equal("{}", File.ReadAllText(Path.Join(_target, "src", "app", "appsettings.json")));
        Assert.False(Directory.Exists(Path.Join(_target, "top")));
    }

    [Fact]
    public async Task Skips_entries_with_dot_dot_and_writes_nothing_outside_the_target()
    {
        var tarball = Tar(t =>
        {
            t.File("top/ok.txt", "ok");
            t.File("top/../evil1.txt", "evil");
            t.File("top/sub/../../evil2.txt", "evil");
            t.File("top/sub/../inside.txt", "evil");
            t.File("top/./dot.txt", "evil");
            t.File("top/..\\evil3.txt", "evil");
            t.File("top/../../evil4.txt", "evil");
        });

        await Extract(tarball);

        Assert.Equal(["ok.txt"], Directory.EnumerateFiles(_target, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_target, f)));
        Assert.Equal([_target], Directory.EnumerateFileSystemEntries(_parent.FullName));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.GetTempPath(), "evil*"));
    }

    [Fact]
    public async Task Keeps_an_absolute_looking_name_inside_the_target()
    {
        var tarball = Tar(t => t.File("top//etc/evil.txt", "evil"));

        await Extract(tarball);

        Assert.False(File.Exists("/etc/evil.txt"));
        Assert.Equal([_target], Directory.EnumerateFileSystemEntries(_parent.FullName));
    }

    [Fact]
    public async Task Skips_symlinks_hard_links_and_directories()
    {
        var tarball = Tar(t =>
        {
            t.File("top/real.txt", "real");
            t.Directory("top/emptydir/");
            t.SymbolicLink("top/link.txt", "/etc/passwd");
            t.SymbolicLink("top/linkdir", "../..");
            t.HardLink("top/hard.txt", "top/real.txt");
        });

        await Extract(tarball);

        Assert.Equal(["real.txt"], Directory.EnumerateFileSystemEntries(_target).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Does_not_write_through_a_symlink_that_an_earlier_entry_declared()
    {
        var outside = Directory.CreateDirectory(Path.Join(_parent.FullName, "outside")).FullName;
        var tarball = Tar(t =>
        {
            t.SymbolicLink("top/escape", outside);
            t.File("top/escape/pwned.txt", "pwned");
        });

        await Extract(tarball);

        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task Skips_files_that_are_not_inside_a_top_folder()
    {
        var tarball = Tar(t =>
        {
            t.File("loose.txt", "loose");
            t.File("top/kept.txt", "kept");
        });

        await Extract(tarball);

        Assert.Equal(["kept.txt"], Directory.EnumerateFileSystemEntries(_target).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Extracts_the_orders_repository()
    {
        await Extract(FakeGitHub.Tarball(OrdersRepo.Root));

        var scan = RepositoryScanner.Scan(_target);
        Assert.Equal("src/Orders.Api", Assert.Single(scan.Apps).RelativePath);
        Assert.Equal([".github/workflows/deploy.yml"], scan.Workflows);
    }

    private async Task Extract(byte[] tarball)
    {
        using var stream = new MemoryStream(tarball);
        await GitHubAccount.ExtractAsync(stream, _target, TestContext.Current.CancellationToken);
    }

    private static byte[] Tar(Action<TarBuilder> build)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            build(new TarBuilder(writer));
        }

        return buffer.ToArray();
    }

    private sealed class TarBuilder(TarWriter writer)
    {
        public void Directory(string name) => writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name));

        public void File(string name, string content) =>
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });

        public void SymbolicLink(string name, string target) =>
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, name) { LinkName = target });

        public void HardLink(string name, string target) =>
            writer.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, name) { LinkName = target });
    }
}
