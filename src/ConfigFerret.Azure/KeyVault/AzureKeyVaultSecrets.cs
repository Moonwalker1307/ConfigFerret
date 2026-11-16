using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using ConfigFerret.Sources;

namespace ConfigFerret.Azure;

/// <summary>
/// A snapshot of the secrets in one Azure Key Vault: names and properties, and values only when asked for. Load it with
/// <see cref="LoadAsync(AzureKeyVaultOptions, CancellationToken)"/>, or build it from secrets you already have, then pass
/// it to <see cref="AzureTracerExtensions.WithAzureKeyVault"/>.
/// </summary>
public sealed class AzureKeyVaultSecrets
{
    // Stop after this many pages (25 secrets each) so a misbehaving server cannot keep a trace looping.
    internal const int MaxPages = 400;

    private readonly Dictionary<string, AzureKeyVaultSecret> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<AzureKeyVaultSecret>> _byKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a snapshot from secrets you already have, for example from <c>az keyvault secret list</c>.</summary>
    /// <param name="vault">The vault name, for example <c>contoso-prod</c>.</param>
    /// <param name="secrets">The secrets. A later secret with the same name replaces an earlier one.</param>
    /// <param name="warnings">Problems found while reading them.</param>
    public AzureKeyVaultSecrets(string vault, IEnumerable<AzureKeyVaultSecret> secrets, IEnumerable<string>? warnings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vault);
        ArgumentNullException.ThrowIfNull(secrets);
        Vault = vault;
        foreach (var secret in secrets)
        {
            ArgumentNullException.ThrowIfNull(secret, nameof(secrets));
            _byName[secret.Name] = secret;
        }

        Secrets = [.. _byName.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
        foreach (var secret in Secrets)
        {
            if (!_byKey.TryGetValue(secret.ConfigurationKey, out var list))
            {
                _byKey[secret.ConfigurationKey] = list = [];
            }

            list.Add(secret);
        }

        Warnings = [.. warnings ?? []];
    }

    /// <summary>The vault name, for example <c>contoso-prod</c>.</summary>
    public string Vault { get; }

    /// <summary>Every secret, ordered by name.</summary>
    public IReadOnlyList<AzureKeyVaultSecret> Secrets { get; }

