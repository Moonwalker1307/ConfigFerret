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

    /// <summary>Allows a plain-http <see cref="ServerUri"/> that is not loopback. Off by default, so the API key is not sent in clear text.</summary>
    public bool AllowUnencryptedHttp { get; set; }
}
