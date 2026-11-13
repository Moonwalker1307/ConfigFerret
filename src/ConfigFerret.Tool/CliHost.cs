namespace ConfigFerret.Tool;

/// <summary>What the command reads from and writes to, so tests can run it without a console or a real environment.</summary>
internal sealed class CliHost
{
    /// <summary>Where the report goes, unless <c>--output</c> names a file.</summary>
    public required TextWriter Out { get; init; }

    /// <summary>Errors, warnings and notes.</summary>
    public required TextWriter Error { get; init; }

    /// <summary>Read for <c>-</c> in place of a file name.</summary>
    public TextReader In { get; init; } = TextReader.Null;

    /// <summary>
    /// The tool's own environment. Only the GitHub token, the Octopus API key and server, and the folders that hold user
    /// secrets are read from it. It never reaches a trace: the traced app's environment comes from the manifests,
    /// <c>--env</c> and <c>--env-file</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>Creates the client for the GitHub and Octopus APIs. Null uses the packages' own client, which does not follow redirects.</summary>
    public Func<HttpClient>? HttpClientFactory { get; init; }

    public string? GetEnvironmentVariable(string name) =>
        Environment.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    public static CliHost FromConsole()
    {
        var environment = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = entry.Value as string;
        }

        return new CliHost { Out = Console.Out, Error = Console.Error, In = Console.In, Environment = environment };
    }
}
