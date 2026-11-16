namespace ConfigFerret.Octopus;

/// <summary>One Octopus Deploy variable, from a project or a library variable set.</summary>
/// <param name="Name">The variable name, for example <c>Db:Timeout</c>. Octopus compares names case-insensitively.</param>
/// <param name="Value">The value. Always null for a sensitive variable, because Octopus never returns sensitive values.</param>
/// <param name="IsSensitive">True for a sensitive variable.</param>
/// <param name="Type">The Octopus variable type, for example <c>String</c>, <c>Sensitive</c> or <c>Certificate</c>.</param>
/// <param name="SetName">The project or library variable set that holds the variable.</param>
/// <param name="IsProjectVariable">True for a project variable, false for a library variable set variable.</param>
/// <param name="Scope">
/// The scope, by Octopus scope name (<c>Environment</c>, <c>Machine</c>, <c>Role</c>, <c>Action</c>, <c>Channel</c>,
/// <c>TenantTag</c>, <c>ProcessOwner</c>), as display names, except tenant tags, which are canonical names such as
/// <c>Region/West</c>. Empty when the variable is unscoped.
/// </param>
public sealed record OctopusVariable(
    string Name,
    string? Value,
    bool IsSensitive,
    string Type,
    string SetName,
    bool IsProjectVariable,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Scope)
{
    /// <summary>What the <c>ProcessOwner</c> scope names, as the loader tells it from the process ids.</summary>
    internal ProcessScope Process { get; init; }
}

/// <summary>What the process scope of a variable names.</summary>
internal enum ProcessScope
{
    /// <summary>Processes whose type is not known, as in a snapshot built from entries, or no process.</summary>
    TypeNotGiven,

    /// <summary>The deployment process, maybe with runbooks.</summary>
    Deployment,

    /// <summary>Only runbooks.</summary>
    RunbooksOnly,
}

/// <summary>A pointer to an Octopus Deploy variable.</summary>
/// <param name="SetName">The project or library variable set that holds the variable.</param>
/// <param name="Name">The variable name.</param>
public sealed record OctopusVariableRef(string SetName, string Name) : SourceRef
{
    /// <inheritdoc/>
    protected override string? ValueName() => Name;
}
