namespace ConfigFerret.Desktop.Credentials;

/// <summary>
/// Keeps connection credentials (GitHub tokens, Octopus API keys) in the operating system's credential store, never in
/// the app's own files.
/// </summary>
public interface ICredentialStore
{
    /// <summary>The store's name as shown to the user, for example "Windows Credential Manager".</summary>
    string DisplayName { get; }

    /// <summary>Reads the secret saved for <paramref name="account"/>, or null when none is saved.</summary>
    /// <exception cref="CredentialStoreException">The store could not be reached.</exception>
    string? Read(string account);

    /// <summary>Saves <paramref name="secret"/> for <paramref name="account"/>, replacing any earlier one.</summary>
    /// <exception cref="CredentialStoreException">The store could not be reached.</exception>
    void Write(string account, string secret);

    /// <summary>Removes the secret saved for <paramref name="account"/>. Does nothing when none is saved.</summary>
    /// <exception cref="CredentialStoreException">The store could not be reached.</exception>
    void Delete(string account);
}

/// <summary>The credential store could not be reached or refused the request. The message never holds the secret.</summary>
public sealed class CredentialStoreException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CredentialStoreException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public CredentialStoreException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the error behind it.</summary>
    public CredentialStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Picks the credential store for the operating system the app runs on.</summary>
public static class CredentialStores
{
    /// <summary>The service name every credential is saved under.</summary>
    public const string ServiceName = "ConfigFerret";

    /// <summary>The store for this operating system.</summary>
    public static ICredentialStore ForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsCredentialStore();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacKeychainStore();
        }

        return new SecretServiceStore();
    }
}

/// <summary>Keeps secrets in memory only. For tests.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string DisplayName => "memory";

    /// <inheritdoc />
    public string? Read(string account) => _secrets.GetValueOrDefault(account);

    /// <inheritdoc />
    public void Write(string account, string secret) => _secrets[account] = secret;

    /// <inheritdoc />
    public void Delete(string account) => _secrets.Remove(account);
}

/// <summary>
/// Reads from another store first, then from environment variables, so a token in <c>GITHUB_TOKEN</c> works without
/// saving it. Saving and removing only touch the other store; environment variables are never changed.
/// </summary>
public sealed class EnvironmentFallbackCredentialStore : ICredentialStore
{
    private readonly ICredentialStore _inner;
    private readonly IReadOnlyDictionary<string, string[]> _variables;
    private readonly Func<string, string?> _getEnvironmentVariable;

    /// <summary>Creates the store.</summary>
    /// <param name="inner">The store that saves credentials.</param>
    /// <param name="variables">For each account, the environment variables to try, in order.</param>
    /// <param name="getEnvironmentVariable">Reads an environment variable. Defaults to the process environment.</param>
    public EnvironmentFallbackCredentialStore(ICredentialStore inner, IReadOnlyDictionary<string, string[]> variables, Func<string, string?>? getEnvironmentVariable = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _variables = variables ?? throw new ArgumentNullException(nameof(variables));
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    }

    /// <inheritdoc />
    public string DisplayName => _inner.DisplayName;

    /// <inheritdoc />
    public string? Read(string account)
    {
        string? stored;
        try
        {
            stored = _inner.Read(account);
        }
        catch (CredentialStoreException) when (FromEnvironment(account) is not null)
        {
            // No keyring on this machine, but the environment has the credential.
            stored = null;
        }

        return string.IsNullOrEmpty(stored) ? FromEnvironment(account) : stored;
    }

    private string? FromEnvironment(string account)
    {
        if (!_variables.TryGetValue(account, out var names))
        {
            return null;
        }

        return names.Select(_getEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
    }

    /// <inheritdoc />
    public void Write(string account, string secret) => _inner.Write(account, secret);

    /// <inheritdoc />
    public void Delete(string account) => _inner.Delete(account);
}
