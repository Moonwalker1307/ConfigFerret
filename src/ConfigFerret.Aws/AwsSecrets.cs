using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using ConfigFerret.Sources;

namespace ConfigFerret.Aws;

/// <summary>
/// A snapshot of AWS Secrets Manager secrets: names and metadata, and values when asked for. Load it with
/// <see cref="LoadAsync(AwsSecretsManagerOptions, CancellationToken)"/>, or build it from secrets you already have, then
/// pass it to <see cref="AwsTracerExtensions.WithAwsSecretsManager"/>.
/// </summary>
public sealed class AwsSecrets
{
    // ListSecrets returns at most 100 per page, BatchGetSecretValue takes at most 20 ids.
    private const int ListPageSize = 100;
    private const int BatchSize = 20;

    // Stop after this many pages so a misbehaving server cannot keep a trace looping.
    private const int MaxPages = 1000;

    private readonly Dictionary<string, AwsSecret> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AwsSecret> _byArn = new(StringComparer.Ordinal);

    /// <summary>Creates a snapshot from secrets you already have. A second secret with the same name is ignored.</summary>
    /// <param name="secrets">The secrets.</param>
    /// <param name="warnings">Problems found while reading them.</param>
    /// <param name="location">Where they were read from, for example the region.</param>
    /// <param name="namePrefix">The name prefix they were listed with, if any.</param>
    public AwsSecrets(IEnumerable<AwsSecret> secrets, IEnumerable<string>? warnings = null, string? location = null, string? namePrefix = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var entries = new List<AwsSecret>();
        foreach (var secret in secrets)
        {
            ArgumentNullException.ThrowIfNull(secret, nameof(secrets));
            if (_byName.TryAdd(secret.Name, secret))
            {
                entries.Add(secret);
                if (!string.IsNullOrEmpty(secret.Arn))
                {
                    _byArn.TryAdd(secret.Arn, secret);
                }
            }
        }

        Entries = entries;
        Warnings = [.. warnings ?? []];
        Location = location;
        NamePrefix = string.IsNullOrEmpty(namePrefix) ? null : namePrefix;
    }

    /// <summary>Every secret, in the order AWS listed them.</summary>
    public IReadOnlyList<AwsSecret> Entries { get; }

    /// <summary>Problems found while loading, such as values the credential could not read.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Where the secrets were read from, for example <c>eu-west-1</c>.</summary>
    public string? Location { get; }

    /// <summary>The name prefix the secrets were listed with, or null.</summary>
    public string? NamePrefix { get; }

    /// <summary>
    /// Finds a secret by name, full ARN, or ARN without the six random characters AWS appends, the ways External Secrets
    /// Operator and the CSI driver accept. Null when there is none.
    /// </summary>
    public AwsSecret? Find(string nameOrArn)
    {
        ArgumentNullException.ThrowIfNull(nameOrArn);
        if (_byName.TryGetValue(nameOrArn, out var secret) || _byArn.TryGetValue(nameOrArn, out secret))
        {
            return secret;
        }

        if (nameOrArn.StartsWith("arn:", StringComparison.Ordinal))
        {
            // A partial ARN: the full one is this plus "-" and six characters.
            return Entries.FirstOrDefault(e => e.Arn is { } arn
                && arn.Length == nameOrArn.Length + 7
                && arn.StartsWith(nameOrArn, StringComparison.Ordinal)
                && arn[nameOrArn.Length] == '-');
        }

        return null;
    }

