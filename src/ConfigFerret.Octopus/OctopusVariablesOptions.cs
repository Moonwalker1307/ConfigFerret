namespace ConfigFerret.Octopus;

/// <summary>
/// What to read from Octopus Deploy, and how to sign in. Can be bound from a configuration section, see
/// <c>services.AddConfigFerretOctopus(...)</c>.
/// </summary>
public sealed class OctopusVariablesOptions
{
    /// <summary>The Octopus server address, for example <c>https://contoso.octopus.app/</c>.</summary>
    public Uri? ServerUri { get; set; }

    /// <summary>
    /// An Octopus API key (<c>API-...</c>) for a user or service account that can view the project's variables.
    /// Read it from user secrets, an environment variable or a key vault; never hard-code it or put it in a committed
    /// appsettings file. ConfigFerret sends it only in the
    /// <c>X-Octopus-ApiKey</c> header and never writes it to a report or exception.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The project, by name, slug or id (<c>Projects-1</c>).</summary>
    public string Project { get; set; } = string.Empty;

    /// <summary>The space, by name or id (<c>Spaces-1</c>). Leave null for the default space.</summary>
    public string? Space { get; set; }

    /// <summary>Also read the library variable sets the project includes. Default true.</summary>
    public bool IncludeLibraryVariableSets { get; set; } = true;

    /// <summary>
    /// Also read the deployment process, to see which steps replace values in which files (structured configuration
    /// variables, substitute variables in templates) and which run .NET configuration variables or transforms. The API
    /// key then also needs permission to view the process (<c>ProcessView</c>); without it the trace warns and links
    /// by name only. Off by default. Setting <see cref="Release"/> reads the process too.
    /// </summary>
    public bool ReadDeploymentProcess { get; set; }

    /// <summary>
    /// A release version, for example <c>1.4.0</c>. When set, the variables and the deployment process are read from the
    /// snapshot that release was created with, which is what its deployments use, instead of their current versions.
    /// </summary>
    public string? Release { get; set; }

    /// <summary>
    /// For a version-controlled (config as code) project, the branch, tag or Git reference to read the deployment
    /// process from, for example <c>main</c> or <c>refs/tags/v1.4.0</c>. Leave null for the project's default branch.
    /// Ignored when <see cref="Release"/> is set, since the release has its own snapshot.
    /// </summary>
    public string? GitRef { get; set; }

    /// <summary>Allows a plain-http <see cref="ServerUri"/> that is not loopback. Off by default, so the API key is not sent in clear text.</summary>
    public bool AllowUnencryptedHttp { get; set; }
}
