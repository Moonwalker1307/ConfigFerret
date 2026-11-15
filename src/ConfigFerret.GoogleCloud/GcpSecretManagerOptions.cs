using Google.Apis.Auth.OAuth2;

namespace ConfigFerret.GoogleCloud;

/// <summary>
/// What to read from Google Cloud Secret Manager, and how to sign in. Can be bound from a configuration section, see
/// <c>services.AddConfigFerretGcpSecretManager(...)</c>. Without <see cref="Credential"/>, <see cref="AccessToken"/>,
/// <see cref="CredentialsJson"/> or <see cref="CredentialsFile"/>, Application Default Credentials are used: the
/// <c>GOOGLE_APPLICATION_CREDENTIALS</c> file, <c>gcloud auth application-default login</c>, or the metadata server on
/// Google Cloud (GKE Workload Identity, Cloud Run, Compute Engine).
/// </summary>
public sealed class GcpSecretManagerOptions
{
    /// <summary>The project id or number that holds the secrets, for example <c>contoso-prod</c>.</summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// The location of regional secrets, for example <c>europe-west1</c>. Leave null for global secrets. Regional secrets
    /// are read from the regional endpoint <c>https://secretmanager.LOCATION.rep.googleapis.com/v1/</c>.
    /// </summary>
    public string? Location { get; set; }

    /// <summary>Read only secrets whose id starts with this, for example <c>orders-</c>. Compared without case.</summary>
    public string? Prefix { get; set; }

    /// <summary>
    /// A Secret Manager list filter sent to the API, for example <c>labels.app=orders</c>. See the Secret Manager
    /// documentation on filtering. Leave null to list every secret.
    /// </summary>
    public string? Filter { get; set; }

    /// <summary>
    /// Also read each secret's versions and their state (enabled, disabled, destroyed). Costs one request per secret.
    /// Always on when <see cref="IncludeValues"/> is on. Default false.
    /// </summary>
    public bool IncludeVersions { get; set; }

    /// <summary>
    /// Also read the value of each secret's latest enabled version, to link JSON properties and say when a value differs.
    /// Needs the <c>secretmanager.versions.access</c> permission. Values are always marked sensitive and redacted in
    /// reports. Default false: only names and metadata are read.
    /// </summary>
    public bool IncludeValues { get; set; }

    /// <summary>
    /// Keep secrets that have no enabled version (every version is disabled or destroyed) when versions are read. Off by
    /// default, because an app cannot read such a secret. Without versions, every secret is kept.
    /// </summary>
    public bool IncludeDisabled { get; set; }

    /// <summary>
    /// An OAuth 2.0 access token with the <c>cloud-platform</c> scope, for example from <c>gcloud auth print-access-token</c>.
    /// It is sent only in the Authorization header and never written to a report or exception. Keep it out of appsettings files.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// The content of a service account JSON key. Keep it out of appsettings files: use user secrets, an environment
    /// variable or a key vault. Only <c>service_account</c> keys are accepted here; use Application Default Credentials
    /// for other credential types.
    /// </summary>
    public string? CredentialsJson { get; set; }

    /// <summary>The path of a service account JSON key file. Only <c>service_account</c> keys are accepted here.</summary>
    public string? CredentialsFile { get; set; }

    /// <summary>A credential you already have, for example from <c>GoogleCredential.GetApplicationDefaultAsync()</c>. Set it from code only.</summary>
    public ICredential? Credential { get; set; }

    /// <summary>
    /// The REST API address. Leave null for <c>https://secretmanager.googleapis.com/v1/</c>, or the regional endpoint
    /// when <see cref="Location"/> is set. Set it for a private endpoint, for example <c>https://secretmanager-mydomain.p.googleapis.com/v1/</c>.
    /// </summary>
    public Uri? ApiBaseAddress { get; set; }

    /// <summary>Allows a plain-http <see cref="ApiBaseAddress"/> that is not loopback. Off by default, so the token is not sent in clear text.</summary>
    public bool AllowUnencryptedHttp { get; set; }

    /// <summary>Stop after this many secrets, with a warning. Default 1000.</summary>
    public int MaxSecrets { get; set; } = 1000;

    /// <summary>Lets tests answer token requests (for a service account key) without a network.</summary>
    internal Google.Apis.Http.IHttpClientFactory? TokenHttpClientFactory { get; set; }

    /// <summary>The API address that <see cref="ApiBaseAddress"/> and <see cref="Location"/> give.</summary>
    internal Uri EffectiveBaseAddress =>
        ApiBaseAddress ?? new Uri(string.IsNullOrWhiteSpace(Location)
            ? "https://secretmanager.googleapis.com/v1/"
            : $"https://secretmanager.{Location.Trim()}.rep.googleapis.com/v1/");

    /// <summary>True for a location that can go into a host name: lower-case letters, digits and dashes.</summary>
    internal static bool IsValidLocation(string? location) =>
        string.IsNullOrWhiteSpace(location) || (location.Trim() is { Length: > 0 and <= 63 } trimmed && trimmed.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'));
}
