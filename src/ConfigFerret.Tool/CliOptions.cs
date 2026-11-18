using System.CommandLine;

namespace ConfigFerret.Tool;

/// <summary>The report formats.</summary>
internal enum OutputFormat
{
    Text,
    Json,
}

/// <summary>How the report is written.</summary>
internal sealed class OutputOptions
{
    public Option<OutputFormat> Format { get; } = new("--format")
    {
        Description = "text (the default) or json.",
        DefaultValueFactory = _ => OutputFormat.Text,

        // The parser's own error quotes the text, which could be a value put after --format by mistake.
        CustomParser = result =>
        {
            if (result.Tokens.Count == 1 && Enum.TryParse<OutputFormat>(result.Tokens[0].Value, ignoreCase: true, out var format)
                && Enum.IsDefined(format))
            {
                return format;
            }

            result.AddError("--format expects text or json.");
            return OutputFormat.Text;
        },
    };

    public Option<string?> Output { get; } = new("--output")
    {
        Description = "Write the report to this file instead of standard output.",
        HelpName = "file",
    };

    public Option<bool> RevealSecrets { get; } = new("--reveal-secrets")
    {
        Description = "Show secret values instead of ***. Use only on your own machine, never in CI logs.",
    };

    public void AddTo(Command command)
    {
        command.Options.Add(Format);
        command.Options.Add(Output);
        command.Options.Add(RevealSecrets);
    }
}

/// <summary>Where the app's own configuration comes from, for trace and unused.</summary>
internal sealed class AppOptions
{
    public Option<string> App { get; } = new("--app", "-a")
    {
        Description = "The app folder, holding appsettings.json. Defaults to the current folder.",
        HelpName = "folder",
        DefaultValueFactory = _ => ".",
    };

    public Option<string?> Environment { get; } = new("--environment", "-e")
    {
        Description = "The hosting environment, which picks appsettings.{Environment}.json. Defaults to --environment in the app's command line, else DOTNET_ENVIRONMENT, else ASPNETCORE_ENVIRONMENT from the app's variables, else Production.",
        HelpName = "name",
    };

    public Option<string[]> Config { get; } = new("--config")
    {
        Description = "A JSON settings file the app adds itself, loaded after the environment variables and the command line. Repeat in load order.",
        HelpName = "file",
    };

    public Option<bool> ConfigBeforeEnv { get; } = new("--config-before-env")
    {
        Description = "Load the --config files right after the appsettings files, below user secrets, the environment variables and the command line, for an app that adds the variables again after its file.",
    };

    public Option<string[]> Env { get; } = new("--env")
    {
        Description = "An environment variable the app sees, as NAME=VALUE. Repeatable. Wins over --env-file and the manifests.",
        HelpName = "NAME=VALUE",
    };

    public Option<string[]> EnvFile { get; } = new("--env-file")
    {
        Description = "A file of NAME=VALUE lines the app sees as environment variables. Repeatable. Wins over the manifests.",
        HelpName = "file",
    };

    public Option<string[]> EnvPrefix { get; } = new("--env-prefix")
    {
        Description = "The prefix of an AddEnvironmentVariables call the app makes itself, after the call without a prefix the host makes. Repeat for each call, in order. Loaded below the app's command line, where an app that makes the call itself reads it above.",
        HelpName = "prefix",
    };

    public Option<bool> NoDefaultEnv { get; } = new("--no-default-env")
    {
        Description = "Leave out the environment variables the host adds by itself (with the prefixes ASPNETCORE_ and DOTNET_, and without a prefix), for an app that builds its configuration without the host defaults.",
    };

    public Option<string?> UserSecretsId { get; } = new("--user-secrets-id")
    {
        Description = "Read this app's user secrets. Defaults to the UserSecretsId in the app folder's project file, read in Development only.",
        HelpName = "id",
    };

    public Option<bool> NoUserSecrets { get; } = new("--no-user-secrets")
    {
        Description = "Don't read user secrets.",
    };

    public void AddTo(Command command)
    {
        command.Options.Add(App);
        command.Options.Add(Environment);
        command.Options.Add(Config);
        command.Options.Add(ConfigBeforeEnv);
        command.Options.Add(Env);
        command.Options.Add(EnvFile);
        command.Options.Add(EnvPrefix);
        command.Options.Add(NoDefaultEnv);
        command.Options.Add(UserSecretsId);
        command.Options.Add(NoUserSecrets);
    }
}

