using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ConfigFerret.Kustomize;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Tool;

/// <summary>What the app's configuration is built from: the inputs to <c>trace</c> and <c>unused</c>.</summary>
internal sealed record AppInputs(
    string AppDirectory,
    string? EnvironmentName,
    IReadOnlyList<string> ConfigFiles,
    bool ConfigFilesBeforeEnvironment,
    IReadOnlyList<string> EnvironmentPrefixes,
    bool UseHostEnvironmentVariables,
    IReadOnlyDictionary<string, string?> Environment,
    IReadOnlyList<string> CommandLine,
    string? UserSecretsId,
    bool UseUserSecrets);

/// <summary>
/// What the tool reads from the app folder's project file: the app's name, which .NET takes from the entry assembly
/// (the <c>AssemblyName</c>, else the project file's name), and the <c>UserSecretsId</c>.
/// </summary>
internal sealed record AppProject(string ApplicationName, string? UserSecretsId);

/// <summary>
/// Builds configuration the way <c>WebApplication.CreateBuilder</c> and <c>Host.CreateApplicationBuilder</c> do,
/// from files on disk instead of a running app: the <c>ASPNETCORE_</c> and <c>DOTNET_</c> environment variables,
/// <c>appsettings.json</c>, <c>appsettings.{Environment}.json</c>, <c>{ApplicationName}.settings.json</c>,
/// <c>{ApplicationName}.settings.{Environment}.json</c>, user secrets in Development, environment variables, the
/// command line, then the settings files the app adds itself.
/// </summary>
internal static partial class AppConfiguration
{
    public const string DefaultEnvironment = "Production";

    // Paths that differ only in case name the same file on Windows and macOS.
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Picks the environment as <c>WebApplication.CreateBuilder</c> does, from its own configuration: the
    /// <c>ASPNETCORE_</c> variables, then the <c>DOTNET_</c> variables, then the command line, each winning over the one
    /// before. <c>Host.CreateApplicationBuilder</c> reads the same without the <c>ASPNETCORE_</c> variables.
    /// </summary>
    public static string PickEnvironment(AppInputs inputs, out string source)
    {
        if (inputs.EnvironmentName is not null)
        {
            source = "--environment";
            return inputs.EnvironmentName;
        }

        (string Prefix, string Source)[] variables = [("ASPNETCORE_", "ASPNETCORE_ENVIRONMENT"), ("DOTNET_", "DOTNET_ENVIRONMENT")];
        var builder = new ConfigurationBuilder();
        foreach (var (prefix, _) in variables)
        {
            builder.Add(new SimulatedEnvironmentVariablesSource(inputs.Environment, prefix));
        }

        AddCommandLine(builder, inputs.CommandLine);
        var hostConfiguration = builder.Build();
        var name = hostConfiguration["environment"];
        if (string.IsNullOrWhiteSpace(name))
        {
            source = "the default";
            return DefaultEnvironment;
        }

        // The last provider that sets the name is the one it came from.
        var from = hostConfiguration.Providers.ToList().FindLastIndex(p => p.TryGet("environment", out _));
        source = from < variables.Length ? variables[from].Source : "the app's command line";
        return name;
    }

    public static IConfigurationRoot Build(AppInputs inputs, CliHost host, out string environmentName)
    {
        var directory = Path.GetFullPath(InputReader.RequireDirectory(inputs.AppDirectory, "The app folder"));
        environmentName = PickEnvironment(inputs, out var source);
        if (!IsFileNamePart(environmentName))
        {
            // The name can come from any app argument or variable, so the message leaves it out.
            throw new UsageException($"The environment name from {source} holds a slash or a character a file name can't have. Pass --environment with a plain name.");
        }

        var configFiles = inputs.ConfigFiles.Select(file => Path.GetFullPath(InputReader.RequireFile(file))).ToList();
        var configTargets = configFiles.Select(f => Kustomization.RealPath(f)).OfType<string>().ToHashSet(PathComparer);

        // WebApplication.CreateBuilder adds these with their prefix removed, below everything else.
        var builder = new ConfigurationBuilder();
        if (inputs.UseHostEnvironmentVariables)
        {
            builder.Add(new SimulatedEnvironmentVariablesSource(inputs.Environment, "ASPNETCORE_"));
            builder.Add(new SimulatedEnvironmentVariablesSource(inputs.Environment, "DOTNET_"));
        }

        // .NET 10 also loads {ApplicationName}.settings.json and its environment file after the appsettings files.
        var project = ReadProject(directory, inputs.AppDirectory, host.Error);
        List<string> settingsFiles = [Path.Join(directory, "appsettings.json"), Path.Join(directory, $"appsettings.{environmentName}.json")];
        if (project?.ApplicationName is { } applicationName)
        {
            settingsFiles.Add(Path.Join(directory, $"{applicationName}.settings.json"));
            settingsFiles.Add(Path.Join(directory, $"{applicationName}.settings.{environmentName}.json"));
        }

        var foundAny = false;
        foreach (var file in settingsFiles.Distinct(StringComparer.Ordinal).Where(File.Exists))
        {
            // A settings file the tool finds by itself may be a link, as in a mounted ConfigMap, but only to a file in
            // the app folder: the checkout may come from someone else, and a link is a few bytes in Git.
            if (!Kustomization.IsInFolder(file, directory))
            {
                // A --config file that names the same file reads it once, where the --config files load.
                if (Kustomization.RealPath(file) is { } target && configTargets.Contains(target))
                {
                    continue;
                }

                throw new UsageException($"{file} links to a file outside the app folder. Pass that file with --config to read it anyway; it then loads where the --config files do.");
            }

            InputReader.RequireFoundFileSize(file);
            builder.AddJsonFile(file, optional: false);
            foundAny = true;
        }

        if (!foundAny && configFiles.Count == 0)
        {
            throw new UsageException(NoSettingsMessage(directory));
        }

        if (inputs.ConfigFilesBeforeEnvironment)
        {
            AddJsonFiles(builder, configFiles);
        }

        var secretsId = inputs.UserSecretsId ?? project?.UserSecretsId;
        var loadSecrets = inputs.UseUserSecrets
            && secretsId is not null
            && (inputs.UserSecretsId is not null || string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase));
        if (loadSecrets)
        {
            var secretsFile = UserSecretsPath(secretsId!, inputs.UserSecretsId is not null ? "--user-secrets-id" : "the project file", host);
            if (secretsFile is not null && File.Exists(secretsFile))
            {
                builder.AddJsonFile(secretsFile, optional: false);
            }
        }

