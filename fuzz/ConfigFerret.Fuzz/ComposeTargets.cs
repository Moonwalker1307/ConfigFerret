using ConfigFerret.Compose;

namespace ConfigFerret.Fuzz;

/// <summary>Fuzz targets for the dotenv parser and Docker Compose projects.</summary>
internal static partial class Targets
{
    private static readonly Dictionary<string, string?> FuzzShell = new(StringComparer.Ordinal)
    {
        ["SHELL_VAR"] = "from-shell",
        ["DB_PASSWORD"] = "shell-s3cret",
        ["EMPTY"] = "",
    };

    private static void DotEnvLayersTarget(ReadOnlySpan<byte> data)
    {
        var parts = Split(data);
        DotEnvLayers layers;
        try
        {
            layers = DotEnvLayers.Parse(parts.Select((text, i) => ($"file{i}.env", text)), FuzzShell);
        }
        catch (FormatException)
        {
            return;
        }

        for (var i = 0; i < layers.Files.Count; i++)
        {
            var file = layers.Files[i];
            var lines = parts[i].Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Length;
            var previous = 0;
            foreach (var entry in file.Entries)
            {
                if (entry.Line < 1 || entry.Line > lines || entry.Line <= previous)
                {
                    throw new InvariantException($"Entry {entry.Name} is on line {entry.Line}, out of order or outside the file's {lines} lines.");
                }

                if (entry.Name.Length == 0 || !ReferenceEquals(file.Find(entry.Name), file.Entries.Last(e => e.Name == entry.Name)))
                {
                    throw new InvariantException($"Find('{entry.Name}') did not return the last entry with that name.");
                }

                previous = entry.Line;
            }
        }

        foreach (var (name, value) in layers.Variables)
        {
            var history = layers.History(name);
            if (history.Count == 0 || history[^1].Value != value)
            {
                throw new InvariantException($"Variables['{name}'] is not the last entry in History.");
            }
        }

        // Every variable is linked back to its lines, and a secret-looking one never reaches the report.
        var resolver = new DotEnvResolver(layers);
        var diagnostics = new TraceDiagnostics();
        foreach (var name in layers.Variables.Keys.Take(200))
        {
            var steps = resolver.Resolve(Step(LayerKind.DotNetProvider, new EnvironmentVariableRef(name), "seen"), diagnostics);
            if (steps.Count == 0)
            {
                throw new InvariantException($"The dotenv resolver did not link '{name}'.");
            }
        }
    }

    // compose.yaml %% compose.override.yaml %% the project env file. No other file is read, so env_file, extends,
    // include and secret files are only reported as not read.
    private static void ComposeTarget(ReadOnlySpan<byte> data)
    {
        var parts = Split(data);
        var directory = Path.Join(Scratch, "compose");
        Directory.CreateDirectory(directory);
        var envFile = Path.Join(directory, "project.envfile");
        WriteOrDelete(envFile, parts.ElementAtOrDefault(2) ?? "");
        var options = new ComposeOptions();
        options.EnvFiles.Add(envFile);
        foreach (var (name, value) in FuzzShell)
        {
            options.Variables[name] = value;
        }

        ComposeProject project;
        try
        {
            project = ComposeProject.Parse(parts.Take(2).Select((text, i) => (i == 0 ? "compose.yaml" : "compose.override.yaml", text)), options);
        }
        catch (FormatException)
        {
            return;
        }

        var redacted = new TraceOptions().RedactedText;
        foreach (var service in project.ServiceNames.Take(10).Prepend(null))
        {
            var report = ConfigTracer.ForCompose(project, service).Trace();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var setting in report.Settings)
            {
                if (!keys.Add(setting.Key))
                {
                    throw new InvariantException($"ForCompose listed '{setting.Key}' twice.");
                }

                if (!ReferenceEquals(report.Find(setting.Key), setting))
                {
                    throw new InvariantException($"Find('{setting.Key}') did not return the listed setting.");
                }

                if (setting.Steps.Count == 0)
                {
                    throw new InvariantException($"'{setting.Key}' has no layers.");
                }

                // A secret, or a variable named like one, must never reach the report unredacted.
                if ((setting.Key.StartsWith("/run/secrets/", StringComparison.Ordinal) || setting.Key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase))
                    && setting.Value is not null && setting.Value != redacted)
                {
                    throw new InvariantException($"'{setting.Key}' is secret but its value is not redacted.");
                }
            }

            var text = report.ToText();
            var json = report.ToJson();
            if (text.Contains("shell-s3cret", StringComparison.Ordinal) || json.Contains("shell-s3cret", StringComparison.Ordinal))
            {
                throw new InvariantException("The shell's DB_PASSWORD reached the report unredacted.");
            }
        }
    }
}