/// <summary>Rendered Kubernetes manifests and the container to look at.</summary>
internal sealed class KubernetesOptions(bool required)
{
    public Option<string[]> Manifests { get; } = new("--manifests", "-m")
    {
        Description = required
            ? "Manifest files or folders holding the pod or workload and its ConfigMaps and Secrets (kubectl get -o yaml, helm template, kustomize build). - reads standard input. Repeatable."
            : "Rendered manifests the app is deployed with (helm template, kustomize build, kubectl get -o yaml). The container's environment variables are passed to the app. - reads standard input. Repeatable.",
        HelpName = "file",
        Required = required,
    };

    public Option<string?> Workload { get; } = new("--workload", "-w")
    {
        Description = "The workload or pod name, or namespace/name when the name is in more than one namespace (/name for the one without a namespace). Needed when the manifests hold more than one.",
        HelpName = "name",
    };

    public Option<string?> Container { get; } = new("--container", "-c")
    {
        Description = "The container name. Needed when the workload has more than one.",
        HelpName = "name",
    };

    public void AddTo(Command command)
    {
        command.Options.Add(Manifests);
        command.Options.Add(Workload);
        command.Options.Add(Container);
    }
}

/// <summary>A Docker Compose project and the service to look at, for the container command.</summary>
internal sealed class ComposeCliOptions
{
    public Option<string[]> Files { get; } = new("--compose-file")
    {
        Description = "A Docker Compose file, as passed to docker compose -f. Repeat in order. Use instead of --manifests.",
        HelpName = "file",
    };

    public Option<string?> Service { get; } = new("--compose-service")
    {
        Description = "The compose service. Needed when the project has more than one.",
        HelpName = "name",
    };

    public Option<string[]> EnvFiles { get; } = new("--compose-env-file")
    {
        Description = "An env file for ${VAR} in the compose files, as passed to docker compose --env-file. Repeatable. Defaults to the project's .env; your shell's variables are never read.",
        HelpName = "file",
    };

    public Option<bool> AllowFilesOutsideProject { get; } = new("--compose-allow-files-outside-project")
    {
        Description = "Let the compose files name files outside the project directory (env_file, extends, include, config and secret files), as docker compose does.",
    };

    public void AddTo(Command command)
    {
        command.Options.Add(Files);
        command.Options.Add(Service);
        command.Options.Add(EnvFiles);
        command.Options.Add(AllowFilesOutsideProject);
    }

    /// <summary>A trace of the compose service, or null when no compose file was passed.</summary>
    public ConfigTracer? Tracer(ParseResult result, KubernetesOptions kubernetes)
    {
        var files = result.GetValue(Files) ?? [];
        var manifests = result.GetValue(kubernetes.Manifests) ?? [];
        if (files.Length == 0)
        {
            if (manifests.Length == 0)
            {
                throw new UsageException("container needs --manifests or --compose-file.");
            }

            if (result.GetValue(Service) is not null || result.GetValue(EnvFiles) is { Length: > 0 } || result.GetValue(AllowFilesOutsideProject))
            {
                throw new UsageException("--compose-service, --compose-env-file and --compose-allow-files-outside-project need --compose-file.");
            }

            return null;
        }

        if (manifests.Length > 0 || result.GetValue(kubernetes.Workload) is not null || result.GetValue(kubernetes.Container) is not null)
        {
            throw new UsageException("--compose-file can't be combined with --manifests, --workload or --container.");
        }

        // The tool's own environment is never read, so the shell's variables and tokens stay out of the trace.
        var options = new ConfigFerret.Compose.ComposeOptions { AllowFilesOutsideProject = result.GetValue(AllowFilesOutsideProject) };
        foreach (var envFile in result.GetValue(EnvFiles) ?? [])
        {
            options.EnvFiles.Add(InputReader.RequireFile(envFile));
        }

        var project = ConfigFerret.Compose.ComposeProject.Load(files.Select(InputReader.RequireFile), options);
        return ConfigTracer.ForCompose(project, result.GetValue(Service));
    }
}

/// <summary>Helm, Kustomize, GitHub Actions and Octopus Deploy: the layers upstream of Kubernetes and the app.</summary>
internal sealed class UpstreamOptions
{
    public Option<string?> Chart { get; } = new("--chart")
    {
        Description = "The Helm chart folder (Chart.yaml, values.yaml, templates/).",
        HelpName = "folder",
    };

    public Option<string[]> Values { get; } = new("--values", "-f")
    {
        Description = "A Helm values file, as passed to helm install -f, or the output of helm get values. Repeat in order.",
        HelpName = "file",
    };

