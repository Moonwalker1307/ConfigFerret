using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ConfigFerret.Desktop.Model;

/// <summary>The kinds of source a profile can hold.</summary>
public enum SourceKind
{
    /// <summary>An app folder with appsettings.json, appsettings.{Environment}.json and user secrets.</summary>
    AppSettings,

    /// <summary>Environment variables the app gets, from a dotenv file or typed in.</summary>
    EnvironmentVariables,

    /// <summary>Kubernetes manifests: the workload, its ConfigMaps and Secrets.</summary>
    Kubernetes,

    /// <summary>A Helm chart and its values files.</summary>
    Helm,

    /// <summary>A Kustomize kustomization folder.</summary>
    Kustomize,

    /// <summary>GitHub Actions variables and secret names, read over the GitHub API.</summary>
    GitHub,

    /// <summary>Octopus Deploy variables, read over the Octopus API.</summary>
    Octopus,
}

/// <summary>One source of configuration in a profile. Saved as JSON, so it must never hold a credential.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AppSettingsSource), "appsettings")]
[JsonDerivedType(typeof(EnvironmentVariablesSource), "environment")]
[JsonDerivedType(typeof(KubernetesSource), "kubernetes")]
[JsonDerivedType(typeof(HelmSource), "helm")]
[JsonDerivedType(typeof(KustomizeSource), "kustomize")]
[JsonDerivedType(typeof(GitHubSource), "github")]
[JsonDerivedType(typeof(OctopusSource), "octopus")]
public abstract partial class SourceSettings : ObservableObject
{
    /// <summary>A stable id, also used to name the source's stored credential.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The kind of source.</summary>
    [JsonIgnore]
    public abstract SourceKind Kind { get; }

    /// <summary>The kind as shown in the source list.</summary>
    [JsonIgnore]
    public string KindName => SourceCatalog.DisplayName(Kind);

    /// <summary>A name for the source, shown in the source list.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>Whether the source is used in the trace.</summary>
    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    /// <summary>The result of the last trace for this source: empty when it worked, else what went wrong.</summary>
    [JsonIgnore]
    [ObservableProperty]
    public partial string? Problem { get; set; }

    /// <summary>Joins a list for a multi-line text box.</summary>
    protected static string JoinLines(IEnumerable<string> lines) => string.Join(Environment.NewLine, lines);

    /// <summary>Splits a multi-line text box into its non-empty, trimmed lines.</summary>
    protected static List<string> SplitLines(string? text) =>
        (text ?? string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
}

/// <summary>An app folder, read the way the .NET host reads it.</summary>
public sealed partial class AppSettingsSource : SourceSettings
{
    /// <inheritdoc />
    public override SourceKind Kind => SourceKind.AppSettings;

    /// <summary>The folder that holds appsettings.json (and usually the project file).</summary>
    [ObservableProperty]
    public partial string Folder { get; set; } = string.Empty;

    /// <summary>The hosting environment, for example Production. Picks appsettings.{Environment}.json.</summary>
    [ObservableProperty]
    public partial string EnvironmentName { get; set; } = "Production";

    /// <summary>Load user secrets (UserSecretsId from the project file) when the environment is Development.</summary>
    [ObservableProperty]
    public partial bool UseUserSecrets { get; set; } = true;

    /// <summary>More JSON settings files, added after the app's own.</summary>
    public List<string> ExtraFiles { get; set; } = [];

    /// <summary>The prefixes the app's environment variables provider strips, for example ORDERS_. Empty means no prefix.</summary>
    public List<string> EnvironmentPrefixes { get; set; } = [];

    /// <summary><see cref="ExtraFiles"/> as one file per line.</summary>
    [JsonIgnore]
    public string ExtraFilesText
    {
        get => JoinLines(ExtraFiles);
        set
        {
            ExtraFiles = SplitLines(value);
            OnPropertyChanged();
        }
    }

    /// <summary><see cref="EnvironmentPrefixes"/> as one prefix per line.</summary>
    [JsonIgnore]
    public string EnvironmentPrefixesText
    {
        get => JoinLines(EnvironmentPrefixes);
        set
        {
            EnvironmentPrefixes = SplitLines(value);
            OnPropertyChanged();
        }
    }
}

/// <summary>Environment variables the app gets, beyond those from Kubernetes.</summary>
public sealed partial class EnvironmentVariablesSource : SourceSettings
{
    /// <inheritdoc />
    public override SourceKind Kind => SourceKind.EnvironmentVariables;

    /// <summary>A dotenv-style file: NAME=VALUE per line.</summary>
    [ObservableProperty]
    public partial string File { get; set; } = string.Empty;

