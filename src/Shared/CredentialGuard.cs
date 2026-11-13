using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Shared;

/// <summary>
/// Startup checks for credentials bound from configuration, shared by the ConfigFerret.GitHub and ConfigFerret.Octopus
/// packages (compiled into each). Messages name the key and the file, never the credential.
/// </summary>
internal static class CredentialGuard
{
    /// <summary>The full configuration key, for example <c>ConfigFerret:GitHub:Token</c>.</summary>
    public static string Key(string sectionPath, string name) => string.IsNullOrEmpty(sectionPath) ? name : $"{sectionPath}:{name}";

    /// <summary>The environment variable name for <paramref name="key"/>, for example <c>ConfigFerret__GitHub__Token</c>.</summary>
    public static string EnvironmentVariable(string key) => key.Replace(":", "__", StringComparison.Ordinal);

    public static string Missing(string key) =>
        $"{key} is not set. Keep it out of appsettings files: use user secrets in development (dotnet user-secrets set \"{key}\" \"...\"), "
        + $"and an environment variable ({EnvironmentVariable(key)}) or a key vault elsewhere.";

    public static string InAppSettings(string key, string file) =>
        $"{key} is set in {file}. Credentials must not be in appsettings files, which are usually committed. "
        + $"Remove it there and use user secrets in development, and an environment variable ({EnvironmentVariable(key)}) or a key vault elsewhere.";

    /// <summary>
    /// Returns the name of an appsettings JSON file that holds a non-empty value for <paramref name="key"/>, or null.
    /// Every such file counts, even when another source overrides it, because the file itself is what gets committed.
    /// </summary>
    public static string? FindAppSettingsFile(IConfiguration? configuration, string key)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return null;
        }

        foreach (var provider in root.Providers)
        {
            if (provider is ChainedConfigurationProvider chained)
            {
                if (FindAppSettingsFile(chained.Configuration, key) is { } nested)
                {
                    return nested;
                }

                continue;
            }

            if (provider is FileConfigurationProvider file
                && file.Source.Path is { } path
                && IsAppSettingsFile(path)
                && provider.TryGet(key, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                return Path.GetFileName(path);
            }
        }

        return null;
    }

    // appsettings.json, appsettings.Production.json and so on. User secrets live in secrets.json and are fine.
    private static bool IsAppSettingsFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the problem with a base address, or null when it is safe to send a credential to.</summary>
    public static string? CheckAddress(Uri? uri, bool allowUnencryptedHttp, string key)
    {
        try
        {
            JsonApi.RequireSafeBaseAddress(uri, allowUnencryptedHttp, key);
            return null;
        }
        catch (ArgumentException ex)
        {
            // ArgumentException appends "(Parameter '...')"; the key is already in the message.
            return ex.Message.Split(" (Parameter", 2)[0];
        }
    }
}
