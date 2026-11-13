namespace ConfigFerret.GitHub;

/// <summary>Where a GitHub Actions variable or secret is defined. Later members win over earlier ones.</summary>
public enum GitHubVariableScope
{
    /// <summary>An organization variable or secret shared with the repository.</summary>
    Organization,

    /// <summary>A repository variable or secret.</summary>
    Repository,

    /// <summary>A deployment environment variable or secret.</summary>
    Environment,
}

/// <summary>Whether a name refers to a GitHub Actions variable (<c>vars.X</c>) or a secret (<c>secrets.X</c>).</summary>
public enum GitHubVariableKind
{
    /// <summary>A configuration variable, read with <c>${{ vars.NAME }}</c>. Its value can be read through the API.</summary>
    Variable,

    /// <summary>A secret, read with <c>${{ secrets.NAME }}</c>. Only its name can be read through the API, never its value.</summary>
    Secret,
}

/// <summary>One GitHub Actions variable or secret.</summary>
/// <param name="Name">The name, for example <c>DB_TIMEOUT</c>. GitHub compares names case-insensitively.</param>
/// <param name="Value">The value. Always null for a secret, because GitHub never returns secret values.</param>
/// <param name="Kind">Variable or secret.</param>
/// <param name="Scope">Organization, repository or environment.</param>
/// <param name="Owner">Who owns it: the organization name, <c>owner/repo</c>, or the environment name.</param>
/// <param name="UpdatedAt">When it was last changed, when known.</param>
public sealed record GitHubVariable(
    string Name,
    string? Value,
    GitHubVariableKind Kind,
    GitHubVariableScope Scope,
    string Owner,
    DateTimeOffset? UpdatedAt = null);

/// <summary>A pointer to a GitHub Actions variable or secret.</summary>
/// <param name="Kind">Variable or secret.</param>
/// <param name="Scope">Organization, repository or environment.</param>
/// <param name="Owner">The organization, <c>owner/repo</c>, or environment name.</param>
/// <param name="Name">The variable or secret name.</param>
public sealed record GitHubVariableRef(GitHubVariableKind Kind, GitHubVariableScope Scope, string Owner, string Name) : SourceRef;