    /// <summary>NAME=VALUE pairs, one per line, added after the file. Don't put secrets here: this is saved as plain text.</summary>
    [ObservableProperty]
    public partial string Variables { get; set; } = string.Empty;
}

/// <summary>Kubernetes manifests: rendered output, kubectl get -o yaml, or a folder of them.</summary>
public sealed partial class KubernetesSource : SourceSettings
{
    /// <inheritdoc />
    public override SourceKind Kind => SourceKind.Kubernetes;

    /// <summary>Manifest files or folders.</summary>
    public List<string> Paths { get; set; } = [];

    /// <summary>The workload name. Needed only when the manifests hold more than one.</summary>
    [ObservableProperty]
    public partial string Workload { get; set; } = string.Empty;

    /// <summary>The container name. Needed only when the workload has more than one.</summary>
    [ObservableProperty]
    public partial string Container { get; set; } = string.Empty;

    /// <summary><see cref="Paths"/> as one path per line.</summary>
    [JsonIgnore]
    public string PathsText
    {
        get => JoinLines(Paths);
        set
        {
            Paths = SplitLines(value);
            OnPropertyChanged();
        }
    }
}

/// <summary>A Helm chart folder with the values files and --set values of a release.</summary>
public sealed partial class HelmSource : SourceSettings
{
    /// <inheritdoc />
    public override SourceKind Kind => SourceKind.Helm;

    /// <summary>The chart folder.</summary>
    [ObservableProperty]
    public partial string ChartFolder { get; set; } = string.Empty;

    /// <summary>Values files, lowest priority first.</summary>
    public List<string> ValuesFiles { get; set; } = [];

    /// <summary>--set values (path=value), applied last.</summary>
    public List<string> SetValues { get; set; } = [];

    /// <summary><see cref="ValuesFiles"/> as one file per line.</summary>
    [JsonIgnore]
    public string ValuesFilesText
    {
        get => JoinLines(ValuesFiles);
        set
        {
            ValuesFiles = SplitLines(value);
            OnPropertyChanged();
        }
    }

    /// <summary><see cref="SetValues"/> as one value per line.</summary>
    [JsonIgnore]
    public string SetValuesText
    {
        get => JoinLines(SetValues);
        set
        {
            SetValues = SplitLines(value);
            OnPropertyChanged();
        }
    }
}

/// <summary>A Kustomize overlay or base folder.</summary>
public sealed partial class KustomizeSource : SourceSettings
{
    /// <inheritdoc />
    public override SourceKind Kind => SourceKind.Kustomize;

    /// <summary>The folder with kustomization.yaml.</summary>
    [ObservableProperty]
    public partial string Folder { get; set; } = string.Empty;

    /// <summary>Allow generators to read files outside the folder.</summary>
    [ObservableProperty]
    public partial bool AllowFilesOutsideFolder { get; set; }
}

/// <summary>Where a connection's credential comes from.</summary>
public enum CredentialSource
{
    /// <summary>The operating system's credential store: Windows Credential Manager, the macOS Keychain or the Secret Service (libsecret) on Linux.</summary>
    Stored,

    /// <summary>An environment variable of the desktop app's process, for example GITHUB_TOKEN.</summary>
    EnvironmentVariable,
}

/// <summary>A source that signs in to a service. The credential itself is never saved in the profile.</summary>
public abstract partial class ConnectionSource : SourceSettings
{
    /// <summary>Where the credential comes from.</summary>
    [ObservableProperty]
    public partial CredentialSource CredentialSource { get; set; } = CredentialSource.Stored;

    /// <summary>The environment variable that holds the credential, when <see cref="CredentialSource"/> is <see cref="CredentialSource.EnvironmentVariable"/>.</summary>
    [ObservableProperty]
    public partial string CredentialVariable { get; set; } = string.Empty;

    /// <summary>The name of the credential in the operating system's credential store.</summary>
    [JsonIgnore]
    public string CredentialAccount => CredentialAccountName ?? $"{Kind}/{Id:N}";

    /// <summary>Uses a shared credential, such as the signed-in GitHub account's token, instead of one for this source alone.</summary>
    [JsonIgnore]
    public string? CredentialAccountName { get; set; }

    /// <summary>What the credential is called, for example "token" or "API key".</summary>
    [JsonIgnore]
    public abstract string CredentialName { get; }
}

/// <summary>A GitHub repository whose Actions variables and secret names to read.</summary>
public sealed partial class GitHubSource : ConnectionSource
{
    /// <inheritdoc />
    public override SourceKind Kind => SourceKind.GitHub;

    /// <inheritdoc />
    public override string CredentialName => "token";


    /// <summary>The repository as owner/name, for example contoso/api.</summary>
    [ObservableProperty]
    public partial string Repository { get; set; } = string.Empty;