    public Option<string[]> Set { get; } = new("--set")
    {
        Description = "A Helm --set value, for example db.timeout=42. Repeat in order.",
        HelpName = "path=value",
    };

    public Option<string?> Kustomize { get; } = new("--kustomize", "-k")
    {
        Description = "The Kustomize overlay folder you deploy with.",
        HelpName = "folder",
    };

    public Option<bool> KustomizeAllowFilesOutsideFolder { get; } = new("--kustomize-allow-files-outside-folder")
    {
        Description = "Let generators read files outside their kustomization's folder, and bases on another drive or share, like --load-restrictor LoadRestrictionsNone.",
    };

    public Option<string?> GitHubRepository { get; } = new("--github-repo")
    {
        Description = "Read GitHub Actions variables and secret names from this repository (owner/name). Needs GITHUB_TOKEN or GH_TOKEN.",
        HelpName = "owner/name",
    };

    public Option<string?> GitHubEnvironment { get; } = new("--github-environment")
    {
        Description = "The GitHub deployment environment whose variables apply.",
        HelpName = "name",
    };

    public Option<string[]> GitHubWorkflow { get; } = new("--github-workflow")
    {
        Description = "A workflow file whose env: lines map names to ${{ vars.X }} and ${{ secrets.X }}. Repeatable.",
        HelpName = "file",
    };

    public Option<string?> GitHubApiUrl { get; } = new("--github-api-url")
    {
        Description = "The GitHub API address, for GitHub Enterprise Server. Defaults to GITHUB_API_URL, else https://api.github.com/.",
        HelpName = "url",
    };

    public Option<string?> OctopusProject { get; } = new("--octopus-project")
    {
        Description = "Read Octopus Deploy variables for this project (name, slug or id). Needs OCTOPUS_API_KEY.",
        HelpName = "project",
    };

    public Option<string?> OctopusServer { get; } = new("--octopus-server")
    {
        Description = "The Octopus server address. Defaults to OCTOPUS_URL.",
        HelpName = "url",
    };

    public Option<string?> OctopusSpace { get; } = new("--octopus-space")
    {
        Description = "The Octopus space (name or id). Defaults to the default space.",
        HelpName = "space",
    };

    public Option<string?> OctopusEnvironment { get; } = new("--octopus-environment")
    {
        Description = "The Octopus environment the deployment goes to, for scoped variables.",
        HelpName = "name",
    };

    public Option<string?> OctopusMachine { get; } = new("--octopus-machine")
    {
        Description = "The Octopus deployment target, for scoped variables.",
        HelpName = "name",
    };

    public Option<string[]> OctopusRole { get; } = new("--octopus-role")
    {
        Description = "A target role (tag), for scoped variables. Repeatable.",
        HelpName = "role",
    };

    public Option<string?> OctopusChannel { get; } = new("--octopus-channel")
    {
        Description = "The Octopus channel, for scoped variables.",
        HelpName = "name",
    };

    public Option<string?> OctopusStep { get; } = new("--octopus-step")
    {
        Description = "The deployment process step, for scoped variables.",
        HelpName = "name",
    };

    public Option<string[]> OctopusTenantTag { get; } = new("--octopus-tenant-tag")
    {
        Description = "A tenant tag such as Region/EU, for scoped variables. Repeatable.",
        HelpName = "tag",
    };

    public Option<bool> OctopusProcess { get; } = new("--octopus-process")
    {
        Description = "Also read the deployment process, to name the steps that replace values (needs ProcessView).",
    };

    public Option<string?> OctopusRelease { get; } = new("--octopus-release")
    {
        Description = "Read the variables and deployment process from the snapshot of this release.",
        HelpName = "version",
    };

    public Option<string?> OctopusGitRef { get; } = new("--octopus-git-ref")
    {
        Description = "For a config-as-code project, the branch or Git reference to read the process from.",
        HelpName = "ref",
    };

    public void AddTo(Command command)
    {
        foreach (var option in new Option[]
        {
            Chart, Values, Set, Kustomize, KustomizeAllowFilesOutsideFolder,
            GitHubRepository, GitHubEnvironment, GitHubWorkflow, GitHubApiUrl,
            OctopusProject, OctopusServer, OctopusSpace, OctopusEnvironment, OctopusMachine, OctopusRole, OctopusChannel, OctopusStep, OctopusTenantTag,
            OctopusProcess, OctopusRelease, OctopusGitRef,
        })
        {
            command.Options.Add(option);
        }
    }
}
