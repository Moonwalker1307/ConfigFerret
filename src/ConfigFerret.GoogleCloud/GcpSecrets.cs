using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ConfigFerret.Shared;
using Google.Apis.Auth.OAuth2;

namespace ConfigFerret.GoogleCloud;

/// <summary>
/// A snapshot of the Secret Manager secrets in one project (and, for regional secrets, one location). Load it with
/// <see cref="LoadAsync(GcpSecretManagerOptions, CancellationToken)"/>, or build it from secrets you already have, then
/// pass it to <see cref="GoogleCloudTracerExtensions.WithGcpSecretManager"/>.
/// </summary>
public sealed class GcpSecrets
{
    private const int SecretsPageSize = 250;
    private const int VersionsPageSize = 25;

    // Stop after this many pages so a misbehaving server cannot keep a trace looping.
    private const int MaxPages = 1000;

    private readonly Dictionary<string, GcpSecret> _byId = new(StringComparer.Ordinal);

    /// <summary>Creates a snapshot from secrets you already have, for example from <c>gcloud secrets list --format=json</c>.</summary>
    /// <param name="projectId">The project the secrets were read from, as configured.</param>
    /// <param name="secrets">The secrets.</param>
    /// <param name="location">The location of regional secrets, or null.</param>
    /// <param name="prefix">The secret id prefix the secrets were filtered by, or null.</param>
    /// <param name="warnings">Problems found while reading.</param>
    public GcpSecrets(string projectId, IEnumerable<GcpSecret> secrets, string? location = null, string? prefix = null, IEnumerable<string>? warnings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(secrets);
        ProjectId = projectId.Trim();
        Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
        Prefix = string.IsNullOrEmpty(prefix) ? null : prefix;
        // The first one wins, so a server that lists a secret twice cannot make it appear twice.
        Secrets = [.. secrets.Where(secret => _byId.TryAdd(secret.Id, secret))];
        Warnings = [.. warnings ?? []];
    }

    /// <summary>The project, as configured.</summary>
    public string ProjectId { get; }

    /// <summary>The location of regional secrets, or null for global ones.</summary>
    public string? Location { get; }

    /// <summary>The secret id prefix the secrets were filtered by, or null.</summary>
    public string? Prefix { get; }

    /// <summary>Every secret, in the order the API listed them.</summary>
    public IReadOnlyList<GcpSecret> Secrets { get; }

    /// <summary>Problems found while loading, such as versions the credential could not read.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>A short description of where the secrets are, for example <c>project contoso-prod</c>.</summary>
    public string Where => Location is null ? $"project {ProjectId}" : $"project {ProjectId}, location {Location}";

    /// <summary>The secret with id <paramref name="secretId"/> (ids are case-sensitive), or null.</summary>
    public GcpSecret? Find(string secretId)
    {
        ArgumentNullException.ThrowIfNull(secretId);
        return _byId.GetValueOrDefault(secretId);
    }

