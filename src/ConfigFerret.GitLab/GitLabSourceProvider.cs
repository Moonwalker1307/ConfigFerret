using ConfigFerret.Shared;
using ConfigFerret.Sources;

namespace ConfigFerret.GitLab;

/// <summary>Describes the GitLab CI/CD variables source to hosts such as the desktop app and the command-line tool.</summary>
public sealed class GitLabSourceProvider : IConfigSourceProvider
{
    private const string DefaultBaseAddress = "https://gitlab.com/";

    /// <inheritdoc/>
    public string Id => "gitlab-ci-variables";

    /// <inheritdoc/>
    public string DisplayName => "GitLab CI/CD variables";

    /// <inheritdoc/>
    public SourceCategory Category => SourceCategory.CiCd;

    /// <inheritdoc/>
    public IReadOnlyList<SourceSetting> Settings { get; } =
    [
        new(nameof(GitLabOptions.BaseAddress), "GitLab address", SourceSettingKind.Url, Required: true, Help: "The GitLab address, or your self-managed instance's.", DefaultValue: DefaultBaseAddress),
        new(nameof(GitLabOptions.Project), "Project", Required: true, Help: "The project id or path, for example contoso/api."),
        new(nameof(GitLabOptions.Token), "Access token", SourceSettingKind.Secret, Required: true, Help: "A personal, group or project access token with read_api, from a Maintainer. CI_JOB_TOKEN cannot read variables.", EnvironmentVariables: ["GITLAB_TOKEN"]),
        new(nameof(GitLabTraceOptions.Environment), "Environment", Help: "The environment the deployment runs in, to pick environment-scoped variables and leave out CI file jobs for other environments. Leave empty for a job with no environment."),
        new(nameof(GitLabOptions.IncludeGroups), "Include group variables", SourceSettingKind.Boolean, Help: "Also read the variables of the project's groups.", DefaultValue: "true"),
        new(nameof(GitLabOptions.IncludeInstance), "Include instance variables", SourceSettingKind.Boolean, Help: "Also read instance variables. Needs an administrator's token.", DefaultValue: "false"),
        new(nameof(GitLabOptions.IncludeMaskedValues), "Read masked values", SourceSettingKind.Boolean, Help: "Keep masked values to compare them. They are always redacted.", DefaultValue: "false"),
        new(nameof(GitLabTraceOptions.CiFiles), "CI files", SourceSettingKind.Lines, Help: "CI files such as .gitlab-ci.yml to read for variables and $VAR links, one per line."),
        new(nameof(GitLabTraceOptions.MatchByName), "Match by name", SourceSettingKind.Boolean, Help: "Link environment variables and Kubernetes keys to GitLab variables with the same key.", DefaultValue: "true"),
        new(nameof(GitLabOptions.AllowUnencryptedHttp), "Allow plain http", SourceSettingKind.Boolean, Help: "Send the token over http to an address that is not loopback.", DefaultValue: "false"),
    ];

    /// <inheritdoc/>
    public IConfigSource Create(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var options = new GitLabOptions
        {
            BaseAddress = SettingValues.Url(values, nameof(GitLabOptions.BaseAddress), DefaultBaseAddress),
            Project = SettingValues.Required(values, nameof(GitLabOptions.Project)),
            Token = SettingValues.Required(values, nameof(GitLabOptions.Token)),
            IncludeGroups = SettingValues.Boolean(values, nameof(GitLabOptions.IncludeGroups), true),
            IncludeInstance = SettingValues.Boolean(values, nameof(GitLabOptions.IncludeInstance), false),
            IncludeMaskedValues = SettingValues.Boolean(values, nameof(GitLabOptions.IncludeMaskedValues), false),
            AllowUnencryptedHttp = SettingValues.Boolean(values, nameof(GitLabOptions.AllowUnencryptedHttp), false),
        };
        JsonApi.RequireSafeBaseAddress(options.BaseAddress, options.AllowUnencryptedHttp, nameof(GitLabOptions.BaseAddress));
        var trace = new GitLabTraceOptions
        {
            Environment = SettingValues.Text(values, nameof(GitLabTraceOptions.Environment)),
            MatchByName = SettingValues.Boolean(values, nameof(GitLabTraceOptions.MatchByName), true),
        };
        foreach (var path in SettingValues.Lines(values, nameof(GitLabTraceOptions.CiFiles)))
        {
            trace.CiFiles.Add(path);
        }

        return GitLabSource.Create(options, trace);
    }
}
