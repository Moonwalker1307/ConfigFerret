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
        Description = "The hosting environment, which picks appsettings.{Environment}.json. Defaults to ASPNETCORE_ENVIRONMENT or DOTNET_ENVIRONMENT from the app's variables, else Production.",
        HelpName = "name",
    };

    public Option<string[]> Config { get; } = new("--config")
    {
        Description = "Another JSON settings file the app loads after the appsettings files. Repeat in load order.",
        HelpName = "file",
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
        Description = "The prefix the app passes to AddEnvironmentVariables. Repeat for each call, in order. Defaults to one call without a prefix.",
        HelpName = "prefix",
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
        command.Options.Add(Env);
        command.Options.Add(EnvFile);
        command.Options.Add(EnvPrefix);
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
        Description = "The workload or pod name. Needed when the manifests hold more than one.",
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
        Description = "Let generators read files outside their kustomization's folder, like --load-restrictor LoadRestrictionsNone.",
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

    public void AddTo(Command command)
    {
        foreach (var option in new Option[]
        {
            Chart, Values, Set, Kustomize, KustomizeAllowFilesOutsideFolder,
            GitHubRepository, GitHubEnvironment, GitHubWorkflow, GitHubApiUrl,
            OctopusProject, OctopusServer, OctopusSpace, OctopusEnvironment, OctopusMachine, OctopusRole, OctopusChannel, OctopusStep, OctopusTenantTag,
        })
        {
            command.Options.Add(option);
        }
    }
}
