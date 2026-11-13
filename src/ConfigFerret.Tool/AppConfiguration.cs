using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Tool;

/// <summary>What the app's configuration is built from: the inputs to <c>trace</c> and <c>unused</c>.</summary>
internal sealed record AppInputs(
    string AppDirectory,
    string? EnvironmentName,
    IReadOnlyList<string> ConfigFiles,
    IReadOnlyList<string> EnvironmentPrefixes,
    IReadOnlyDictionary<string, string?> Environment,
    IReadOnlyList<string> CommandLine,
    string? UserSecretsId,
    bool UseUserSecrets);

/// <summary>
/// Builds configuration the way <c>WebApplication.CreateBuilder</c> and <c>Host.CreateApplicationBuilder</c> do,
/// from files on disk instead of a running app: <c>appsettings.json</c>, <c>appsettings.{Environment}.json</c>, user
/// secrets in Development, environment variables, then the command line.
/// </summary>
internal static partial class AppConfiguration
{
    public const string DefaultEnvironment = "Production";

    public static string PickEnvironment(AppInputs inputs) =>
        inputs.EnvironmentName
        ?? Get(inputs.Environment, "ASPNETCORE_ENVIRONMENT")
        ?? Get(inputs.Environment, "DOTNET_ENVIRONMENT")
        ?? DefaultEnvironment;

    public static IConfigurationRoot Build(AppInputs inputs, CliHost host, out string environmentName)
    {
        var directory = Path.GetFullPath(InputReader.RequireDirectory(inputs.AppDirectory, "The app folder"));
        environmentName = PickEnvironment(inputs);

        var builder = new ConfigurationBuilder();
        var baseFile = Path.Join(directory, "appsettings.json");
        var environmentFile = Path.Join(directory, $"appsettings.{environmentName}.json");
        var foundAny = false;
        foreach (var file in new[] { baseFile, environmentFile }.Distinct(StringComparer.Ordinal).Where(File.Exists))
        {
            builder.AddJsonFile(file, optional: false);
            foundAny = true;
        }

        foreach (var file in inputs.ConfigFiles)
        {
            builder.AddJsonFile(Path.GetFullPath(InputReader.RequireFile(file)), optional: false);
            foundAny = true;
        }

        if (!foundAny)
        {
            throw new UsageException(NoSettingsMessage(directory));
        }

        var secretsId = inputs.UserSecretsId ?? FindUserSecretsId(directory);
        var loadSecrets = inputs.UseUserSecrets
            && secretsId is not null
            && (inputs.UserSecretsId is not null || string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase));
        if (loadSecrets)
        {
            var secretsFile = UserSecretsPath(secretsId!, host);
            if (secretsFile is not null && File.Exists(secretsFile))
            {
                builder.AddJsonFile(secretsFile, optional: false);
            }
        }

        foreach (var prefix in inputs.EnvironmentPrefixes)
        {
            builder.Add(new SimulatedEnvironmentVariablesSource(inputs.Environment, prefix));
        }

        if (inputs.CommandLine.Count > 0)
        {
            builder.AddCommandLine([.. inputs.CommandLine]);
        }

        return builder.Build();
    }

    /// <summary>Reads <c>UserSecretsId</c> from the one project file in <paramref name="directory"/>, if there is one.</summary>
    internal static string? FindUserSecretsId(string directory)
    {
        var projects = Directory.GetFiles(directory, "*.csproj");
        if (projects.Length != 1)
        {
            return null;
        }

        try
        {
            var id = XDocument.Load(projects[0]).Descendants().FirstOrDefault(e => e.Name.LocalName == "UserSecretsId")?.Value.Trim();
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>Where <c>dotnet user-secrets</c> keeps the secrets for <paramref name="id"/> on this machine.</summary>
    internal static string? UserSecretsPath(string id, CliHost host)
    {
        if (!SafeUserSecretsId().IsMatch(id) || id.Trim('.').Length == 0)
        {
            throw new UsageException($"'{id}' is not a valid UserSecretsId.");
        }

        var appData = host.GetEnvironmentVariable("APPDATA");
        if (OperatingSystem.IsWindows() && appData is not null)
        {
            return Path.Join(appData, "Microsoft", "UserSecrets", id, "secrets.json");
        }

        var home = host.GetEnvironmentVariable("HOME");
        return home is null ? null : Path.Join(home, ".microsoft", "usersecrets", id, "secrets.json");
    }

    private static string NoSettingsMessage(string directory)
    {
        var message = $"No appsettings.json in {directory}. Pass --app with the folder that holds it, or --config with a settings file.";
        var found = FindSettingsBelow(directory).Take(5).ToList();
        if (found.Count > 0)
        {
            message += " Found: " + string.Join(", ", found.Select(f => Path.GetRelativePath(directory, f)));
        }

        return message;
    }

    // Looks a few folders down for app folders, skipping build output and hidden folders.
    private static IEnumerable<string> FindSettingsBelow(string directory, int depth = 4)
    {
        if (depth == 0)
        {
            yield break;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules")
            {
                continue;
            }

            var file = Path.Join(child, "appsettings.json");
            if (File.Exists(file))
            {
                yield return file;
            }

            foreach (var nested in FindSettingsBelow(child, depth - 1))
            {
                yield return nested;
            }
        }
    }

    private static string? Get(IReadOnlyDictionary<string, string?> environment, string name) =>
        environment.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    // User secrets ids are usually GUIDs; anything that could leave the usersecrets folder is refused.
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeUserSecretsId();
}
