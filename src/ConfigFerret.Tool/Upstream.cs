using System.CommandLine;
using ConfigFerret.GitHub;
using ConfigFerret.Helm;
using ConfigFerret.Kustomize;
using ConfigFerret.Octopus;

namespace ConfigFerret.Tool;

/// <summary>Adds the Helm, Kustomize, GitHub Actions and Octopus Deploy layers asked for on the command line.</summary>
internal static class Upstream
{
    // Credentials are read from the environment only, never from an option, so they stay out of shell history and process lists.
    internal static readonly string[] GitHubTokenVariables = ["GITHUB_TOKEN", "GH_TOKEN"];
    internal static readonly string[] OctopusApiKeyVariables = ["OCTOPUS_API_KEY", "OCTOPUS_CLI_API_KEY"];
    internal static readonly string[] OctopusServerVariables = ["OCTOPUS_URL", "OCTOPUS_CLI_SERVER"];

    public static void AddHelm(ConfigTracer tracer, string? chart, IReadOnlyList<string> values, IReadOnlyList<string> set)
    {
        if (chart is null)
        {
            if (values.Count > 0 || set.Count > 0)
            {
                throw new UsageException("--values and --set need --chart, since the chart's templates link values to configuration keys.");
            }

            return;
        }

        InputReader.RequireDirectory(chart, "The Helm chart folder");
        foreach (var file in values)
        {
            InputReader.RequireFile(file);
        }

        tracer.WithHelm(HelmChart.Load(chart, values, set));
    }

    public static void AddKustomize(ConfigTracer tracer, string? directory, bool allowFilesOutsideFolder)
    {
        if (directory is null)
        {
            return;
        }

        InputReader.RequireDirectory(directory, "The Kustomize folder");
        tracer.WithKustomize(Kustomization.Load(directory, allowFilesOutsideFolder));
    }

    public static async Task AddGitHubAsync(ConfigTracer tracer, UpstreamOptions options, ParseResult result, CliHost host, CancellationToken cancellationToken)
    {
        var repository = result.GetValue(options.GitHubRepository);
        if (repository is null)
        {
            if (result.GetValue(options.GitHubEnvironment) is not null || result.GetValue(options.GitHubWorkflow) is { Length: > 0 })
            {
                throw new UsageException("--github-environment and --github-workflow need --github-repo.");
            }

            return;
        }

        var parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new UsageException($"--github-repo expects owner/name, got '{repository}'.");
        }

        var token = FirstVariable(host, GitHubTokenVariables)
            ?? throw new UsageException("--github-repo needs a token in the GITHUB_TOKEN or GH_TOKEN environment variable. Tokens are never read from options.");
        var github = new GitHubVariablesOptions
        {
            Owner = parts[0],
            Repository = parts[1],
            Environment = result.GetValue(options.GitHubEnvironment),
            Token = token,
        };
        if ((result.GetValue(options.GitHubApiUrl) ?? host.GetEnvironmentVariable("GITHUB_API_URL")) is { } apiUrl)
        {
            github.ApiBaseAddress = ParseUrl(apiUrl, "--github-api-url");
        }

        var workflows = result.GetValue(options.GitHubWorkflow) ?? [];
        foreach (var workflow in workflows)
        {
            InputReader.RequireFile(workflow);
        }

        GitHubVariables variables;
        if (host.HttpClientFactory is { } createClient)
        {
            using var client = createClient();
            variables = await GitHubVariables.LoadAsync(github, client, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            variables = await GitHubVariables.LoadAsync(github, cancellationToken).ConfigureAwait(false);
        }

        tracer.WithGitHub(variables, o =>
        {
            foreach (var workflow in workflows)
            {
                o.AddWorkflow(workflow);
            }
        });
    }

    public static async Task AddOctopusAsync(ConfigTracer tracer, UpstreamOptions options, ParseResult result, CliHost host, CancellationToken cancellationToken)
    {
        var project = result.GetValue(options.OctopusProject);
        if (project is null)
        {
            if (result.GetValue(options.OctopusServer) is not null || result.GetValue(options.OctopusEnvironment) is not null || result.GetValue(options.OctopusSpace) is not null)
            {
                throw new UsageException("The --octopus-* options need --octopus-project.");
            }

            return;
        }

        var server = result.GetValue(options.OctopusServer) ?? FirstVariable(host, OctopusServerVariables)
            ?? throw new UsageException("--octopus-project needs the server address in --octopus-server or the OCTOPUS_URL environment variable.");
        var apiKey = FirstVariable(host, OctopusApiKeyVariables)
            ?? throw new UsageException("--octopus-project needs an API key in the OCTOPUS_API_KEY environment variable. API keys are never read from options.");
        var octopus = new OctopusVariablesOptions
        {
            ServerUri = ParseUrl(server, "--octopus-server"),
            ApiKey = apiKey,
            Project = project,
            Space = result.GetValue(options.OctopusSpace),
        };

        OctopusVariables variables;
        if (host.HttpClientFactory is { } createClient)
        {
            using var client = createClient();
            variables = await OctopusVariables.LoadAsync(octopus, client, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            variables = await OctopusVariables.LoadAsync(octopus, cancellationToken).ConfigureAwait(false);
        }

        tracer.WithOctopus(variables, o =>
        {
            o.Environment = result.GetValue(options.OctopusEnvironment);
            o.Machine = result.GetValue(options.OctopusMachine);
            o.Channel = result.GetValue(options.OctopusChannel);
            o.Step = result.GetValue(options.OctopusStep);
            foreach (var role in result.GetValue(options.OctopusRole) ?? [])
            {
                o.Roles.Add(role);
            }

            foreach (var tag in result.GetValue(options.OctopusTenantTag) ?? [])
            {
                o.TenantTags.Add(tag);
            }
        });
    }

    private static string? FirstVariable(CliHost host, IEnumerable<string> names) =>
        names.Select(host.GetEnvironmentVariable).FirstOrDefault(v => v is not null);

    // Relative API paths resolve under the base address only when it ends with a slash.
    private static Uri ParseUrl(string text, string optionName)
    {
        if (!Uri.TryCreate(text.EndsWith('/') ? text : text + "/", UriKind.Absolute, out var uri))
        {
            throw new UsageException($"{optionName} expects an absolute URL, got '{text}'.");
        }

        return uri;
    }
}
