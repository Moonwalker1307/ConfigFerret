namespace ConfigFerret.GitLab;

/// <summary>Where a GitLab CI/CD variable is defined. Later members win over earlier ones.</summary>
public enum GitLabVariableLevel
{
    /// <summary>An instance variable, set by an administrator for every project.</summary>
    Instance,

    /// <summary>A group variable, inherited by every project in the group and its subgroups.</summary>
    Group,

    /// <summary>A project variable.</summary>
    Project,
}

/// <summary>One GitLab CI/CD variable, as set in the project, group or instance settings.</summary>
/// <param name="Key">The key, for example <c>DB_TIMEOUT</c>. GitLab compares keys case-sensitively.</param>
/// <param name="Value">The value. Null for a hidden variable, which GitLab never returns, and for a masked one unless <see cref="GitLabOptions.IncludeMaskedValues"/> is on.</param>
/// <param name="Level">Instance, group or project.</param>
/// <param name="Owner">Who owns it: the group or project path, or the instance host.</param>
/// <param name="EnvironmentScope">The environments it applies to: <c>*</c> for all, a name, or a pattern such as <c>review/*</c>.</param>
/// <param name="IsMasked">True when GitLab masks the value in job logs.</param>
/// <param name="IsHidden">True when the value is hidden: masked, and never shown again after it was saved.</param>
/// <param name="IsProtected">True when only pipelines on protected branches and tags get it.</param>
/// <param name="IsFile">True for a file variable: the job gets the path of a temporary file that holds the value.</param>
/// <param name="Description">The description, when there is one.</param>
public sealed record GitLabVariable(
    string Key,
    string? Value,
    GitLabVariableLevel Level,
    string Owner,
    string EnvironmentScope = "*",
    bool IsMasked = false,
    bool IsHidden = false,
    bool IsProtected = false,
    bool IsFile = false,
    string? Description = null)
{
    /// <summary>
    /// True when GitLab treats the value as secret: a masked or hidden variable. A trace also redacts a file variable and
    /// a variable whose key looks like a secret's.
    /// </summary>
    public bool IsSensitive => IsMasked || IsHidden;
}

/// <summary>A pointer to a GitLab CI/CD variable in the project, group or instance settings.</summary>
/// <param name="Level">Instance, group or project.</param>
/// <param name="Owner">The group or project path, or the instance host.</param>
/// <param name="Key">The variable key.</param>
/// <param name="EnvironmentScope">The environment scope.</param>
public sealed record GitLabVariableRef(GitLabVariableLevel Level, string Owner, string Key, string EnvironmentScope) : SourceRef;

/// <summary>A pointer to a variable set in <c>.gitlab-ci.yml</c> (<c>variables:</c>).</summary>
/// <param name="File">The file's name in the report, as <see cref="GitLabTraceOptions.AddCiFile"/> gives it or as passed to <see cref="GitLabTraceOptions.AddCiFileText"/>.</param>
/// <param name="Line">The 1-based line of the variable.</param>
/// <param name="Name">The variable name.</param>
public sealed record GitLabCiVariableRef(string File, int Line, string Name) : SourceRef;
