using ConfigFerret.Desktop.Credentials;

namespace ConfigFerret.Web.Services;

/// <summary>
/// Keeps the tokens and API keys typed into one browser tab in the server's memory, for as long as the tab stays
/// connected. They are never written to disk, and other tabs and other people using the server can't see them.
/// </summary>
public sealed class SessionCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <inheritdoc />
    public string DisplayName => "server's memory for this browser tab";

    /// <inheritdoc />
    public string? Read(string account)
    {
        lock (_lock)
        {
            return _secrets.GetValueOrDefault(account);
        }
    }

    /// <inheritdoc />
    public void Write(string account, string secret)
    {
        lock (_lock)
        {
            _secrets[account] = secret;
        }
    }

    /// <inheritdoc />
    public void Delete(string account)
    {
        lock (_lock)
        {
            _secrets.Remove(account);
        }
    }
}
