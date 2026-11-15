using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;

namespace ConfigFerret.GoogleCloud;

/// <summary>Gets an access token for Secret Manager from the credential the options name.</summary>
internal static class GcpAuth
{
    public const string Scope = "https://www.googleapis.com/auth/cloud-platform";

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoHeaders = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>
    /// The access token, and any extra headers the credential needs (such as <c>x-goog-user-project</c> for a quota
    /// project). Order: <see cref="GcpSecretManagerOptions.AccessToken"/>, <see cref="GcpSecretManagerOptions.Credential"/>,
    /// <see cref="GcpSecretManagerOptions.CredentialsJson"/>, <see cref="GcpSecretManagerOptions.CredentialsFile"/>, then
    /// Application Default Credentials.
    /// </summary>
    /// <exception cref="InvalidOperationException">No credential was found, or it could not give a token. The message never holds the credential.</exception>
    public static async Task<AccessTokenWithHeaders> GetTokenAsync(GcpSecretManagerOptions options, Uri baseAddress, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.AccessToken))
        {
            return new AccessTokenWithHeaders(options.AccessToken.Trim(), NoHeaders);
        }

        var credential = options.Credential ?? await CreateAsync(options, cancellationToken).ConfigureAwait(false);
        if (credential is GoogleCredential google)
        {
            if (google.IsCreateScopedRequired)
            {
                google = google.CreateScoped(Scope);
            }

            if (options.TokenHttpClientFactory is { } factory)
            {
                google = google.CreateWithHttpClientFactory(factory);
            }

            credential = google;
        }

        try
        {
            var token = credential is ITokenAccessWithHeaders withHeaders
                ? await withHeaders.GetAccessTokenWithHeadersForRequestAsync(baseAddress.AbsoluteUri, cancellationToken).ConfigureAwait(false)
                : new AccessTokenWithHeaders(await credential.GetAccessTokenForRequestAsync(baseAddress.AbsoluteUri, cancellationToken).ConfigureAwait(false), NoHeaders);
            return string.IsNullOrEmpty(token?.AccessToken)
                ? throw new InvalidOperationException("The Google Cloud credential gave no access token.")
                : token;
        }
        catch (Exception ex) when (ex is TokenResponseException or HttpRequestException or IOException)
        {
            // These messages hold the OAuth error code and description, never the token or the key.
            throw new InvalidOperationException($"Could not get a Google Cloud access token: {ex.Message}", ex);
        }
    }

    private static async Task<ICredential> CreateAsync(GcpSecretManagerOptions options, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.CredentialsJson))
        {
            return FromServiceAccountKey(options.CredentialsJson, nameof(options.CredentialsJson));
        }

        if (!string.IsNullOrWhiteSpace(options.CredentialsFile))
        {
            var path = options.CredentialsFile.Trim();
            string json;
            try
            {
                json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new InvalidOperationException($"Could not read the Google Cloud credentials file {path}: {ex.Message}", ex);
            }

            return FromServiceAccountKey(json, $"The Google Cloud credentials file {path}");
        }

        try
        {
            return await GoogleCredential.GetApplicationDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"No Google Cloud credential was found. Set AccessToken, CredentialsJson or CredentialsFile, set GOOGLE_APPLICATION_CREDENTIALS, or run gcloud auth application-default login. {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Reads a service account key. Other credential types are refused, because a credential configuration from an
    /// unknown place can make the library run a command or call any URL.
    /// </summary>
    internal static GoogleCredential FromServiceAccountKey(string json, string what)
    {
        string? type;
        try
        {
            using var doc = JsonDocument.Parse(json);
            type = JsonRead.String(JsonRead.Property(doc.RootElement, "type"));
        }
        catch (JsonException)
        {
            // No inner exception: its message could quote part of the key.
            throw new InvalidOperationException($"{what} is not valid JSON.");
        }

        if (!string.Equals(type, "service_account", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{what} is not a service account key (its type must be service_account). Use Application Default Credentials for other credential types.");
        }

        try
        {
            return CredentialFactory.FromJson<ServiceAccountCredential>(json).ToGoogleCredential();
        }
        // The library throws many types for a bad key; none of their messages may be shown.
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            throw new InvalidOperationException($"{what} is not a valid service account key: check its client_email and private_key.");
        }
    }
}
