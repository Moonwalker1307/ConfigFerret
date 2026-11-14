using ConfigFerret.Desktop.Credentials;

namespace ConfigFerret.Desktop.Tests;

public class SecretServiceStoreTests : IDisposable
{
    private const string Secret = "ghp" + "_topsecret000000000000000000000000";
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("configferret-secret-tool-");

    public void Dispose()
    {
        _folder.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private string ArgsFile => Path.Join(_folder.FullName, "args.txt");

    private string StdinFile => Path.Join(_folder.FullName, "stdin.txt");

    [Fact]
    public void Passes_the_secret_on_standard_input_and_never_in_the_arguments()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new SecretServiceStore(FakeTool(exitCode: 0));

        store.Write("GitHub/main", Secret);

        Assert.Equal(Secret, File.ReadAllText(StdinFile));
        var arguments = File.ReadAllLines(ArgsFile);
        Assert.Equal(["store", "--label", "ConfigFerret GitHub/main", "service", "ConfigFerret", "account", "GitHub/main"], arguments);
        Assert.DoesNotContain(Secret, string.Join(' ', arguments), StringComparison.Ordinal);
    }

    [Fact]
    public void Passes_a_secret_with_shell_characters_unchanged()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const string tricky = "p@ss \"quoted\" $HOME `id` ; rm -rf / 'x'\nsecond line";
        var store = new SecretServiceStore(FakeTool(exitCode: 0));

        store.Write("account with spaces", tricky);

        Assert.Equal(tricky, File.ReadAllText(StdinFile));
        Assert.Contains("account with spaces", File.ReadAllLines(ArgsFile));
    }

    [Fact]
    public void Reads_the_secret_the_tool_prints()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new SecretServiceStore(FakeTool(exitCode: 0, stdout: Secret));

        Assert.Equal(Secret, store.Read("GitHub/main"));
        Assert.Equal(["lookup", "service", "ConfigFerret", "account", "GitHub/main"], File.ReadAllLines(ArgsFile));
    }

    [Fact]
    public void Reading_a_secret_that_is_not_saved_gives_null()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new SecretServiceStore(FakeTool(exitCode: 1));

        Assert.Null(store.Read("GitHub/main"));
    }

    [Fact]
    public void Reading_fails_with_the_tool_message_when_the_keyring_is_not_reachable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new SecretServiceStore(FakeTool(exitCode: 1, stderr: "Cannot autolaunch D-Bus without X11 $DISPLAY"));

        var ex = Assert.Throws<CredentialStoreException>(() => store.Read("GitHub/main"));

        Assert.Contains("D-Bus", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_fails_with_a_message_that_has_no_secret()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new SecretServiceStore(FakeTool(exitCode: 1, stderr: "The collection is locked"));

        var ex = Assert.Throws<CredentialStoreException>(() => store.Write("GitHub/main", Secret));

        Assert.Contains("locked", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deleting_a_secret_that_is_not_saved_does_nothing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new SecretServiceStore(FakeTool(exitCode: 1));

        store.Delete("GitHub/main");

        Assert.Equal(["clear", "service", "ConfigFerret", "account", "GitHub/main"], File.ReadAllLines(ArgsFile));
    }

    [Fact]
    public void Deleting_fails_when_the_keyring_is_not_reachable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new SecretServiceStore(FakeTool(exitCode: 1, stderr: "keyring is locked"));

        Assert.Throws<CredentialStoreException>(() => store.Delete("GitHub/main"));
    }

    [Fact]
    public void A_missing_executable_is_a_credential_store_exception_that_names_secret_tool()
    {
        var store = new SecretServiceStore(Path.Join(_folder.FullName, "no-such-secret-tool"));

        var ex = Assert.Throws<CredentialStoreException>(() => store.Read("GitHub/main"));

        Assert.Contains("secret-tool", ex.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<System.ComponentModel.Win32Exception>(ex.InnerException);
    }

    [Fact]
    public void A_missing_executable_does_not_put_the_secret_in_the_message()
    {
        var store = new SecretServiceStore(Path.Join(_folder.FullName, "no-such-secret-tool"));

        var ex = Assert.Throws<CredentialStoreException>(() => store.Write("GitHub/main", Secret));

        Assert.Contains("secret-tool", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_display_name_mentions_the_keyring()
    {
        Assert.Contains("Secret Service", new SecretServiceStore().DisplayName, StringComparison.Ordinal);
    }

    // A shell script that records its arguments (one per line) and standard input, prints stdout and stderr, and exits.
    private string FakeTool(int exitCode, string stdout = "", string stderr = "")
    {
        var path = Path.Join(_folder.FullName, "secret-tool");
        var script = $"""
            #!/bin/sh
            printf '%s\n' "$@" > '{ArgsFile}'
            cat > '{StdinFile}'
            printf '%s' '{stdout}'
            printf '%s' '{stderr}' >&2
            exit {exitCode}
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        File.WriteAllText(path, script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
