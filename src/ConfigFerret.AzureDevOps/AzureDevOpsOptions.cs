namespace ConfigFerret.AzureDevOps;

/// <summary>
/// What to read from Azure DevOps, and how to sign in. Can be bound from a configuration section, see
/// <c>services.AddConfigFerretAzureDevOps(...)</c>.
/// </summary>
public sealed class AzureDevOpsOptions
{
    /// <summary>
    /// The organization URL, for example <c>https://dev.azure.com/contoso/</c>, or a collection URL on Azure DevOps Server,
    /// for example <c>https://tfs.example.com/tfs/DefaultCollection/</c>.
    /// </summary>
    public Uri? OrganizationUrl { get; set; }

    /// <summary>The project name or id, for example <c>api</c>.</summary>
    public string Project { get; set; } = string.Empty;

    /// <summary>
    /// The variable groups to read, by name. A name may hold <c>*</c> wildcards. Leave empty to read every group in the
    /// project. Groups are listed in this order, which matters when several set the same variable.
    /// </summary>
    public IList<string> GroupNames { get; } = [];

    /// <summary>
    /// A personal access token with the "Variable Groups: Read" scope, or a pipeline's <c>System.AccessToken</c>. It is sent
    /// with Basic authentication and an empty user name. Read it from user secrets, an environment variable
    /// (<c>AZURE_DEVOPS_EXT_PAT</c>) or a key vault; never hard-code it or put it in a committed appsettings file.
    /// ConfigFerret sends it only in the Authorization header and never writes it to a report or exception.
    /// </summary>
    public string? PersonalAccessToken { get; set; }

    /// <summary>
    /// A Microsoft Entra ID access token for Azure DevOps (resource <c>499b84ac-1321-427f-aa17-267ca6975798</c>), sent as a
    /// bearer token. Set this or <see cref="PersonalAccessToken"/>, not both.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>Allows a plain-http <see cref="OrganizationUrl"/> that is not loopback. Off by default, so the token is not sent in clear text.</summary>
    public bool AllowUnencryptedHttp { get; set; }
}
