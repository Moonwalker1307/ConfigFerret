namespace ConfigFerret.AzureDevOps;

/// <summary>What backs an Azure Pipelines variable group.</summary>
public enum AzureDevOpsGroupType
{
    /// <summary>A plain variable group (<c>Vsts</c>): its variables are stored in Azure DevOps.</summary>
    Vsts,

    /// <summary>A group linked to an Azure Key Vault: each variable is a secret read from the vault when a pipeline runs.</summary>
    AzureKeyVault,

    /// <summary>Any other type a server reports.</summary>
    Other,
}

/// <summary>One Azure Pipelines variable group.</summary>
/// <param name="Id">The group id.</param>
/// <param name="Name">The group name, as used in <c>- group: NAME</c>.</param>
/// <param name="Type">Plain or linked to a key vault.</param>
/// <param name="Description">The description, when there is one.</param>
/// <param name="KeyVaultName">The linked key vault, for a group of type <see cref="AzureDevOpsGroupType.AzureKeyVault"/>.</param>
/// <param name="ModifiedOn">When the group was last changed, when known.</param>
/// <param name="Variables">The group's variables.</param>
public sealed record AzureDevOpsVariableGroup(
    int Id,
    string Name,
    AzureDevOpsGroupType Type,
    string? Description,
    string? KeyVaultName,
    DateTimeOffset? ModifiedOn,
    IReadOnlyList<AzureDevOpsVariable> Variables);

/// <summary>One variable in an Azure Pipelines variable group.</summary>
/// <param name="Name">The name, for example <c>Db.Timeout</c>. Azure Pipelines compares names case-insensitively.</param>
/// <param name="Value">The value. Always null for a secret, because Azure DevOps never returns secret values.</param>
/// <param name="IsSecret">True for a secret variable.</param>
/// <param name="Group">The group name.</param>
/// <param name="GroupId">The group id.</param>
/// <param name="GroupType">Plain or linked to a key vault.</param>
/// <param name="KeyVaultName">The key vault the value comes from, for a group linked to one.</param>
public sealed record AzureDevOpsVariable(
    string Name,
    string? Value,
    bool IsSecret,
    string Group,
    int GroupId,
    AzureDevOpsGroupType GroupType = AzureDevOpsGroupType.Vsts,
    string? KeyVaultName = null)
{
    /// <summary>
    /// True when Azure DevOps treats the value as secret: a secret variable, or any variable of a group linked to a key
    /// vault. A trace also redacts a variable whose name looks like a secret's.
    /// </summary>
    public bool IsSensitive => IsSecret || GroupType == AzureDevOpsGroupType.AzureKeyVault;

    /// <summary>
    /// The environment variable name Azure Pipelines gives the variable in scripts: upper case, with <c>.</c> and spaces
    /// turned into <c>_</c>, so <c>Db.Timeout</c> becomes <c>DB_TIMEOUT</c>. Secret variables reach scripts only when
    /// mapped in an <c>env:</c> block.
    /// </summary>
    public string EnvironmentName => AzureDevOpsVariableGroups.ToEnvironmentName(Name);
}

/// <summary>A pointer to a variable in an Azure Pipelines variable group.</summary>
/// <param name="Project">The project, for example <c>contoso/api</c> (organization and project).</param>
/// <param name="Group">The group name.</param>
/// <param name="Name">The variable name.</param>
public sealed record AzureDevOpsVariableRef(string Project, string Group, string Name) : SourceRef;

/// <summary>A pointer to a variable set in pipeline YAML (<c>variables:</c>).</summary>
/// <param name="File">The pipeline file's name in the report, as <see cref="AzureDevOpsTraceOptions.AddPipeline"/> gives it or as passed to <see cref="AzureDevOpsTraceOptions.AddPipelineText"/>.</param>
/// <param name="Line">The 1-based line of the variable.</param>
/// <param name="Name">The variable name.</param>
public sealed record AzurePipelinesVariableRef(string File, int Line, string Name) : SourceRef;