    /// <summary>
    /// Reads the secrets from the Secret Manager REST API.
    /// </summary>
    /// <exception cref="HttpRequestException">The API refused the list or sent something unexpected.</exception>
    /// <exception cref="InvalidOperationException">No credential was found, or it could not give a token.</exception>
    public static async Task<GcpSecrets> LoadAsync(GcpSecretManagerOptions options, CancellationToken cancellationToken = default)
    {
        using var client = JsonApi.CreateClient();
        return await LoadAsync(options, client, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the secrets with your own <see cref="HttpClient"/> (for a proxy or a test handler). The client should not
    /// follow redirects, so the token never reaches another host.
    /// </summary>
    /// <exception cref="HttpRequestException">The API refused the list or sent something unexpected.</exception>
    /// <exception cref="InvalidOperationException">No credential was found, or it could not give a token.</exception>
    public static async Task<GcpSecrets> LoadAsync(GcpSecretManagerOptions options, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProjectId);
        if (!GcpSecretManagerOptions.IsValidLocation(options.Location))
        {
            throw new ArgumentException("Location must hold only lower-case letters, digits and dashes, for example europe-west1.", nameof(options));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxSecrets, 1, nameof(options));
        var baseAddress = JsonApi.RequireSafeBaseAddress(options.EffectiveBaseAddress, options.AllowUnencryptedHttp, nameof(options.ApiBaseAddress));
        var token = await GcpAuth.GetTokenAsync(options, baseAddress, cancellationToken).ConfigureAwait(false);

        var loader = new Loader(httpClient, baseAddress, token);
        var warnings = new List<string>();
        var location = string.IsNullOrWhiteSpace(options.Location) ? null : options.Location.Trim();
        var listed = await loader.ListSecretsAsync(options, location, warnings, cancellationToken).ConfigureAwait(false);

        var secrets = new List<GcpSecret>();
        var skipped = 0;
        for (var i = 0; i < listed.Count; i++)
        {
            var secret = listed[i];
            if (options.IncludeVersions || options.IncludeValues)
            {
                secret = secret with { Versions = await loader.TryReadVersionsAsync(secret, warnings, cancellationToken).ConfigureAwait(false) };
                if (!options.IncludeDisabled && secret.Versions is not null && secret.LatestEnabledVersion is null)
                {
                    skipped++;
                    continue;
                }
            }

            if (options.IncludeValues && secret.LatestEnabledVersion is { } version)
            {
                secret = await loader.TryReadValueAsync(secret, version, warnings, cancellationToken).ConfigureAwait(false);
            }

            secrets.Add(secret);
        }

        if (skipped > 0)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Skipped {skipped} Google Cloud secret(s) with no enabled version. Turn on IncludeDisabled to keep them."));
        }

        return new GcpSecrets(options.ProjectId, secrets, location, options.Prefix, warnings);
    }

    private static string Escape(string segment) => Uri.EscapeDataString(segment);

    // projects/P/secrets/S or projects/P/locations/L/secrets/S.
    internal static GcpSecret? ParseSecretName(string name)
    {
        var parts = name.Split('/');
        if (!parts.All(IsPlainSegment))
        {
            return null;
        }

        return parts switch
        {
            ["projects", { Length: > 0 } p, "secrets", { Length: > 0 } s] => new GcpSecret { Project = p, Id = s },
            ["projects", { Length: > 0 } p, "locations", { Length: > 0 } l, "secrets", { Length: > 0 } s] => new GcpSecret { Project = p, Location = l, Id = s },
            _ => null,
        };
    }

    // A path segment that cannot change the request path once escaped: not empty, not . or .., no slash.
    private static bool IsPlainSegment(string segment) => segment.Length > 0 && segment != "." && segment != ".." && !segment.Contains('/', StringComparison.Ordinal);

    // Secret Manager's own rule for a secret id: 1 to 255 letters, digits, underscores and hyphens.
    private static bool IsSecretId(string id) => id.Length is > 0 and <= 255 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    // A version id is a number: digits only, and at most 19 of them, the length of the largest long.
    private static bool IsVersionId(string id) => id.Length is > 0 and <= 19 && id.All(char.IsAsciiDigit);

    private static string SecretPath(GcpSecret secret) => secret.Location is null
        ? $"projects/{Escape(secret.Project)}/secrets/{Escape(secret.Id)}"
        : $"projects/{Escape(secret.Project)}/locations/{Escape(secret.Location)}/secrets/{Escape(secret.Id)}";

