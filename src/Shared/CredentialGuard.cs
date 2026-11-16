using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Shared;

/// <summary>
/// Startup checks for credentials bound from configuration, compiled into every extension package that compiles
/// src/Shared. Messages name the key, the file and a provider type, never the credential.
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

    public static string CannotLookInto(string key, string source) =>
        $"{key} is set in {source}, whose own sources cannot be seen, so there is no telling whether it comes from an appsettings file. "
        + $"Remove it from {source}, and set {key} in a source added to the configuration itself, such as user secrets, an environment variable ({EnvironmentVariable(key)}) or a key vault.";

    public static string NoRoot(string key) =>
        $"{key} is set, but neither the configuration it is bound from nor a registered IConfiguration leads to a configuration root, "
        + "so there is no telling whether it comes from an appsettings file. "
        + "Bind it from the root or from a section that GetSection made, or register the root before the options are read: "
        + "services.AddSingleton<IConfiguration>(configuration).";

    /// <summary>
    /// Returns the startup failure for the credential under <paramref name="key"/> when an appsettings JSON file holds a
    /// non-empty value for it, or when a source that holds it hides where its value comes from; otherwise null. Every such
    /// file counts, even when another source overrides it, because the file itself is what gets committed. The check looks
    /// inside chained configurations, chained sections included, and inside providers that wrap another one, such as
    /// those RecordReads adds. A section is checked through the root it reads from, with <paramref name="key"/> as the
    /// full path in that root. The root of the <paramref name="registered"/> IConfiguration is checked as well when it is
    /// another one, since its files are committed too. Without any root there is no telling, so a credential that is set
    /// (<paramref name="value"/>) fails then.
    /// </summary>
    public static string? CheckSource(IConfiguration? configuration, string key, string? value, IConfiguration? registered = null)
    {
        var roots = new[] { RootOf(configuration), RootOf(registered) }
            .OfType<IConfigurationRoot>()
            .Distinct<IConfigurationRoot>(ReferenceEqualityComparer.Instance)
            .ToList();
        if (roots.Count == 0)
        {
            return string.IsNullOrWhiteSpace(value) ? null : NoRoot(key);
        }

        foreach (var root in roots)
        {
            var (file, hidden) = Find(root.Providers, key, []);
            if (file is not null)
            {
                return InAppSettings(key, file);
            }

            if (hidden is not null)
            {
                return CannotLookInto(key, hidden);
            }
        }

        return null;
    }

    // The first appsettings file that holds the key, or else the first source that holds it but hides its own sources.
    private static (string? File, string? Hidden) Find(IEnumerable<IConfigurationProvider> providers, string key, HashSet<(IConfigurationProvider, string)> seen)
    {
        string? hidden = null;
        foreach (var provider in providers)
        {
            var found = Find(provider, key, seen);
            if (found.File is not null)
            {
                return found;
            }

            hidden ??= found.Hidden;
        }

        return (null, hidden);
    }

    private static (string? File, string? Hidden) Find(IConfigurationProvider provider, string key, HashSet<(IConfigurationProvider, string)> seen)
    {
        if (!seen.Add((provider, key)))
        {
            return default;
        }

        if (provider is FileConfigurationProvider file)
        {
            return file.Source.Path is { } path && IsAppSettingsFile(path) && HasValue(provider, key) ? (Path.GetFileName(path), null) : default;
        }

        if (provider is ChainedConfigurationProvider chained)
        {
            // A chained section serves its keys without its own path, so its root is asked for the key under that path.
            return chained.Configuration switch
            {
                IConfigurationRoot root => Find(root.Providers, key, seen),
                IConfigurationSection section when RootOf(section) is { } root => Find(root.Providers, Key(section.Path, key), seen),
                _ => HasValue(provider, key) ? (null, provider.GetType().Name) : default,
            };
        }

        string? hidden = null;
        foreach (var inner in Fields(provider, typeof(IConfigurationProvider), typeof(IConfiguration)))
        {
            var found = inner switch
            {
                IConfigurationProvider wrapped => Find(wrapped, key, seen),
                IConfigurationRoot root => Find(root.Providers, key, seen),
                // A provider of an unknown type that keeps a section or another configuration without providers: there is
                // no knowing how its keys relate to that configuration's, so a credential it serves fails the check.
                _ => HasValue(provider, key) ? (null, provider.GetType().Name) : default,
            };
            if (found.File is not null)
            {
                return found;
            }

            hidden ??= found.Hidden;
        }

        return (null, hidden);
    }

    /// <summary>
    /// Returns <paramref name="configuration"/> when it is a root, or the root a section made by <c>GetSection</c> reads
    /// from, which that section keeps in its one root field; otherwise null. A section of another type may keep a root it
    /// does not read from, such as one of defaults, so its root is not taken.
    /// </summary>
    public static IConfigurationRoot? RootOf(IConfiguration? configuration)
    {
        if (configuration is null or IConfigurationRoot)
        {
            return configuration as IConfigurationRoot;
        }

        if (configuration.GetType() != typeof(ConfigurationSection))
        {
            return null;
        }

        var roots = Fields(configuration, typeof(IConfigurationRoot)).OfType<IConfigurationRoot>().Take(2).ToList();
        return roots.Count == 1 ? roots[0] : null;
    }

    // The values of the instance fields of owner whose type is one of types. A provider that wraps another keeps it in a
    // field: the one RecordReads adds keeps the provider whose reads it records.
    private static IEnumerable<object> Fields(object owner, params Type[] types)
    {
        for (var type = owner.GetType(); type is not null; type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (Array.Exists(types, t => field.FieldType.IsAssignableTo(t)) && field.GetValue(owner) is { } value)
                {
                    yield return value;
                }
            }
        }
    }

    private static bool HasValue(IConfigurationProvider provider, string key) =>
        provider.TryGet(key, out var value) && !string.IsNullOrWhiteSpace(value);

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
