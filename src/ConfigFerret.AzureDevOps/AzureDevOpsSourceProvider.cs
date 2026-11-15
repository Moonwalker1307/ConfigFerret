using ConfigFerret.Shared;
using ConfigFerret.Sources;

namespace ConfigFerret.AzureDevOps;

/// <summary>Describes the Azure DevOps variable groups source to hosts such as the desktop app and the command-line tool.</summary>
public sealed class AzureDevOpsSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc/>
    public string Id => "azure-devops-variable-groups";

    /// <inheritdoc/>
    public string DisplayName => "Azure DevOps variable groups";

    /// <inheritdoc/>
    public SourceCategory Category => SourceCategory.CiCd;

    /// <inheritdoc/>
    public IReadOnlyList<SourceSetting> Settings { get; } =
    [
        new(nameof(AzureDevOpsOptions.OrganizationUrl), "Organization URL", SourceSettingKind.Url, Required: true, Help: "The organization URL, for example https://dev.azure.com/contoso/, or an Azure DevOps Server collection URL."),
        new(nameof(AzureDevOpsOptions.Project), "Project", Required: true, Help: "The project name."),
        new(nameof(AzureDevOpsOptions.GroupNames), "Variable groups", SourceSettingKind.Lines, Help: "The variable groups to read, one per line, lowest priority first. Leave empty to read every group."),
        new(nameof(AzureDevOpsOptions.PersonalAccessToken), "Personal access token", SourceSettingKind.Secret, Help: "A token with the Variable Groups (Read) scope, or a pipeline's System.AccessToken.", EnvironmentVariables: ["AZURE_DEVOPS_EXT_PAT", "SYSTEM_ACCESSTOKEN"]),
        new(nameof(AzureDevOpsOptions.AccessToken), "Entra ID access token", SourceSettingKind.Secret, Help: "A Microsoft Entra ID bearer token for Azure DevOps, instead of a personal access token."),
        new(nameof(AzureDevOpsTraceOptions.Pipelines), "Pipeline files", SourceSettingKind.Lines, Help: "Pipeline YAML files to read for variables, groups and env: links, one per line."),
        new(nameof(AzureDevOpsTraceOptions.MatchByName), "Match by name", SourceSettingKind.Boolean, Help: "Link environment variables and Kubernetes keys to variables with the same name, or the name scripts see (Db.Timeout as DB_TIMEOUT).", DefaultValue: "true"),
        new(nameof(AzureDevOpsOptions.AllowUnencryptedHttp), "Allow plain http", SourceSettingKind.Boolean, Help: "Send the token over http to an address that is not loopback.", DefaultValue: "false"),
    ];

    /// <inheritdoc/>
    public IConfigSource Create(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var options = new AzureDevOpsOptions
        {
            OrganizationUrl = SettingValues.Url(values, nameof(AzureDevOpsOptions.OrganizationUrl)),
            Project = SettingValues.Required(values, nameof(AzureDevOpsOptions.Project)),
            PersonalAccessToken = SettingValues.Text(values, nameof(AzureDevOpsOptions.PersonalAccessToken)),
            AccessToken = SettingValues.Text(values, nameof(AzureDevOpsOptions.AccessToken)),
            AllowUnencryptedHttp = SettingValues.Boolean(values, nameof(AzureDevOpsOptions.AllowUnencryptedHttp), false),
        };
        JsonApi.RequireSafeBaseAddress(options.OrganizationUrl, options.AllowUnencryptedHttp, nameof(AzureDevOpsOptions.OrganizationUrl));
        if (options.PersonalAccessToken is null && options.AccessToken is null)
        {
            throw new ArgumentException($"{nameof(AzureDevOpsOptions.PersonalAccessToken)} or {nameof(AzureDevOpsOptions.AccessToken)} is required.", nameof(values));
        }

        if (options.PersonalAccessToken is not null && options.AccessToken is not null)
        {
            throw new ArgumentException($"Set {nameof(AzureDevOpsOptions.PersonalAccessToken)} or {nameof(AzureDevOpsOptions.AccessToken)}, not both.", nameof(values));
        }

        foreach (var group in SettingValues.Lines(values, nameof(AzureDevOpsOptions.GroupNames)))
        {
            options.GroupNames.Add(group);
        }

        var trace = new AzureDevOpsTraceOptions { MatchByName = SettingValues.Boolean(values, nameof(AzureDevOpsTraceOptions.MatchByName), true) };
        foreach (var path in SettingValues.Lines(values, nameof(AzureDevOpsTraceOptions.Pipelines)))
        {
            trace.Pipelines.Add(path);
        }

        return AzureDevOpsSource.Create(options, trace);
    }
}
