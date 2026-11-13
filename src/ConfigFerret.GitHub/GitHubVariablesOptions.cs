namespace ConfigFerret.GitHub;

/// <summary>
/// What to read from GitHub, and how to sign in. Can be bound from a configuration section, see
/// <c>services.AddConfigFerretGitHub(...)</c>.
/// </summary>
public sealed class GitHubVariablesOptions
{
    /// <summary>The repository owner (user or organization), for example <c>contoso</c>.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>The repository name, for example <c>api</c>.</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>
    /// The deployment environment whose variables and secrets to read, for example <c>production</c>.
    /// Leave null to read only organization and repository ones.
    /// </summary>
    public string? Environment { get; set; }

    /// <summary>
    /// A token that can read Actions variables: a fine-grained token with "Variables: read" (and "Secrets: read" for
    /// secret names, "Environments: read" for environments), a classic token with <c>repo</c>, or a GitHub Actions
    /// <c>GITHUB_TOKEN</c> with <c>actions: read</c>. Read it from user secrets, an environment variable or a key vault;
    /// never hard-code it or put it in a committed appsettings file. ConfigFerret sends it only in the Authorization header and never writes it to a report or exception.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>Also read organization variables and secrets shared with the repository. Default true.</summary>
    public bool IncludeOrganization { get; set; } = true;

    /// <summary>Also read secret names (values are never available). Default true.</summary>
    public bool IncludeSecrets { get; set; } = true;

    /// <summary>The REST API address. Default <c>https://api.github.com/</c>; for GitHub Enterprise Server use <c>https://HOST/api/v3/</c>.</summary>
    public Uri ApiBaseAddress { get; set; } = new("https://api.github.com/");

    /// <summary>Allows a plain-http <see cref="ApiBaseAddress"/> that is not loopback. Off by default, so the token is not sent in clear text.</summary>
    public bool AllowUnencryptedHttp { get; set; }
}