    /// <summary>Reads the secrets with the AWS SDK, signed in with the default credential chain or the options' credentials.</summary>
    /// <exception cref="AmazonServiceException">AWS refused the request.</exception>
    /// <exception cref="AmazonClientException">AWS could not be reached, or no region or credentials were found.</exception>
    public static async Task<AwsSecrets> LoadAsync(AwsSecretsManagerOptions options, CancellationToken cancellationToken = default)
    {
        using var client = AwsClients.CreateSecretsManager(options);
        return await LoadAsync(options, client, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the secrets with your own client, for example one with a custom configuration. ConfigFerret checks the responses of the clients it creates before the SDK reads them; your client's responses are not checked. Only <see cref="AwsSecretsManagerOptions.NamePrefix"/>
    /// and <see cref="AwsSecretsManagerOptions.IncludeValues"/> are used; the client decides the region and credentials.
    /// </summary>
    /// <exception cref="AmazonServiceException">AWS refused the request.</exception>
    /// <exception cref="AmazonClientException">AWS could not be reached, or no region or credentials were found.</exception>
    public static async Task<AwsSecrets> LoadAsync(AwsSecretsManagerOptions options, IAmazonSecretsManager client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(client);
        var warnings = new List<string>();
        var listed = await ListAsync(client, options.NamePrefix, warnings, cancellationToken).ConfigureAwait(false);
        var location = AwsClients.Describe(client.Config);
        if (!options.IncludeValues || listed.Count == 0)
        {
            return new AwsSecrets(listed.Select(e => ToSecret(e, null)), warnings, location, options.NamePrefix);
        }

        var values = await ReadValuesAsync(client, listed, warnings, cancellationToken).ConfigureAwait(false);
        return new AwsSecrets(listed.Select(e => ToSecret(e, values.TryGetValue(e.Name!, out var value) ? value : null)), warnings, location, options.NamePrefix);
    }

    /// <summary>Wraps the snapshot for <see cref="IConfigSource"/> hosts.</summary>
    internal LoadedConfigSource ToLoadedSource(string displayName, AwsSecretsTraceOptions traceOptions)
    {
        var entries = new List<SourceEntry>();
        foreach (var secret in Entries)
        {
            var location = secret.Location ?? Location;
            if (secret.Properties is { } properties)
            {
                entries.AddRange(properties.Keys.Select(p => new SourceEntry($"{secret.Name}#{p}", new AwsSecretRef(secret.Name, p), IsSensitive: true, "JSON property", location)));
            }
            else
            {
                entries.Add(new SourceEntry(secret.Name, new AwsSecretRef(secret.Name), IsSensitive: true, secret.Description, location));
            }
        }

        return new LoadedConfigSource(displayName, new AwsSecretsResolver(this, traceOptions), entries, Warnings);
    }

    private static async Task<List<SecretListEntry>> ListAsync(IAmazonSecretsManager client, string? prefix, List<string> warnings, CancellationToken cancellationToken)
    {
        var result = new List<SecretListEntry>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        var request = new ListSecretsRequest { MaxResults = ListPageSize };
        if (!string.IsNullOrEmpty(prefix))
        {
            request.Filters = [new Filter { Key = FilterNameStringType.Name, Values = [prefix] }];
        }

        for (var page = 0; ; page++)
        {
            if (page == MaxPages)
            {
                warnings.Add($"Stopped listing AWS secrets after {MaxPages} pages.");
                break;
            }

            var response = await client.ListSecretsAsync(request, cancellationToken).ConfigureAwait(false);
            result.AddRange((response?.SecretList ?? []).Where(e => e is not null && !string.IsNullOrEmpty(e.Name)));
            var next = response?.NextToken;
            if (string.IsNullOrEmpty(next) || !seenTokens.Add(next))
            {
                break;
            }

            request.NextToken = next;
        }

        return result;
    }

    private static async Task<Dictionary<string, (string? Text, bool Binary)>> ReadValuesAsync(
        IAmazonSecretsManager client,
        List<SecretListEntry> listed,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, (string?, bool)>(StringComparer.Ordinal);
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in listed)
        {
            byId.TryAdd(entry.Name!, entry.Name!);
            if (!string.IsNullOrEmpty(entry.ARN))
            {
                byId.TryAdd(entry.ARN, entry.Name!);
            }
        }

        var ids = listed.Select(e => string.IsNullOrEmpty(e.ARN) ? e.Name! : e.ARN).Distinct(StringComparer.Ordinal).ToList();
        var useBatch = true;
        foreach (var chunk in ids.Chunk(BatchSize))
        {
            if (useBatch)
            {
                try
                {
                    await ReadBatchAsync(client, chunk, byId, values, warnings, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch (AmazonSecretsManagerException ex) when (ex.ErrorCode is "AccessDeniedException" or "UnknownOperationException")
                {
                    // BatchGetSecretValue needs its own permission; fall back to one GetSecretValue per secret.
                    useBatch = false;
                }
            }

            foreach (var id in chunk)
            {
                try
                {
                    var response = await client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = id }, cancellationToken).ConfigureAwait(false);
                    values[byId[id]] = (response?.SecretString, response?.SecretString is null && response?.SecretBinary is not null);
                }
                catch (AmazonServiceException ex)
                {
                    warnings.Add($"Could not read the value of AWS secret {byId[id]}: {ex.ErrorCode ?? ex.StatusCode.ToString()}.");
                }
            }
        }

        return values;
    }

    private static async Task ReadBatchAsync(
        IAmazonSecretsManager client,
        string[] chunk,
        Dictionary<string, string> byId,
        Dictionary<string, (string?, bool)> values,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var request = new BatchGetSecretValueRequest { SecretIdList = [.. chunk] };
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        var answered = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < MaxPages; page++)
        {
            var response = await client.BatchGetSecretValueAsync(request, cancellationToken).ConfigureAwait(false);
            foreach (var value in response?.SecretValues ?? [])
            {
                // Only take values for secrets that were asked for, matched by name or ARN.
                if (value is not null
                    && ((value.Name is not null && byId.TryGetValue(value.Name, out var name)) || (value.ARN is not null && byId.TryGetValue(value.ARN, out name))))
                {
                    values[name] = (value.SecretString, value.SecretString is null && value.SecretBinary is not null);
                    answered.Add(name);
                }
            }

            foreach (var error in response?.Errors ?? [])
            {
                var id = error?.SecretId;
                var name = id is not null && byId.TryGetValue(id, out var known) ? known : id ?? "(unknown)";
                answered.Add(name);
                warnings.Add($"Could not read the value of AWS secret {name}: {error?.ErrorCode ?? "unknown error"}.");
            }

            var next = response?.NextToken;
            if (string.IsNullOrEmpty(next) || !seenTokens.Add(next))
            {
                break;
            }

            request.NextToken = next;
        }

        foreach (var id in chunk)
        {
            if (!answered.Contains(byId[id]))
            {
                warnings.Add($"AWS did not return the value of secret {byId[id]}.");
            }
        }
    }

    private static AwsSecret ToSecret(SecretListEntry entry, (string? Text, bool Binary)? value)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in entry.Tags ?? [])
        {
            if (tag?.Key is { } key)
            {
                tags[key] = tag.Value ?? string.Empty;
            }
        }

        return new AwsSecret(entry.Name!)
        {
            Arn = string.IsNullOrEmpty(entry.ARN) ? null : entry.ARN,
            Description = string.IsNullOrEmpty(entry.Description) ? null : entry.Description,
            LastChanged = ToOffset(entry.LastChangedDate),
            Tags = tags,
            Value = value?.Text,
            IsBinary = value?.Binary ?? false,
            ValueRead = value is not null,
        };
    }

    internal static DateTimeOffset? ToOffset(DateTime? date)
    {
        if (date is not { } d)
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d.ToUniversalTime());
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
