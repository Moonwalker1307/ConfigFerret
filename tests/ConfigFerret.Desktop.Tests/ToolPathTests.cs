using ConfigFerret.Desktop.Services;

namespace ConfigFerret.Desktop.Tests;

public class ToolPathTests
{
    [Fact]
    public void A_tool_in_the_apps_own_folder_is_not_started()
    {
        var appFolder = Path.GetDirectoryName(Environment.ProcessPath);
        Assert.SkipUnless(
            appFolder is not null && Path.TrimEndingDirectorySeparator(appFolder) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            "The tests do not run from their own executable.");
        using var temp = new TempFolder();
        var marker = temp.Full("started");
        var name = "configferret-app-folder-tool-" + Guid.NewGuid().ToString("N");
        var tool = Path.Join(appFolder, OperatingSystem.IsWindows() ? name + ".exe" : name);
        try
        {
            // On Windows a copy of hostname.exe, which writes no marker: there the result alone shows it did not run.
            if (OperatingSystem.IsWindows())
            {
                File.Copy(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.System), "hostname.exe"), tool);
            }
            else
            {
                File.WriteAllText(tool, $"#!/bin/sh\necho planted > '{marker}'\n");
                File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var run = RepositoryAnalyzer.RunTool(name, [], cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(File.Exists(marker));
            Assert.Equal(ToolRun.Missing, run);
        }
        finally
        {
            File.Delete(tool);
        }
    }

    [Fact]
    public void Finds_the_tool_in_the_first_folder_on_path_that_has_it()
    {
        using var first = new TempFolder();
        using var second = new TempFolder();
        using var empty = new TempFolder();
        Tool(first.Path);
        Tool(second.Path);

        Assert.Equal(Path.Join(first.Path, FileName), ToolPath.Find("helm", Join(empty.Path, first.Path, second.Path)));
    }

    [Fact]
    public void Finds_nothing_when_path_is_unset_or_empty()
    {
        Assert.Null(ToolPath.Find("helm", null));
        Assert.Null(ToolPath.Find("helm", string.Empty));
    }

    [Fact]
    public void Finds_nothing_when_no_folder_on_path_has_the_tool()
    {
        using var empty = new TempFolder();

        Assert.Null(ToolPath.Find("helm", empty.Path));
    }

    [Fact]
    public void A_folder_named_like_the_tool_is_not_the_tool()
    {
        using var withFolder = new TempFolder();
        using var withTool = new TempFolder();
        Directory.CreateDirectory(Path.Join(withFolder.Path, FileName));
        Tool(withTool.Path);

        Assert.Null(ToolPath.Find("helm", withFolder.Path));
        Assert.Equal(Path.Join(withTool.Path, FileName), ToolPath.Find("helm", Join(withFolder.Path, withTool.Path)));
    }

    [Fact]
    public void A_file_that_may_not_be_executed_is_not_the_tool()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no execute permission bit.");
        using var notExecutable = new TempFolder();
        using var withTool = new TempFolder();
        var file = Path.Join(notExecutable.Path, "helm");
        File.WriteAllText(file, "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Tool(withTool.Path);

        Assert.Null(ToolPath.Find("helm", notExecutable.Path));
        Assert.Equal(Path.Join(withTool.Path, "helm"), ToolPath.Find("helm", Join(notExecutable.Path, withTool.Path)));
    }

    [Fact]
    public void A_link_to_nothing_is_skipped_for_the_tool_later_on_path()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Making a symbolic link on Windows needs rights the tests may not have.");
        using var withLink = new TempFolder();
        using var withTool = new TempFolder();
        File.CreateSymbolicLink(Path.Join(withLink.Path, "helm"), Path.Join(withLink.Path, "removed-helm"));
        Tool(withTool.Path);

        Assert.Equal(Path.Join(withTool.Path, "helm"), ToolPath.Find("helm", Join(withLink.Path, withTool.Path)));
    }

    [Fact]
    public void A_link_loop_is_skipped_for_the_tool_later_on_path()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Making a symbolic link on Windows needs rights the tests may not have.");
        using var withLoop = new TempFolder();
        using var withTool = new TempFolder();
        File.CreateSymbolicLink(Path.Join(withLoop.Path, "helm"), Path.Join(withLoop.Path, "other"));
        File.CreateSymbolicLink(Path.Join(withLoop.Path, "other"), Path.Join(withLoop.Path, "helm"));
        Tool(withTool.Path);

        Assert.Equal(Path.Join(withTool.Path, "helm"), ToolPath.Find("helm", Join(withLoop.Path, withTool.Path)));
    }

    [Fact]
    public void Adds_exe_on_windows_and_never_starts_a_cmd_or_bat_file()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows adds an extension.");
        using var scripts = new TempFolder();
        using var withTool = new TempFolder();
        File.WriteAllText(Path.Join(scripts.Path, "helm.cmd"), "@echo off\r\n");
        File.WriteAllText(Path.Join(scripts.Path, "helm.bat"), "@echo off\r\n");
        File.WriteAllText(Path.Join(scripts.Path, "helm"), string.Empty);
        Tool(withTool.Path);

        Assert.Null(ToolPath.Find("helm", scripts.Path));
        Assert.Equal(Path.Join(withTool.Path, "helm.exe"), ToolPath.Find("helm", Join(scripts.Path, withTool.Path)));
    }

    [Fact]
    public void Reads_a_quoted_folder_on_windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows PATH entries are quoted.");
        using var withTool = new TempFolder();
        Tool(withTool.Path);

        Assert.Equal(Path.Join(withTool.Path, "helm.exe"), ToolPath.Find("helm", "\"" + withTool.Path + "\""));
    }

    [Fact]
    public void A_path_is_used_as_given_even_when_it_does_not_exist()
    {
        using var temp = new TempFolder();
        var missing = Path.Join(temp.Path, "no-such-tool");

        Assert.Equal(missing, ToolPath.Find(missing, null));
    }

    private static string FileName => OperatingSystem.IsWindows() ? "helm.exe" : "helm";

    private static string Join(params string[] folders) => string.Join(Path.PathSeparator, folders);

    // An executable named helm. It is never started.
    private static void Tool(string folder)
    {
        var file = Path.Join(folder, FileName);
        File.WriteAllText(file, "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