    /// <summary>The deployment environment, for example production. Empty for repository and organization variables only.</summary>
    [ObservableProperty]
    public partial string Environment { get; set; } = string.Empty;

    /// <summary>The REST API address. Empty for github.com; https://HOST/api/v3/ for GitHub Enterprise Server.</summary>
    [ObservableProperty]
    public partial string ApiUrl { get; set; } = string.Empty;

    /// <summary>Workflow files whose env blocks link names to vars.X and secrets.X.</summary>
    public List<string> Workflows { get; set; } = [];

    /// <summary><see cref="Workflows"/> as one file per line.</summary>
    [JsonIgnore]
    public string WorkflowsText
    {
        get => JoinLines(Workflows);
        set
        {
            Workflows = SplitLines(value);
            OnPropertyChanged();
        }
    }
}

/// <summary>An Octopus Deploy project, and the deployment whose scoped variables apply.</summary>
public sealed partial class OctopusSource : ConnectionSource
{
    /// <inheritdoc />
    public override SourceKind Kind => SourceKind.Octopus;

    /// <inheritdoc />
    public override string CredentialName => "API key";


    /// <summary>The server address, for example https://contoso.octopus.app/.</summary>
    [ObservableProperty]
    public partial string Server { get; set; } = string.Empty;

    /// <summary>The space, by name or id. Empty for the default space.</summary>
    [ObservableProperty]
    public partial string Space { get; set; } = string.Empty;

    /// <summary>The project, by name, slug or id.</summary>
    [ObservableProperty]
    public partial string Project { get; set; } = string.Empty;

    /// <summary>The environment deployed to, for example Production.</summary>
    [ObservableProperty]
    public partial string Environment { get; set; } = string.Empty;

    /// <summary>The deployment target (machine) name.</summary>
    [ObservableProperty]
    public partial string Machine { get; set; } = string.Empty;

    /// <summary>The release channel.</summary>
    [ObservableProperty]
    public partial string Channel { get; set; } = string.Empty;

    /// <summary>The deployment process step that wrote the configuration.</summary>
    [ObservableProperty]
    public partial string Step { get; set; } = string.Empty;

    /// <summary>Include the library variable sets the project uses.</summary>
    [ObservableProperty]
    public partial bool IncludeLibraryVariableSets { get; set; } = true;

    /// <summary>Target tags (roles) of the deployment target.</summary>
    public List<string> Roles { get; set; } = [];

    /// <summary>Tenant tags, such as Region/West.</summary>
    public List<string> TenantTags { get; set; } = [];

    /// <summary><see cref="Roles"/> as one role per line.</summary>
    [JsonIgnore]
    public string RolesText
    {
        get => JoinLines(Roles);
        set
        {
            Roles = SplitLines(value);
            OnPropertyChanged();
        }
    }

    /// <summary><see cref="TenantTags"/> as one tag per line.</summary>
    [JsonIgnore]
    public string TenantTagsText
    {
        get => JoinLines(TenantTags);
        set
        {
            TenantTags = SplitLines(value);
            OnPropertyChanged();
        }
    }
}

/// <summary>Names and factories for each kind of source.</summary>
public static class SourceCatalog
{
    /// <summary>Every kind, in the order the Add menu lists them.</summary>
    public static IReadOnlyList<SourceKind> Kinds { get; } = Enum.GetValues<SourceKind>();

    /// <summary>The name shown for a kind.</summary>
    public static string DisplayName(SourceKind kind) => kind switch
    {
        SourceKind.AppSettings => "App settings",
        SourceKind.EnvironmentVariables => "Environment variables",
        SourceKind.Kubernetes => "Kubernetes manifests",
        SourceKind.Helm => "Helm chart",
        SourceKind.Kustomize => "Kustomize",
        SourceKind.GitHub => "GitHub Actions",
        SourceKind.Octopus => "Octopus Deploy",
        _ => kind.ToString(),
    };

    /// <summary>Creates a new, empty source of <paramref name="kind"/>.</summary>
    public static SourceSettings Create(SourceKind kind)
    {
        SourceSettings source = kind switch
        {
            SourceKind.AppSettings => new AppSettingsSource(),
            SourceKind.EnvironmentVariables => new EnvironmentVariablesSource(),
            SourceKind.Kubernetes => new KubernetesSource(),
            SourceKind.Helm => new HelmSource(),
            SourceKind.Kustomize => new KustomizeSource(),
            SourceKind.GitHub => new GitHubSource { CredentialVariable = "GITHUB_TOKEN" },
            SourceKind.Octopus => new OctopusSource { CredentialVariable = "OCTOPUS_API_KEY" },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        source.Name = DisplayName(kind);
        return source;
    }
}