    /// <summary>Problems found while loading, such as values the credential could not read.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The secret named <paramref name="name"/> (case-insensitive), or null.</summary>
    public AzureKeyVaultSecret? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.GetValueOrDefault(name);
    }

    /// <summary>
    /// The secrets the Key Vault configuration provider maps to <paramref name="key"/> (case-insensitive): <c>Db:Password</c>
    /// finds <c>Db--Password</c>.
    /// </summary>
    public IReadOnlyList<AzureKeyVaultSecret> FindByConfigurationKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _byKey.TryGetValue(key, out var list) ? list : [];
    }

    /// <summary>Wraps the snapshot for <see cref="ConfigSourceTracerExtensions.WithSource"/>, so unused secrets can be listed.</summary>
    public LoadedConfigSource ToLoadedSource(AzureKeyVaultTraceOptions? traceOptions = null) =>
        new(
            $"Azure Key Vault {Vault}",
            new AzureKeyVaultResolver(this, traceOptions ?? new AzureKeyVaultTraceOptions()),
            Secrets.Select(s => new SourceEntry(s.Name, new AzureKeyVaultSecretRef(s.Vault, s.Name), IsSensitive: true, Detail(s), s.Vault)),
            Warnings);

    /// <summary>Reads the secrets, signing in with <c>DefaultAzureCredential</c> or the service principal in <paramref name="options"/>.</summary>
    /// <exception cref="RequestFailedException">Key Vault could not be reached, refused the request or sent a response that could not be read.</exception>
    /// <exception cref="global::Azure.Identity.AuthenticationFailedException">Signing in failed.</exception>
    public static Task<AzureKeyVaultSecrets> LoadAsync(AzureKeyVaultOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return LoadAsync(options, AzureAuth.Create(options.TenantId, options.ClientId, options.ClientSecret), new SecretClientOptions(), cancellationToken);
    }

    /// <summary>Reads the secrets, signing in with <paramref name="credential"/>.</summary>
    /// <exception cref="RequestFailedException">Key Vault could not be reached, refused the request or sent a response that could not be read.</exception>
    /// <exception cref="global::Azure.Identity.AuthenticationFailedException">Signing in failed.</exception>
    public static Task<AzureKeyVaultSecrets> LoadAsync(AzureKeyVaultOptions options, TokenCredential credential, CancellationToken cancellationToken = default) =>
        LoadAsync(options, credential, new SecretClientOptions(), cancellationToken);

    /// <summary>
    /// Reads the secrets with your own client options, for example a proxy, retry settings, or a
    /// <c>Transport</c> (<c>new HttpClientTransport(httpClient)</c>) that answers from a test handler.
    /// </summary>
    /// <exception cref="RequestFailedException">Key Vault could not be reached, refused the request or sent a response that could not be read.</exception>
    /// <exception cref="global::Azure.Identity.AuthenticationFailedException">Signing in failed.</exception>
    public static async Task<AzureKeyVaultSecrets> LoadAsync(AzureKeyVaultOptions options, TokenCredential credential, SecretClientOptions clientOptions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(clientOptions);
        var vaultUri = RequireVaultUri(options.VaultUri);
        var vault = AzureAuth.ShortName(vaultUri);
        var client = new SecretClient(vaultUri, credential, clientOptions);
        var filter = new NameFilter(options.SecretNames);
        var secrets = new List<AzureKeyVaultSecret>();
        var warnings = new List<string>();

        try
        {
            var pages = 0;
            await foreach (var page in client.GetPropertiesOfSecretsAsync(cancellationToken).AsPages().ConfigureAwait(false))
            {
                foreach (var properties in page.Values)
                {
                    if (properties?.Name is not { Length: > 0 } name || !filter.Matches(name))
                    {
                        continue;
                    }

                    var enabled = properties.Enabled != false;
                    if (enabled || options.IncludeDisabled)
                    {
                        secrets.Add(FromProperties(properties, vault));
                    }
                }

                if (++pages >= MaxPages && page.ContinuationToken is not null)
                {
                    warnings.Add($"Stopped listing Azure Key Vault {vault} after {MaxPages} pages; some secrets were not read.");
                    break;
                }
            }

            // Inside the try, so that a vault that stops answering ends the load at the first value instead of running
            // the retries again for every secret.
            if (options.IncludeValues)
            {
                for (var i = 0; i < secrets.Count; i++)
                {
                    if (secrets[i].Enabled)
                    {
                        secrets[i] = await ReadValueAsync(client, secrets[i], warnings, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            throw Unreadable(vault, ex);
        }
        catch (AggregateException ex) when (IsUnreachable(ex, cancellationToken))
        {
            throw Unreachable($"Azure Key Vault {vault}", ex);
        }
        catch (AggregateException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw Cancelled(ex, cancellationToken);
        }

        return new AzureKeyVaultSecrets(vault, secrets, warnings);
    }

    internal static Uri RequireVaultUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("VaultUri must be an absolute https URL without credentials, for example https://contoso.vault.azure.net/.", nameof(uri));
        }

        return uri;
    }

    internal static string? Detail(AzureKeyVaultSecret secret)
    {
        var parts = new List<string>();
        if (!secret.Enabled)
        {
            parts.Add("disabled");
        }

        if (secret.ExpiresOn is { } expires)
        {
            parts.Add($"expires {expires:yyyy-MM-dd}");
        }

        if (!string.IsNullOrEmpty(secret.ContentType))
        {
            parts.Add($"content type {secret.ContentType}");
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static AzureKeyVaultSecret FromProperties(SecretProperties properties, string vault) => new(properties.Name, vault, properties.Enabled != false)
    {
        ContentType = properties.ContentType,
        UpdatedOn = properties.UpdatedOn,
        ExpiresOn = properties.ExpiresOn,
        NotBefore = properties.NotBefore,
        Tags = new Dictionary<string, string>(properties.Tags ?? new Dictionary<string, string>(), StringComparer.Ordinal),
        Version = properties.Version,
        Managed = properties.Managed,
    };

    private static async Task<AzureKeyVaultSecret> ReadValueAsync(SecretClient client, AzureKeyVaultSecret secret, List<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetSecretAsync(secret.Name, cancellationToken: cancellationToken).ConfigureAwait(false);
            var read = response.Value;
            return secret with { Value = read?.Value, Version = read?.Properties?.Version ?? secret.Version };
        }
        catch (RequestFailedException ex)
        {
            // Listing needs only "list"; reading a value needs "get", which the credential may not have.
            warnings.Add($"Could not read the value of Azure Key Vault secret {secret.Name} ({secret.Vault}): {AzureAuth.Describe(ex)}.");
            return secret;
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            warnings.Add($"Could not read the value of Azure Key Vault secret {secret.Name} ({secret.Vault}): the response could not be read.");
            return secret;
        }
    }

    // What the SDK throws when a response body is not the JSON it expects (NullReferenceException for a body of
    // "null"), or IndexOutOfRangeException from Azure.Core's status code classifier for a status of 700 and up.
    internal static bool IsUnreadable(Exception ex) =>
        ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or UriFormatException or ArgumentException
            or OverflowException or IndexOutOfRangeException or NullReferenceException;

    private static RequestFailedException Unreadable(string vault, Exception inner) =>
        new($"Azure Key Vault {vault} sent a response that could not be read.", inner);

    // What Azure.Core throws once its retries run out when every try failed in the transport (status 0), for example
    // because the host name is mistyped or there is no network, or timed out because the host never answered. A
    // cancellation is a timeout only while the caller's token is not cancelled, the same rule Azure.Core uses.
    internal static bool IsUnreachable(AggregateException ex, CancellationToken cancellationToken) =>
        ex.InnerExceptions.Count > 0 && ex.InnerExceptions.All(e =>
            e is RequestFailedException { Status: 0 } || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested));

    // Names the service only: the inner messages come from the HTTP stack and are not repeated here.
    internal static RequestFailedException Unreachable(string service, AggregateException inner) =>
        new($"Could not reach {service}.", inner);

    // The caller cancelled after a try had failed, so Azure.Core put the cancellation in an AggregateException.
    internal static OperationCanceledException Cancelled(AggregateException inner, CancellationToken cancellationToken) =>
        new("The operation was canceled.", inner, cancellationToken);

    /// <summary>Matches names exactly or by a prefix ending in <c>*</c>, case-insensitively. Empty matches everything.</summary>
    private sealed class NameFilter(IEnumerable<string> patterns)
    {
        private readonly List<string> _patterns = [.. patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim())];

        public bool Matches(string name) => _patterns.Count == 0 || _patterns.Any(p => p.EndsWith('*')
            ? name.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(name, p, StringComparison.OrdinalIgnoreCase));
    }
}
