using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.FileProviders;

namespace ConfigFerret.DotNet;

/// <summary>Turns a .NET configuration provider and key into a provenance step.</summary>
internal sealed class ProviderDescriber(IReadOnlyDictionary<string, string?> environment, TraceDiagnostics diagnostics)
{
    private static readonly string[] ConnectionStringPrefixes =
        ["MYSQLCONNSTR_", "SQLAZURECONNSTR_", "SQLCONNSTR_", "CUSTOMCONNSTR_", "POSTGRESQLCONNSTR_"];

    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, int>> _jsonLines = new(StringComparer.Ordinal);

    /// <summary>Expands chained configurations so each real provider is seen in priority order.</summary>
    public static IEnumerable<IConfigurationProvider> Flatten(IEnumerable<IConfigurationProvider> providers)
    {
        foreach (var provider in providers)
        {
            if (provider is ChainedConfigurationProvider { Configuration: IConfigurationRoot inner })
            {
                foreach (var nested in Flatten(inner.Providers))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return provider;
            }
        }
    }

    public ProvenanceStep Describe(IConfigurationProvider provider, string key, string? value) => provider switch
    {
        EnvironmentVariablesConfigurationProvider env => DescribeEnvironment(env, key, value),
        FileConfigurationProvider file => DescribeFile(file, key, value),
        CommandLineConfigurationProvider cmd => DescribeCommandLine(cmd, key, value),
        MemoryConfigurationProvider => new ProvenanceStep
        {
            Kind = LayerKind.DotNetProvider,
            Source = "in-memory collection",
            Value = value,
        },
        _ when provider.GetType().FullName == "Microsoft.Extensions.Configuration.KeyPerFile.KeyPerFileConfigurationProvider"
            => DescribeKeyPerFile(provider, key, value),
        _ => new ProvenanceStep
        {
            Kind = LayerKind.DotNetProvider,
            Source = provider.ToString() ?? provider.GetType().Name,
            Value = value,
        },
    };

    private ProvenanceStep DescribeEnvironment(EnvironmentVariablesConfigurationProvider provider, string key, string? value)
    {
        var prefix = ReadPrefix(provider);
        var name = FindEnvironmentVariable(prefix, key, value) ?? prefix + key.Replace(":", "__", StringComparison.Ordinal);
        return new ProvenanceStep
        {
            Kind = LayerKind.DotNetProvider,
            Source = $"environment variable {name}",
            Value = value,
            Reference = new EnvironmentVariableRef(name),
        };
    }

    internal string? FindEnvironmentVariable(string prefix, string key, string? value)
    {
        string? fallback = null;
        foreach (var (name, envValue) in environment)
        {
            if (!MapsTo(name, prefix, key))
            {
                continue;
            }

            if (string.Equals(envValue, value, StringComparison.Ordinal))
            {
                return name;
            }

            fallback ??= name;
        }

        return fallback;
    }

    private static bool MapsTo(string name, string prefix, string key)
    {
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = name[prefix.Length..];
        foreach (var cs in ConnectionStringPrefixes)
        {
            if (rest.StartsWith(cs, StringComparison.OrdinalIgnoreCase))
            {
                var csKey = "ConnectionStrings:" + Normalize(rest[cs.Length..]);
                if (string.Equals(csKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return string.Equals(Normalize(rest), key, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string name) => name.Replace("__", ConfigurationPath.KeyDelimiter, StringComparison.Ordinal);

    /// <summary>Reads the provider's prefix. .NET prints it in ToString as <c>Prefix: 'X'</c>.</summary>
    internal static string ReadPrefix(EnvironmentVariablesConfigurationProvider provider)
    {
        var field = typeof(EnvironmentVariablesConfigurationProvider).GetField("_prefix", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(provider) is string prefix)
        {
            return prefix;
        }

        var text = provider.ToString();
        const string marker = "Prefix: '";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += marker.Length;
        var end = text.IndexOf('\'', start);
        return end > start ? text[start..end] : string.Empty;
    }

    private ProvenanceStep DescribeFile(FileConfigurationProvider provider, string key, string? value)
    {
        var source = provider.Source;
        var relative = source.Path ?? "(unknown file)";
        var fullPath = source.FileProvider?.GetFileInfo(relative).PhysicalPath
            ?? (source.FileProvider is PhysicalFileProvider p ? Path.Combine(p.Root, relative) : relative);

        string? location = null;
        if (fullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var lines = _jsonLines.GetOrAdd(fullPath, path => JsonLineLocator.TryLocate(path, diagnostics));
            if (lines.TryGetValue(key, out var line))
            {
                location = $"line {line}";
            }
        }

        var isUserSecrets = fullPath.Contains("usersecrets", StringComparison.OrdinalIgnoreCase)
            || fullPath.Contains("UserSecrets", StringComparison.Ordinal);

        return new ProvenanceStep
        {
            Kind = LayerKind.DotNetProvider,
            Source = isUserSecrets ? $"user secrets ({Path.GetFileName(fullPath)})" : relative,
            Location = location,
            Value = value,
            IsSensitive = isUserSecrets,
            Note = source.Optional ? "optional file" : null,
            Reference = new FileRef(fullPath),
        };
    }

    private static ProvenanceStep DescribeCommandLine(CommandLineConfigurationProvider provider, string key, string? value)
    {
        string? arg = null;
        // Args is protected, so read it by reflection.
        var args = typeof(CommandLineConfigurationProvider)
            .GetProperty("Args", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?
            .GetValue(provider) as IEnumerable<string> ?? [];
        foreach (var a in args)
        {
            var trimmed = a.TrimStart('-', '/');
            var eq = trimmed.IndexOf('=', StringComparison.Ordinal);
            var name = eq >= 0 ? trimmed[..eq] : trimmed;
            if (string.Equals(name.Replace("__", ":", StringComparison.Ordinal), key, StringComparison.OrdinalIgnoreCase))
            {
                arg = a;
                break;
            }
        }

        return new ProvenanceStep
        {
            Kind = LayerKind.DotNetProvider,
            Source = arg is null ? "command line" : $"command-line argument {(arg.Contains('=', StringComparison.Ordinal) ? arg[..arg.IndexOf('=', StringComparison.Ordinal)] : arg)}",
            Value = value,
            Note = arg is null ? "possibly through a switch mapping" : null,
        };
    }

    private static ProvenanceStep DescribeKeyPerFile(IConfigurationProvider provider, string key, string? value)
    {
        // KeyPerFileConfigurationProvider has a public Source with a FileProvider. Read it by reflection so the
        // package does not have to depend on Microsoft.Extensions.Configuration.KeyPerFile.
        var source = provider.GetType().GetProperty("Source")?.GetValue(provider);
        var fileProvider = source?.GetType().GetProperty("FileProvider")?.GetValue(source) as IFileProvider;
        var fileName = key.Replace(":", "__", StringComparison.Ordinal);
        var physical = fileProvider?.GetFileInfo(fileName).PhysicalPath;
        if (physical is null && fileProvider is PhysicalFileProvider p)
        {
            physical = Path.Combine(p.Root, fileName);
        }

        return new ProvenanceStep
        {
            Kind = LayerKind.DotNetProvider,
            Source = physical is null ? $"key-per-file {fileName}" : $"key-per-file {physical}",
            Value = value,
            IsSensitive = true,
            Reference = physical is null ? null : new FileRef(physical),
        };
    }
}
