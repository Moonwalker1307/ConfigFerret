using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ConfigFerret.Shared;

namespace ConfigFerret.HashiCorp;

/// <summary>
/// A snapshot of the Consul KV keys under one prefix, mapped to configuration keys. Load it with
/// <see cref="LoadAsync(ConsulOptions, CancellationToken)"/>, or build it from entries you already have, then pass it to
/// <see cref="HashiCorpTracerExtensions.WithConsul"/>.
/// </summary>
public sealed class ConsulKeyValues
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Dictionary<string, List<ConsulEntry>> _byConfigurationKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a snapshot from entries you already have.</summary>
    /// <param name="entries">The entries, in the order Consul returned them.</param>
    /// <param name="prefix">The prefix they were read from.</param>
    /// <param name="datacenter">The datacenter, or null for the agent's own.</param>
    /// <param name="location">Where the keys are, shown on each step, for example <c>consul.example.com:8501</c>.</param>
    /// <param name="isSensitive">True to treat every value as secret.</param>
    /// <param name="warnings">Problems found while loading.</param>
    public ConsulKeyValues(
        IEnumerable<ConsulEntry> entries,
        string prefix = "",
        string? datacenter = null,
        string? location = null,
        bool isSensitive = false,
        IEnumerable<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Entries = [.. entries];
        foreach (var entry in Entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (!_byConfigurationKey.TryGetValue(entry.ConfigurationKey, out var list))
            {
                _byConfigurationKey[entry.ConfigurationKey] = list = [];
            }

            list.Add(entry);
        }

        Prefix = prefix ?? string.Empty;
        Datacenter = string.IsNullOrWhiteSpace(datacenter) ? null : datacenter.Trim();
        Location = string.IsNullOrWhiteSpace(location) ? (Datacenter is null ? "Consul" : $"Consul datacenter {Datacenter}") : location;
        IsSensitive = isSensitive;
        Warnings = [.. warnings ?? []];
    }

    /// <summary>Every entry, in the order Consul returned them.</summary>
    public IReadOnlyList<ConsulEntry> Entries { get; }

    /// <summary>The prefix the keys were read from.</summary>
    public string Prefix { get; }

    /// <summary>The datacenter, or null for the agent's own.</summary>
    public string? Datacenter { get; }

    /// <summary>Where the keys are: the Consul address and datacenter, shown as each step's location.</summary>
    public string Location { get; }

    /// <summary>True when every value is treated as secret.</summary>
    public bool IsSensitive { get; }

    /// <summary>Problems found while loading, such as a value that is not valid base64.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Returns the entries that set <paramref name="configurationKey"/> (case-insensitive), in the order Consul returned them.</summary>
    public IReadOnlyList<ConsulEntry> Find(string configurationKey)
    {
        ArgumentNullException.ThrowIfNull(configurationKey);
        return _byConfigurationKey.TryGetValue(configurationKey, out var list) ? list : [];
    }

    /// <summary>Reads the keys from the Consul HTTP API.</summary>
    /// <exception cref="ArgumentException">An option is missing or not valid.</exception>
    /// <exception cref="HttpRequestException">Consul could not be reached, or answered with an error other than 404.</exception>
    public static async Task<ConsulKeyValues> LoadAsync(ConsulOptions options, CancellationToken cancellationToken = default)
    {
        using var client = JsonApi.CreateClient();
        return await LoadAsync(options, client, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the keys from the Consul HTTP API with your own <see cref="HttpClient"/> (for a proxy or a test handler).
    /// The client should not follow redirects, so the token never reaches another host.
    /// </summary>
    /// <exception cref="ArgumentException">An option is missing or not valid.</exception>
    /// <exception cref="HttpRequestException">Consul could not be reached, or answered with an error other than 404.</exception>
    public static async Task<ConsulKeyValues> LoadAsync(ConsulOptions options, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        var address = JsonApi.RequireSafeBaseAddress(options.Address, options.AllowUnencryptedHttp, nameof(options.Address));
        var prefix = (options.Prefix ?? string.Empty).TrimStart('/');
        if (prefix.Split('/').SkipLast(1).Any(s => s is "" or "." or "..") || prefix.Split('/')[^1] is "." or "..")
        {
            throw new ArgumentException($"{nameof(options.Prefix)} must be a key prefix such as 'config/orders/', without empty, '.' or '..' segments.", nameof(options));
        }

        if (options.MaxKeys < 1)
        {
            throw new ArgumentException($"{nameof(options.MaxKeys)} must be at least 1.", nameof(options));
        }

        if (options.Token is { } token && token.Any(c => char.IsControl(c) || c > '\x7e'))
        {
            // Never include the value.
            throw new ArgumentException($"{nameof(options.Token)} holds characters that cannot be sent in an HTTP header.", nameof(options));
        }

        var datacenter = string.IsNullOrWhiteSpace(options.Datacenter) ? null : options.Datacenter.Trim();
        var query = "?recurse=true" + (datacenter is null ? string.Empty : "&dc=" + Uri.EscapeDataString(datacenter));
        var uri = new Uri(address, "v1/kv/" + string.Join('/', prefix.Split('/').Select(Uri.EscapeDataString)) + query);
        var location = datacenter is null ? address.Authority : $"{address.Authority} datacenter {datacenter}";
        var warnings = new List<string>();
        var entries = new List<ConsulEntry>();

        JsonDocument doc;
        try
        {
            doc = await JsonApi.GetAsync(client: httpClient, uri, headers =>
            {
                headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (!string.IsNullOrEmpty(options.Token))
                {
                    headers.Add("X-Consul-Token", options.Token);
                }
            }, "Consul", null, options.Token, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            warnings.Add($"Consul has no keys under prefix '{prefix}'.");
            return new ConsulKeyValues(entries, prefix, datacenter, location, options.IsSensitive, warnings);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                warnings.Add($"Consul returned an unexpected response for prefix '{prefix}': not an array of keys.");
            }
            else
            {
                new Reader(options, prefix, entries, warnings).Read(doc.RootElement);
            }
        }

        return new ConsulKeyValues(entries, prefix, datacenter, location, options.IsSensitive, warnings);
    }

    private sealed class Reader(ConsulOptions options, string prefix, List<ConsulEntry> entries, List<string> warnings)
    {
        private int _skipped;
        private int _notText;

        public void Read(JsonElement items)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (entries.Count >= options.MaxKeys)
                {
                    warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Stopped reading Consul after {options.MaxKeys} keys (MaxKeys)."));
                    break;
                }

                ReadItem(item);
            }

            if (_skipped > 0)
            {
                warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Skipped {_skipped} Consul entries that have no key, or no name once the prefix is stripped."));
            }

            if (_notText > 0)
            {
                warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Skipped {_notText} Consul values that are not base64-encoded UTF-8 text."));
            }
        }

        private void ReadItem(JsonElement item)
        {
            var key = JsonRead.String(JsonRead.Property(item, "Key"));
            if (string.IsNullOrEmpty(key))
            {
                _skipped++;
                return;
            }

            var encoded = JsonRead.Property(item, "Value");
            if (key.EndsWith('/') && encoded is null or { ValueKind: JsonValueKind.Null })
            {
                // A folder.
                return;
            }

            string? value = null;
            if (encoded is { ValueKind: not JsonValueKind.Null } e)
            {
                if (JsonRead.String(e) is not { } base64 || Decode(base64) is not { } decoded)
                {
                    _notText++;
                    return;
                }

                value = decoded;
            }

            var modifyIndex = JsonRead.Number(JsonRead.Property(item, "ModifyIndex"));
            var rest = key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : key;
            var configurationKey = rest.Trim('/').Replace('/', ':');

            if (options.ExpandJson && value is not null && Expand(key, configurationKey, value, modifyIndex))
            {
                return;
            }

            if (configurationKey.Length == 0)
            {
                // The prefix names this key itself: use its last segment, so it still has a name.
                configurationKey = key.TrimEnd('/').Split('/')[^1];
                if (configurationKey.Length == 0)
                {
                    _skipped++;
                    return;
                }
            }

            entries.Add(new ConsulEntry(key, configurationKey, value, modifyIndex));
        }

        // Expands a JSON object or array value into one entry per leaf. Returns false when the value is not one.
        private bool Expand(string key, string baseKey, string value, long? modifyIndex)
        {
            var trimmed = value.AsSpan().TrimStart();
            if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            {
                return false;
            }

            JsonDocument json;
            try
            {
                json = JsonDocument.Parse(value);
            }
            catch (JsonException)
            {
                return false;
            }

            using (json)
            {
                var leaves = new List<(string Path, string? Value)>();
                Flatten(json.RootElement, string.Empty, leaves);
                foreach (var (path, leaf) in leaves)
                {
                    var configurationKey = NameMatching.Combine(baseKey, path);
                    if (configurationKey.Length > 0)
                    {
                        entries.Add(new ConsulEntry(key, configurationKey, leaf, modifyIndex, path.Length == 0 ? null : path));
                    }
                }
            }

            return true;
        }

        // Like the .NET JSON configuration provider: object members and array indexes become key segments, and an empty
        // object or array is a key with a null value.
        private void Flatten(JsonElement element, string path, List<(string Path, string? Value)> leaves)
        {
            if (entries.Count + leaves.Count >= options.MaxKeys)
            {
                return;
            }

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var any = false;
                    foreach (var (name, child) in JsonRead.Properties(element))
                    {
                        any = true;
                        Flatten(child, NameMatching.Combine(path, name), leaves);
                    }

                    if (!any)
                    {
                        leaves.Add((path, null));
                    }

                    break;
                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var child in element.EnumerateArray())
                    {
                        Flatten(child, NameMatching.Combine(path, index.ToString(CultureInfo.InvariantCulture)), leaves);
                        index++;
                    }

                    if (index == 0)
                    {
                        leaves.Add((path, null));
                    }

                    break;
                default:
                    leaves.Add((path, JsonRead.Text(element)));
                    break;
            }
        }

        private static string? Decode(string base64)
        {
            var buffer = new byte[(base64.Length * 3 / 4) + 3];
            if (!Convert.TryFromBase64String(base64, buffer, out var length))
            {
                return null;
            }

            try
            {
                return StrictUtf8.GetString(buffer, 0, length);
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }
    }
}
