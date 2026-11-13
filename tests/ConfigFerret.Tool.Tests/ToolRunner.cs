using ConfigFerret.Tests.Shared;

namespace ConfigFerret.Tool.Tests;

/// <summary>Runs the command in process with its own environment, standard input and HTTP handler.</summary>
internal sealed class ToolRunner
{
    public Dictionary<string, string?> Environment { get; } = new(StringComparer.Ordinal);

    public string StandardInput { get; set; } = string.Empty;

    public FakeHttpHandler? Http { get; set; }

    public static string Golden(params string[] parts) => Path.Join([AppContext.BaseDirectory, "TestData", "golden", .. parts]);

    public async Task<ToolResult> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var input = new StringReader(StandardInput);
        var host = new CliHost
        {
            Out = output,
            Error = error,
            In = input,
            Environment = Environment,
            HttpClientFactory = Http is null ? null : Http.Client,
        };

        var code = await Cli.RunAsync(args, host, TestContext.Current.CancellationToken);
        return new ToolResult(code, output.ToString(), error.ToString());
    }
}

internal sealed record ToolResult(int ExitCode, string Output, string Error);