        // The hosts always add the environment variables without a prefix; each prefix is another call after it.
        foreach (var prefix in inputs.UseHostEnvironmentVariables ? [string.Empty, .. inputs.EnvironmentPrefixes] : inputs.EnvironmentPrefixes)
        {
            builder.Add(new SimulatedEnvironmentVariablesSource(inputs.Environment, prefix));
        }

        if (inputs.CommandLine.Count > 0)
        {
            AddCommandLine(builder, inputs.CommandLine);
        }

        // A file the app adds itself comes after everything the host set up.
        if (!inputs.ConfigFilesBeforeEnvironment)
        {
            AddJsonFiles(builder, configFiles);
        }

        return builder.Build();
    }

    // The command-line provider quotes an argument it rejects, and an argument may hold a secret, so its message is
    // left out. It parses when it loads, so the arguments are loaded once here to see the error at this place.
    private static void AddCommandLine(ConfigurationBuilder builder, IReadOnlyList<string> args)
    {
        try
        {
            new ConfigurationBuilder().AddCommandLine([.. args]).Build();
        }
        catch (FormatException)
        {
            throw new UsageException("The app's arguments after -- can't be read as a .NET command line: a short switch such as -p needs a switch mapping, which the tool doesn't know.");
        }

        builder.AddCommandLine([.. args]);
    }

    private static void AddJsonFiles(ConfigurationBuilder builder, List<string> files)
    {
        foreach (var file in files)
        {
            builder.AddJsonFile(file, optional: false);
        }
    }

    /// <summary>
    /// Reads the one project file in <paramref name="directory"/>, if there is one. The checkout may come from someone
    /// else, so the file is read only when it is in the app folder, links followed, and at most
    /// <see cref="InputReader.MaxFoundFileBytes"/> long, without a DTD. Otherwise nothing is read from it, and one
    /// warning names the file, as <paramref name="appFolder"/> was given, and the reason, but nothing in it. A folder
    /// with several project files gives one warning too, and one with none gives nothing.
    /// </summary>
    internal static AppProject? ReadProject(string directory, string appFolder, TextWriter warnings)
    {
        var projects = Directory.GetFiles(directory, "*.csproj");
        if (projects.Length > 1)
        {
            // Which one builds the app can't be told from the folder, so none is read, and no file name is given.
            warnings.WriteLine($"configferret: {TextRenderer.Escape(appFolder)} holds several project files, so no {{ApplicationName}}.settings.json or UserSecretsId is read.");
        }

        if (projects.Length != 1)
        {
            return null;
        }

        var path = projects[0];
        var applicationName = Path.GetFileNameWithoutExtension(path);
        AppProject NotRead(string reason)
        {
            // Only a fixed reason: an exception message can hold a path the file links to.
            var shown = TextRenderer.Escape(Path.Join(appFolder, Path.GetFileName(path)));
            warnings.WriteLine($"configferret: {shown} {reason}, so its UserSecretsId and AssemblyName are not used.");
            return new AppProject(applicationName, null);
        }

        if (!Kustomization.IsInFolder(path, directory))
        {
            return NotRead("links to a file outside the app folder");
        }

        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > InputReader.MaxFoundFileBytes)
            {
                return NotRead($"is bigger than {InputReader.MaxFoundFileBytes / (1024 * 1024)} MB");
            }

            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);

            // A name built from MSBuild properties can't be worked out here; the project file's name is the usual result.
            var assemblyName = ReadProperty(document, "AssemblyName");
            if (assemblyName is not null && !assemblyName.Contains("$(", StringComparison.Ordinal) && IsFileNamePart(assemblyName))
            {
                applicationName = assemblyName;
            }

            return new AppProject(applicationName, ReadProperty(document, "UserSecretsId"));
        }
        catch (XmlException)
        {
            return NotRead("holds a DTD or is not valid XML");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NotRead("could not be read");
        }
    }

    private static string? ReadProperty(XDocument document, string name)
    {
        var value = document.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Where <c>dotnet user-secrets</c> keeps the secrets for <paramref name="id"/> on this machine. An id that is not
    /// valid is refused naming <paramref name="source"/>, but not the id, which could be a value pasted there by mistake.
    /// </summary>
    internal static string? UserSecretsPath(string id, string source, CliHost host)
    {
        if (!SafeUserSecretsId().IsMatch(id) || id.Trim('.').Length == 0)
        {
            throw new UsageException($"The UserSecretsId from {source} is not valid: it can hold only letters, digits, '.', '_' and '-', and not only dots.");
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

    // The environment name becomes part of a file name in the app folder, so a name that holds a folder is refused.
    private static bool IsFileNamePart(string name) =>
        name.IndexOfAny(['/', '\\']) < 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    // User secrets ids are usually GUIDs; anything that could leave the usersecrets folder is refused.
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeUserSecretsId();
}
