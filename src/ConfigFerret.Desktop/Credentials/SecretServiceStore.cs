using System.ComponentModel;
using System.Diagnostics;

namespace ConfigFerret.Desktop.Credentials;

/// <summary>
/// The freedesktop Secret Service (GNOME Keyring, KWallet, KeePassXC) through libsecret's <c>secret-tool</c>, on
/// Linux and other Unix desktops. The secret is passed on standard input, never on the command line, so it does not
/// show in the process list. Each secret has the attributes <c>service=ConfigFerret</c> and <c>account={account}</c>.
/// </summary>
public sealed class SecretServiceStore : ICredentialStore
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly string _tool;

    /// <summary>Creates the store.</summary>
    /// <param name="tool">The secret-tool executable. Defaults to <c>secret-tool</c> on the PATH.</param>
    public SecretServiceStore(string tool = "secret-tool") => _tool = tool;

    /// <inheritdoc />
    public string DisplayName => "Secret Service keyring (libsecret)";

    /// <inheritdoc />
    public string? Read(string account)
    {
        var (exitCode, output, error) = Run(["lookup", "service", CredentialStores.ServiceName, "account", account], input: null);
        if (exitCode == 0)
        {
            return output;
        }

        // secret-tool exits with 1 and prints nothing when no item matches.
        return string.IsNullOrWhiteSpace(error) ? null : throw Failure("read", error);
    }

    /// <inheritdoc />
    public void Write(string account, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var (exitCode, _, error) = Run(
            ["store", "--label", $"{CredentialStores.ServiceName} {account}", "service", CredentialStores.ServiceName, "account", account],
            secret);
        if (exitCode != 0)
        {
            throw Failure("save", error);
        }
    }

    /// <inheritdoc />
    public void Delete(string account)
    {
        var (exitCode, _, error) = Run(["clear", "service", CredentialStores.ServiceName, "account", account], input: null);
        if (exitCode != 0 && !string.IsNullOrWhiteSpace(error))
        {
            throw Failure("remove", error);
        }
    }

    private (int ExitCode, string Output, string Error) Run(IReadOnlyList<string> arguments, string? input)
    {
        var start = new ProcessStartInfo(_tool)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new CredentialStoreException(NotInstalled);
        }
        catch (Win32Exception ex)
        {
            throw new CredentialStoreException(NotInstalled, ex);
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (input is not null)
            {
                process.StandardInput.Write(input);
            }

            process.StandardInput.Close();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                throw new CredentialStoreException("The keyring did not answer within 30 seconds. Unlock it and try again.");
            }

            return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult().Trim());
        }
    }

    private const string NotInstalled =
        "secret-tool was not found. Install libsecret-tools (Debian, Ubuntu) or libsecret (Fedora, Arch) and a keyring such as GNOME Keyring or KWallet, or read the credential from an environment variable instead.";

    private static CredentialStoreException Failure(string action, string error) =>
        new($"Could not {action} the credential in the keyring: {error}. Check that a Secret Service keyring (GNOME Keyring, KWallet) is running and unlocked, or read the credential from an environment variable instead.");
}
