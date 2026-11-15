namespace ConfigFerret.GitLab;

/// <summary>
/// What to read from GitLab, and how to sign in. Can be bound from a configuration section, see
/// <c>services.AddConfigFerretGitLab(...)</c>.
/// </summary>
public sealed class GitLabOptions
{
    /// <summary>The GitLab address. Default <c>https://gitlab.com/</c>; for a self-managed instance use its address, for example <c>https://gitlab.example.com/</c>.</summary>
    public Uri BaseAddress { get; set; } = new("https://gitlab.com/");

    /// <summary>The project id (for example <c>42</c>) or path (for example <c>contoso/platform/api</c>).</summary>
    public string Project { get; set; } = string.Empty;

    /// <summary>
    /// A personal, group or project access token with the <c>read_api</c> scope, from a user with the Maintainer role
    /// (GitLab shows CI/CD variables only to maintainers). A job's <c>CI_JOB_TOKEN</c> cannot read variables. Read it from
    /// user secrets, an environment variable (<c>GITLAB_TOKEN</c>) or a key vault; never hard-code it or put it in a
    /// committed appsettings file. ConfigFerret sends it only in the <c>PRIVATE-TOKEN</c> header and never writes it to a
    /// report or exception.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>Also read the variables of the groups the project is in, outer group first. Default true.</summary>
    public bool IncludeGroups { get; set; } = true;

    /// <summary>Also read instance variables. Off by default: only an administrator's token can read them.</summary>
    public bool IncludeInstance { get; set; }

    /// <summary>
    /// Keep the values of masked variables, which the API returns. Off by default, so masked values are not read into
    /// memory. When on, they are still marked sensitive and redacted in every report. Hidden values are never returned.
    /// </summary>
    public bool IncludeMaskedValues { get; set; }

    /// <summary>Allows a plain-http <see cref="BaseAddress"/> that is not loopback. Off by default, so the token is not sent in clear text.</summary>
    public bool AllowUnencryptedHttp { get; set; }
}
