using ConfigFerret.Desktop.Credentials;
using ConfigFerret.Desktop.Services;

namespace ConfigFerret.Desktop.Tests;

/// <summary>These tests change the current directory and PATH, which every test in the process shares.</summary>
[CollectionDefinition(nameof(ToolPathCurrentDirectoryTests), DisableParallelization = true)]
public sealed class ToolPathCurrentDirectoryDefinition;

// A repository the app was started from can hold an executable named like a tool. Only the tool on PATH may start.
[Collection(nameof(ToolPathCurrentDirectoryTests))]
public class ToolPathCurrentDirectoryTests
{
    [Fact]
    public void A_tool_in_the_current_directory_is_not_started()
    {
        using var repository = new TempFolder();
        using var path = new TempFolder();
        var marker = repository.Full("started");
        Plant(repository.Path, "helm", marker);

        var run = InFolder(repository.Path, path.Path, () => RepositoryAnalyzer.RunTool("helm", ["version"], cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(File.Exists(marker));
        Assert.Equal(ToolRun.Missing, run);
    }

    [Fact]
    public void A_secret_tool_in_the_current_directory_is_not_started()
    {
        using var repository = new TempFolder();
        using var path = new TempFolder();
        var marker = repository.Full("started");
        Plant(repository.Path, "secret-tool", marker);

        var ex = InFolder(repository.Path, path.Path, () => Record.Exception(() => new SecretServiceStore().Read("GitHub/main")));

        Assert.False(File.Exists(marker));
        Assert.Contains("secret-tool was not found", Assert.IsType<CredentialStoreException>(ex).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tool_on_path_wins_over_one_in_the_current_directory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Tells the two tools apart by a script's output.");
        using var repository = new TempFolder();
        using var path = new TempFolder();
        var marker = repository.Full("started");
        Plant(repository.Path, "helm", marker);
        Script(path.Path, "helm", "echo from-path");

        var run = InFolder(repository.Path, path.Path, () => RepositoryAnalyzer.RunTool("helm", ["version"], cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(File.Exists(marker));
        Assert.Equal(new ToolRun("from-path\n"), run);
    }

    [Fact]
    public void The_tool_on_path_is_found_and_started()
    {
        using var elsewhere = new TempFolder();
        using var path = new TempFolder();
        if (OperatingSystem.IsWindows())
        {
            // hostname.exe with no arguments prints the computer's name.
            File.Copy(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.System), "hostname.exe"), Path.Join(path.Path, "helm.exe"));
        }
        else
        {
            Script(path.Path, "helm", "echo from-path");
        }

        var run = InFolder(elsewhere.Path, path.Path, () => RepositoryAnalyzer.RunTool("helm", [], cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(string.IsNullOrWhiteSpace(run.Output), $"No output: {run}");
    }

    [Fact]
    public void A_link_on_path_to_the_tool_is_found_and_started()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Making a symbolic link on Windows needs rights the tests may not have.");
        using var elsewhere = new TempFolder();
        using var path = new TempFolder();
        using var install = new TempFolder();
        Script(install.Path, "helm-3", "echo from-link");
        File.CreateSymbolicLink(Path.Join(path.Path, "helm"), Path.Join(install.Path, "helm-3"));

        var run = InFolder(elsewhere.Path, path.Path, () => RepositoryAnalyzer.RunTool("helm", [], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(new ToolRun("from-link\n"), run);
    }

    [Fact]
    public void A_secret_tool_on_path_is_found_and_started()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell script as secret-tool.");
        using var elsewhere = new TempFolder();
        using var path = new TempFolder();
        Script(path.Path, "secret-tool", "printf '%s' fake-token");

        var secret = InFolder(elsewhere.Path, path.Path, () => new SecretServiceStore().Read("GitHub/main"));

        Assert.Equal("fake-token", secret);
    }

    [Fact]
    public void Empty_and_relative_path_entries_are_skipped()
    {
        using var repository = new TempFolder();
        using var path = new TempFolder();
        Directory.CreateDirectory(repository.Full("bin"));
        Plant(repository.Path, "helm", repository.Full("started"));
        Plant(repository.Full("bin"), "helm", repository.Full("started"));

        // An empty entry is the current directory on Unix.
        var found = InFolder(repository.Path, path.Path, () => ToolPath.Find("helm", string.Join(Path.PathSeparator, string.Empty, ".", "bin", Path.Join(".", "bin"))));

        Assert.Null(found);
    }

    // Runs action with the current directory and PATH set as given, and puts both back after.
    private static T InFolder<T>(string currentDirectory, string path, Func<T> action)
    {
        var previousDirectory = Environment.CurrentDirectory;
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.CurrentDirectory = currentDirectory;
            Environment.SetEnvironmentVariable("PATH", path);
            return action();
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            Environment.SetEnvironmentVariable("PATH", previousPath);
        }
    }

    // An executable named like the tool that creates marker when it runs. On Windows it is a copy of hostname.exe,
    // which writes no marker, so there the test relies on the tool being reported as not installed.
    private static void Plant(string folder, string name, string marker)
    {
        if (OperatingSystem.IsWindows())
        {
            File.Copy(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.System), "hostname.exe"), Path.Join(folder, name + ".exe"));
        }
        else
        {
            Script(folder, name, $"echo planted > '{marker}'");
        }
    }

    private static void Script(string folder, string name, string body)
    {
        var file = Path.Join(folder, name);
        File.WriteAllText(file, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