    private sealed class Loader(HttpClient client, Uri baseAddress, AccessTokenWithHeaders token)
    {
        public async Task<List<GcpSecret>> ListSecretsAsync(GcpSecretManagerOptions options, string? location, List<string> warnings, CancellationToken cancellationToken)
        {
            var parent = location is null
                ? $"projects/{Escape(options.ProjectId.Trim())}"
                : $"projects/{Escape(options.ProjectId.Trim())}/locations/{Escape(location)}";
            var filter = string.IsNullOrWhiteSpace(options.Filter) ? string.Empty : "&filter=" + Escape(options.Filter.Trim());
            var secrets = new List<GcpSecret>();
            var seenTokens = new HashSet<string>(StringComparer.Ordinal);
            string? pageToken = null;
            for (var page = 0; page < MaxPages; page++)
            {
                var query = string.Create(CultureInfo.InvariantCulture, $"{parent}/secrets?pageSize={SecretsPageSize}{filter}");
                if (pageToken is not null)
                {
                    query += "&pageToken=" + Escape(pageToken);
                }

                using var doc = await GetAsync(query, cancellationToken).ConfigureAwait(false);
                var root = doc.RootElement;
                var items = JsonRead.Property(root, "secrets");
                if (root.ValueKind != JsonValueKind.Object || (items is { } i && i.ValueKind != JsonValueKind.Array))
                {
                    throw new HttpRequestException($"Google Cloud Secret Manager returned an unexpected response for {parent}/secrets: no 'secrets' array.");
                }

                var refused = 0;
                foreach (var item in JsonRead.Items(items))
                {
                    if (ReadSecret(item, ref refused) is not { } secret
                        || (!string.IsNullOrEmpty(options.Prefix) && !secret.Id.StartsWith(options.Prefix, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (secrets.Count >= options.MaxSecrets)
                    {
                        warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Stopped after {options.MaxSecrets} Google Cloud secrets (MaxSecrets); the rest were not read."));
                        return secrets;
                    }

                    secrets.Add(secret);
                }

                if (refused > 0)
                {
                    // Never print what a refused id was called: it came from the server.
                    warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Left out {refused} Google Cloud secret(s) whose id is not a valid Secret Manager secret id."));
                }

                pageToken = JsonRead.String(JsonRead.Property(root, "nextPageToken"));
                if (string.IsNullOrEmpty(pageToken))
                {
                    return secrets;
                }

                if (!seenTokens.Add(pageToken))
                {
                    warnings.Add("Google Cloud Secret Manager sent the same page token twice, so listing stopped there.");
                    return secrets;
                }
            }

            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Stopped listing Google Cloud secrets after {MaxPages} pages."));
            return secrets;
        }

        public async Task<IReadOnlyList<GcpSecretVersion>?> TryReadVersionsAsync(GcpSecret secret, List<string> warnings, CancellationToken cancellationToken)
        {
            var path = SecretPath(secret) + "/versions";
            try
            {
                var versions = new List<GcpSecretVersion>();
                var (more, refused) = await ReadVersionPageAsync(string.Create(CultureInfo.InvariantCulture, $"{path}?pageSize={VersionsPageSize}"), versions, cancellationToken).ConfigureAwait(false);
                if (more && !versions.Any(v => v.State == GcpSecretVersionState.Enabled))
                {
                    // The newest enabled version is further back: ask for it alone rather than read every page.
                    refused += (await ReadVersionPageAsync($"{path}?pageSize=1&filter={Escape("state:ENABLED")}", versions, cancellationToken).ConfigureAwait(false)).Refused;
                }

                if (refused > 0)
                {
                    // Never print what a refused id was called: it came from the server.
                    warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Left out {refused} version(s) of Google Cloud secret {secret.Id} whose id is not a number."));
                }

                return versions
                    .DistinctBy(v => v.Id, StringComparer.Ordinal)
                    .OrderByDescending(v => GcpVersionOrder.Key(v.Id))
                    .ToList();
            }
            catch (HttpRequestException ex)
            {
                warnings.Add($"Could not read the versions of Google Cloud secret {secret.Id}: {ex.Message}");
                return null;
            }
        }

        public async Task<GcpSecret> TryReadValueAsync(GcpSecret secret, string version, List<string> warnings, CancellationToken cancellationToken)
        {
            try
            {
                using var doc = await GetAsync($"{SecretPath(secret)}/versions/{Escape(version)}:access", cancellationToken).ConfigureAwait(false);
                var data = JsonRead.String(JsonRead.Property(JsonRead.Object(doc.RootElement, "payload") ?? default, "data"));
                if (data is null)
                {
                    // An empty payload is sent without a data field.
                    return JsonRead.Object(doc.RootElement, "payload") is null
                        ? Unread(secret, version, warnings, "the response has no payload")
                        : secret with { ValueVersion = version, Value = string.Empty };
                }

                var bytes = new byte[data.Length];
                if (!Convert.TryFromBase64String(data, bytes, out var length))
                {
                    return Unread(secret, version, warnings, "the payload is not base64");
                }

                try
                {
                    return secret with { ValueVersion = version, Value = Strict.GetString(bytes, 0, length) };
                }
                catch (DecoderFallbackException)
                {
                    return secret with { ValueVersion = version, IsBinary = true };
                }
            }
            catch (HttpRequestException ex)
            {
                warnings.Add($"Could not read the value of Google Cloud secret {secret.Id} version {version}: {ex.Message}");
                return secret;
            }
        }

        private static GcpSecret Unread(GcpSecret secret, string version, List<string> warnings, string why)
        {
            warnings.Add($"Could not read the value of Google Cloud secret {secret.Id} version {version}: {why}.");
            return secret;
        }

        // Returns whether there are more pages, and how many versions were left out for an id that is not a number.
        private async Task<(bool More, int Refused)> ReadVersionPageAsync(string query, List<GcpSecretVersion> into, CancellationToken cancellationToken)
        {
            using var doc = await GetAsync(query, cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            var items = JsonRead.Property(root, "versions");
            if (root.ValueKind != JsonValueKind.Object || (items is { } i && i.ValueKind != JsonValueKind.Array))
            {
                throw new HttpRequestException("Google Cloud Secret Manager returned an unexpected response: no 'versions' array.");
            }

            var refused = 0;
            foreach (var item in JsonRead.Items(items))
            {
                var name = JsonRead.String(JsonRead.Property(item, "name"));
                var slash = name?.LastIndexOf("/versions/", StringComparison.Ordinal) ?? -1;
                var id = slash < 0 ? null : name![(slash + "/versions/".Length)..];
                if (id is null || !IsPlainSegment(id))
                {
                    continue;
                }

                if (!IsVersionId(id))
                {
                    refused++;
                    continue;
                }

                var state = JsonRead.String(JsonRead.Property(item, "state")) switch
                {
                    "ENABLED" => GcpSecretVersionState.Enabled,
                    "DISABLED" => GcpSecretVersionState.Disabled,
                    "DESTROYED" => GcpSecretVersionState.Destroyed,
                    _ => GcpSecretVersionState.Unknown,
                };
                into.Add(new GcpSecretVersion(id, state, JsonRead.Date(JsonRead.Property(item, "createTime"))));
            }

            return (!string.IsNullOrEmpty(JsonRead.String(JsonRead.Property(root, "nextPageToken"))), refused);
        }

        private static GcpSecret? ReadSecret(JsonElement item, ref int refused)
        {
            if (JsonRead.String(JsonRead.Property(item, "name")) is not { } name || ParseSecretName(name) is not { } secret)
            {
                return null;
            }

            if (!IsSecretId(secret.Id))
            {
                refused++;
                return null;
            }

            return secret with
            {
                CreateTime = JsonRead.Date(JsonRead.Property(item, "createTime")),
                Labels = StringMap(JsonRead.Object(item, "labels")),
                Annotations = StringMap(JsonRead.Object(item, "annotations")),
                Replication = ReadReplication(JsonRead.Object(item, "replication")),
            };
        }

        private static Dictionary<string, string> StringMap(JsonElement? element)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in element is { } e ? JsonRead.Properties(e) : [])
            {
                if (JsonRead.String(value) is { } text)
                {
                    map.TryAdd(key, text);
                }
            }

            return map;
        }

        private static string? ReadReplication(JsonElement? replication)
        {
            if (replication is not { } r)
            {
                return null;
            }

            if (JsonRead.Object(r, "automatic") is not null)
            {
                return "automatic";
            }

            if (JsonRead.Object(r, "userManaged") is { } userManaged)
            {
                var locations = JsonRead.Items(JsonRead.Array(userManaged, "replicas"))
                    .Select(replica => JsonRead.String(JsonRead.Property(replica, "location")))
                    .OfType<string>()
                    .Where(l => l.Length > 0)
                    .ToList();
                return locations.Count == 0 ? "user-managed" : $"user-managed ({string.Join(", ", locations)})";
            }

            return null;
        }

        private Task<JsonDocument> GetAsync(string relative, CancellationToken cancellationToken) =>
            JsonApi.GetAsync(client, new Uri(baseAddress, relative), AddHeaders, "Google Cloud Secret Manager", null, token.AccessToken, cancellationToken);

        private void AddHeaders(HttpRequestHeaders headers)
        {
            headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            foreach (var (name, values) in token.Headers)
            {
                if (!string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    headers.TryAddWithoutValidation(name, values);
                }
            }
        }
    }

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}
