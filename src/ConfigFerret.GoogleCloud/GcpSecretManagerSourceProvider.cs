using ConfigFerret.Shared;
using ConfigFerret.Sources;

namespace ConfigFerret.GoogleCloud;

/// <summary>Describes the Google Cloud Secret Manager source to hosts such as the desktop app and the command-line tool.</summary>
public sealed class GcpSecretManagerSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc/>
    public string Id => "gcp-secret-manager";

    /// <inheritdoc/>
    public string DisplayName => "Google Cloud Secret Manager";

    /// <inheritdoc/>
    public SourceCategory Category => SourceCategory.SecretStore;

    /// <inheritdoc/>
    public IReadOnlyList<SourceSetting> Settings { get; } =
    [
        new(nameof(GcpSecretManagerOptions.ProjectId), "Project", Required: true, Help: "The project id or number that holds the secrets, for example contoso-prod."),
        new(nameof(GcpSecretManagerOptions.Location), "Location", Help: "The location of regional secrets, for example europe-west1. Leave empty for global secrets."),
        new(nameof(GcpSecretManagerOptions.Prefix), "Secret id prefix", Help: "Read only secrets whose id starts with this, for example orders-."),
        new(nameof(GcpSecretManagerOptions.Filter), "Filter", Help: "A Secret Manager list filter, for example labels.app=orders."),
        new(nameof(GcpSecretManagerOptions.CredentialsFile), "Service account key file", SourceSettingKind.Path, Help: "A service account JSON key file. Leave empty to use Application Default Credentials.", EnvironmentVariables: ["GOOGLE_APPLICATION_CREDENTIALS"]),
        new(nameof(GcpSecretManagerOptions.CredentialsJson), "Service account key", SourceSettingKind.Secret, Help: "The content of a service account JSON key, instead of a key file."),
        new(nameof(GcpSecretManagerOptions.AccessToken), "Access token", SourceSettingKind.Secret, Help: "An OAuth access token, for example from gcloud auth print-access-token, instead of a key.", EnvironmentVariables: ["GOOGLE_OAUTH_ACCESS_TOKEN"]),
        new(nameof(GcpSecretManagerOptions.IncludeVersions), "Read version states", SourceSettingKind.Boolean, Help: "Read each secret's versions and whether they are enabled. One request per secret.", DefaultValue: "false"),
        new(nameof(GcpSecretManagerOptions.IncludeValues), "Read secret values", SourceSettingKind.Boolean, Help: "Read the latest enabled version of each secret to link JSON properties and compare values. Values stay redacted.", DefaultValue: "false"),
        new(nameof(GcpSecretManagerOptions.IncludeDisabled), "Keep secrets with no enabled version", SourceSettingKind.Boolean, Help: "Keep secrets whose versions are all disabled or destroyed.", DefaultValue: "false"),
        new(nameof(GcpSecretManagerTraceOptions.MatchByName), "Match by name", SourceSettingKind.Boolean, Help: "Link environment variables and Kubernetes keys to secrets with the same name, with __ for :.", DefaultValue: "true"),
        new(nameof(GcpSecretManagerTraceOptions.StripPrefixes), "Strip prefixes", SourceSettingKind.Lines, Help: "Prefixes to remove from layer names before matching, one per line."),
        new(nameof(GcpSecretManagerTraceOptions.Separator), "Separator", Help: "Another separator in secret ids that stands for :, for example --."),
        new(nameof(GcpSecretManagerOptions.ApiBaseAddress), "API address", SourceSettingKind.Url, Help: "Leave empty for the public endpoint. Set it for a private endpoint."),
    ];

    /// <inheritdoc/>
    public IConfigSource Create(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var options = new GcpSecretManagerOptions
        {
            ProjectId = SettingValues.Required(values, nameof(GcpSecretManagerOptions.ProjectId)),
            Location = SettingValues.Text(values, nameof(GcpSecretManagerOptions.Location)),
            Prefix = SettingValues.Text(values, nameof(GcpSecretManagerOptions.Prefix)),
            Filter = SettingValues.Text(values, nameof(GcpSecretManagerOptions.Filter)),
            CredentialsFile = SettingValues.Text(values, nameof(GcpSecretManagerOptions.CredentialsFile)),
            CredentialsJson = SettingValues.Text(values, nameof(GcpSecretManagerOptions.CredentialsJson)),
            AccessToken = SettingValues.Text(values, nameof(GcpSecretManagerOptions.AccessToken)),
            IncludeVersions = SettingValues.Boolean(values, nameof(GcpSecretManagerOptions.IncludeVersions), false),
            IncludeValues = SettingValues.Boolean(values, nameof(GcpSecretManagerOptions.IncludeValues), false),
            IncludeDisabled = SettingValues.Boolean(values, nameof(GcpSecretManagerOptions.IncludeDisabled), false),
            ApiBaseAddress = SettingValues.Url(values, nameof(GcpSecretManagerOptions.ApiBaseAddress)),
        };
        if (!GcpSecretManagerOptions.IsValidLocation(options.Location))
        {
            throw new ArgumentException("Location must hold only lower-case letters, digits and dashes, for example europe-west1.", nameof(values));
        }

        JsonApi.RequireSafeBaseAddress(options.EffectiveBaseAddress, options.AllowUnencryptedHttp, nameof(GcpSecretManagerOptions.ApiBaseAddress));
        var trace = new GcpSecretManagerTraceOptions
        {
            MatchByName = SettingValues.Boolean(values, nameof(GcpSecretManagerTraceOptions.MatchByName), true),
            Separator = SettingValues.Text(values, nameof(GcpSecretManagerTraceOptions.Separator)),
        };
        foreach (var prefix in SettingValues.Lines(values, nameof(GcpSecretManagerTraceOptions.StripPrefixes)))
        {
            trace.StripPrefixes.Add(prefix);
        }

        return GcpSecretManagerSource.Create(options, trace);
    }
}
