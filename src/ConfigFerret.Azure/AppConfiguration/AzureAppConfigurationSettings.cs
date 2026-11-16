using System.Globalization;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Data.AppConfiguration;
using ConfigFerret.Sources;

namespace ConfigFerret.Azure;

/// <summary>
/// A snapshot of the key-values read from one Azure App Configuration store, for the labels the app reads. Load it with
/// <see cref="LoadAsync(AzureAppConfigurationOptions, CancellationToken)"/>, or build it from settings you already have,
/// then pass it to <see cref="AzureTracerExtensions.WithAzureAppConfiguration"/>.
/// </summary>
public sealed class AzureAppConfigurationSettings
{
    /// <summary>The content type App Configuration gives a Key Vault reference.</summary>
    public const string KeyVaultReferenceContentType = "application/vnd.microsoft.appconfig.keyvaultref+json";

    /// <summary>The key prefix App Configuration gives a feature flag.</summary>
    public const string FeatureFlagPrefix = ".appconfig.featureflag/";

    // Stop after this many pages (100 key-values each) per selector so a misbehaving server cannot keep a trace looping.
    internal const int MaxPages = 400;

    // A JSON key-value is flattened from at most this many elements, and one load into at most MaxJsonValuesPerLoad
    // values whose keys hold at most MaxJsonKeyCharactersPerLoad characters.
    internal const int MaxJsonValues = 1_000;
    internal const int MaxJsonValuesPerLoad = 100_000;
    internal const int MaxJsonKeyCharactersPerLoad = 10_000_000;

    // The provider parses a JSON value wrapped in one more object, with JsonDocument's default depth of 64.
    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, MaxDepth = 63 };

    private static readonly Dictionary<AzureAppConfigurationSetting, IReadOnlyList<AzureAppConfigurationSetting>> NoneFlattened = [];

    private const string FeatureFlagContentType = "application/vnd.microsoft.appconfig.ff+json";
    private const string KeyDelimiter = ":";

    private readonly Dictionary<string, List<AzureAppConfigurationSetting>> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<AzureAppConfigurationSetting>> _byStoredKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<AzureAppConfigurationSetting>> _byKeyOrOwnKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<AzureAppConfigurationSetting, IReadOnlyList<AzureAppConfigurationSetting>> _flattened;

    /// <summary>Creates a snapshot from settings you already have. Each setting sets its own configuration key.</summary>
    /// <param name="store">The store name, for example <c>contoso</c>.</param>
    /// <param name="settings">The settings, lowest priority first: for one key, a later label wins over an earlier one.</param>
    /// <param name="warnings">Problems found while reading them.</param>
    public AzureAppConfigurationSettings(string store, IEnumerable<AzureAppConfigurationSetting> settings, IEnumerable<string>? warnings = null)
        : this(store, settings, warnings, NoneFlattened)
    {
    }

    // flattened maps a stored JSON key-value, by reference, to the values the provider flattens it to.
    internal AzureAppConfigurationSettings(
        string store,
        IEnumerable<AzureAppConfigurationSetting> settings,
        IEnumerable<string>? warnings,
        IReadOnlyDictionary<AzureAppConfigurationSetting, IReadOnlyList<AzureAppConfigurationSetting>> flattened)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(store);
        ArgumentNullException.ThrowIfNull(settings);
        Store = store;
        Settings = [.. settings];
        _flattened = flattened;
        foreach (var setting in Settings)
        {
            ArgumentNullException.ThrowIfNull(setting, nameof(settings));
            Add(_byStoredKey, setting.Key, setting);
            var values = Flattened(setting) ?? [setting];
            foreach (var value in values)
            {
                Add(_byKey, value.ConfigurationKey, value);
                Add(_byKeyOrOwnKey, value.ConfigurationKey, value);
            }

            // A JSON key-value also answers to its own configuration key, once, unless a value it sets already does.
            if (!values.Any(value => string.Equals(value.ConfigurationKey, setting.ConfigurationKey, StringComparison.OrdinalIgnoreCase)))
            {
                Add(_byKeyOrOwnKey, setting.ConfigurationKey, setting);
            }

            if (setting.Kind == AzureAppConfigurationSettingKind.FeatureFlag)
            {
                LongestFlagKey = Math.Max(LongestFlagKey, setting.ConfigurationKey.Length);
            }
        }

        Warnings = [.. warnings ?? []];
    }

    /// <summary>
    /// <see cref="FindByConfigurationKey"/>, plus each stored JSON key-value whose own configuration key (its key with the
    /// trimmed prefixes removed) is <paramref name="key"/>, holding its whole JSON, lowest priority first. For matching a
    /// name to a key-value, as a variable that holds the JSON of <c>App:Retry</c> may be named <c>Retry</c>.
    /// </summary>
    internal IReadOnlyList<AzureAppConfigurationSetting> FindByConfigurationKeyOrOwnKey(string key) =>
        _byKeyOrOwnKey.TryGetValue(key, out var list) ? list : [];

    /// <summary>The length of the longest configuration key a feature flag sets, or 0 when there is no flag.</summary>
    internal int LongestFlagKey { get; }

    /// <summary>The store name, for example <c>contoso</c>.</summary>
    public string Store { get; }

    /// <summary>
    /// Every key-value as it is stored, lowest priority first. A key-value with a JSON content type is one setting here,
    /// holding its stored value; <see cref="FindByConfigurationKey"/> finds the values it is flattened to.
    /// </summary>
    public IReadOnlyList<AzureAppConfigurationSetting> Settings { get; }

    /// <summary>Problems found while loading, such as a Key Vault reference that could not be read.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// The settings that set the configuration key <paramref name="key"/> (case-insensitive), lowest priority first, so the
    /// last one wins. A key-value with a JSON content type that was loaded gives one setting for each value in it, under
    /// the key the provider flattens it to, such as <c>Orders:Retry:Count</c>, holding that one value.
    /// </summary>
    public IReadOnlyList<AzureAppConfigurationSetting> FindByConfigurationKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _byKey.TryGetValue(key, out var list) ? list : [];
    }

    /// <summary>
    /// The key-values stored under the key <paramref name="key"/> (case-insensitive), one for each label, lowest priority
    /// first. Each holds its stored value: for a key-value with a JSON content type, the whole JSON text.
    /// </summary>
    public IReadOnlyList<AzureAppConfigurationSetting> Find(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _byStoredKey.TryGetValue(key, out var list) ? list : [];
    }

    /// <summary>Wraps the snapshot for <see cref="ConfigSourceTracerExtensions.WithSource"/>, so unused key-values can be listed.</summary>
    public LoadedConfigSource ToLoadedSource(AzureAppConfigurationTraceOptions? traceOptions = null) =>
        new(
            $"Azure App Configuration {Store}",
            new AzureAppConfigurationResolver(this, traceOptions ?? new AzureAppConfigurationTraceOptions()),
            Settings.Select(s => new SourceEntry(
                s.Key,
                Reference(s),
                IsSensitive: s.Kind == AzureAppConfigurationSettingKind.KeyVaultReference,
                Detail(s),
                $"{s.Store}, {s.LabelText}")),
            Warnings);

    /// <summary>Reads the key-values, signing in with the connection string, the service principal or <c>DefaultAzureCredential</c>.</summary>
    /// <exception cref="RequestFailedException">App Configuration could not be reached, refused the request or sent a response that could not be read.</exception>
    /// <exception cref="global::Azure.Identity.AuthenticationFailedException">Signing in failed.</exception>
    public static Task<AzureAppConfigurationSettings> LoadAsync(AzureAppConfigurationOptions options, CancellationToken cancellationToken = default) =>
        LoadAsync(options, null, new ConfigurationClientOptions(), cancellationToken);

    /// <summary>
    /// Reads the key-values from <see cref="AzureAppConfigurationOptions.Endpoint"/>, signing in with <paramref name="credential"/>.
    /// Without an endpoint, signs in with the connection string when there is one, and <paramref name="credential"/> is not used.
    /// </summary>
    /// <exception cref="RequestFailedException">App Configuration could not be reached, refused the request or sent a response that could not be read.</exception>
    /// <exception cref="global::Azure.Identity.AuthenticationFailedException">Signing in failed.</exception>
    public static Task<AzureAppConfigurationSettings> LoadAsync(AzureAppConfigurationOptions options, TokenCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return LoadAsync(options, credential, new ConfigurationClientOptions(), cancellationToken);
    }

    /// <summary>
    /// Reads the key-values with your own client options, for example a proxy, retry settings, or a <c>Transport</c>
    /// (<c>new HttpClientTransport(httpClient)</c>) that answers from a test handler. Without an endpoint, or with a null
    /// <paramref name="credential"/>, signs in with the connection string when there is one. Otherwise it signs in with
    /// <paramref name="credential"/>, the service principal or <c>DefaultAzureCredential</c>.
    /// </summary>
    /// <exception cref="RequestFailedException">App Configuration could not be reached, refused the request or sent a response that could not be read.</exception>
    /// <exception cref="global::Azure.Identity.AuthenticationFailedException">Signing in failed.</exception>
    public static async Task<AzureAppConfigurationSettings> LoadAsync(
        AzureAppConfigurationOptions options,
        TokenCredential? credential,
        ConfigurationClientOptions clientOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clientOptions);
        var (client, store) = CreateClient(options, credential, clientOptions);
        var settings = new List<AzureAppConfigurationSetting>();
        var flattened = new Dictionary<AzureAppConfigurationSetting, IReadOnlyList<AzureAppConfigurationSetting>>(ReferenceEqualityComparer.Instance);
        var warnings = new List<string>();

        // The provider keeps the prefixes sorted in descending order with case ignored, so of two nested prefixes the
        // longer one is tried first, and it compares them with case ignored.
        var prefixes = options.TrimKeyPrefixes.Where(p => !string.IsNullOrEmpty(p)).OrderDescending(StringComparer.OrdinalIgnoreCase).ToList();
        var json = new JsonBudget();
        var keyFilter = string.IsNullOrWhiteSpace(options.KeyFilter) ? SettingSelector.Any : options.KeyFilter.Trim();
        foreach (var label in ReadLabels(options.Labels))
        {
            await ReadAsync(client, store, keyFilter, label, options, prefixes, json, settings, flattened, warnings, cancellationToken).ConfigureAwait(false);
        }

        // As in the provider, feature flags have their own labels, which default to no label whatever Labels holds.
        if (options.IncludeFeatureFlags)
        {
            foreach (var label in ReadLabels(options.FeatureFlagLabels))
            {
                await ReadAsync(client, store, FeatureFlagPrefix + "*", label, options, prefixes, json, settings, flattened, warnings, cancellationToken).ConfigureAwait(false);
            }
        }

        return new AzureAppConfigurationSettings(store, settings, warnings, flattened);
    }

    /// <summary>The values a stored JSON key-value was flattened to, or null when it sets its own key as it is.</summary>
    internal IReadOnlyList<AzureAppConfigurationSetting>? Flattened(AzureAppConfigurationSetting setting) =>
        _flattened.TryGetValue(setting, out var values) ? values : null;

    // An empty entry or \0 is no label (null), and an empty list reads only keys with no label.
    private static List<string?> ReadLabels(IList<string> labels) =>
        labels.Count == 0 ? [null] : labels.Select(l => string.IsNullOrEmpty(l) || l == "\0" ? null : l).Distinct().ToList();

    internal static AzureAppConfigurationSettingRef Reference(AzureAppConfigurationSetting setting) =>
        new(setting.Store, setting.Key, setting.Label, setting.SecretId);

    internal static string? Detail(AzureAppConfigurationSetting setting) => setting.Kind switch
    {
        AzureAppConfigurationSettingKind.KeyVaultReference => setting.SecretId is null ? "Key Vault reference" : $"Key Vault reference to {setting.SecretId}",
        AzureAppConfigurationSettingKind.FeatureFlag => setting.Enabled == true ? "feature flag, on" : "feature flag, off",
        _ => string.IsNullOrEmpty(setting.ContentType) ? null : $"content type {setting.ContentType}",
    };

    internal static (ConfigurationClient Client, string Store) CreateClient(AzureAppConfigurationOptions options, TokenCredential? credential, ConfigurationClientOptions clientOptions)
    {
        // A credential needs an endpoint, so a connection string alone is used whatever credential the app registered.
        if (!string.IsNullOrWhiteSpace(options.ConnectionString) && (credential is null || options.Endpoint is null))
        {
            var endpoint = ReadEndpoint(options.ConnectionString);
            try
            {
                return (new ConfigurationClient(options.ConnectionString, clientOptions), AzureAuth.ShortName(endpoint));
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
            {
                // Never pass on the SDK's message: it may quote the connection string.
                throw new ArgumentException("ConnectionString is not a valid App Configuration connection string (Endpoint=...;Id=...;Secret=...).", nameof(options));
            }
        }

        var uri = RequireEndpoint(options.Endpoint);
        credential ??= AzureAuth.Create(options.TenantId, options.ClientId, options.ClientSecret);
        return (new ConfigurationClient(uri, credential, clientOptions), AzureAuth.ShortName(uri));
    }

    internal static Uri RequireEndpoint(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Endpoint must be an absolute https URL without credentials, for example https://contoso.azconfig.io, or set ConnectionString.", nameof(uri));
        }

        return uri;
    }

    /// <summary>Reads the <c>Endpoint=</c> part of a connection string, without ever quoting the string itself.</summary>
    internal static Uri ReadEndpoint(string connectionString)
    {
        foreach (var pair in connectionString.Split(';').Select(part => part.Split('=', 2)))
        {
            if (pair.Length == 2 && string.Equals(pair[0].Trim(), "Endpoint", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.TryCreate(pair[1].Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
                    ? uri
                    : throw new ArgumentException("The Endpoint in ConnectionString must be an absolute https URL.", nameof(connectionString));
            }
        }

        throw new ArgumentException("ConnectionString has no Endpoint=... part.", nameof(connectionString));
    }

    private static async Task ReadAsync(
        ConfigurationClient client,
        string store,
        string keyFilter,
        string? label,
        AzureAppConfigurationOptions options,
        IReadOnlyList<string> prefixes,
        JsonBudget json,
        List<AzureAppConfigurationSetting> into,
        Dictionary<AzureAppConfigurationSetting, IReadOnlyList<AzureAppConfigurationSetting>> flattened,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var flags = keyFilter.StartsWith(FeatureFlagPrefix, StringComparison.Ordinal);
        var selector = new SettingSelector { KeyFilter = keyFilter, LabelFilter = label ?? "\0" };
        try
        {
            var pages = 0;
            await foreach (var page in client.GetConfigurationSettingsAsync(selector, cancellationToken).AsPages().ConfigureAwait(false))
            {
                foreach (var setting in page.Values)
                {
                    if (setting?.Key is not { Length: > 0 } key || key.StartsWith(FeatureFlagPrefix, StringComparison.Ordinal) != flags)
                    {
                        // Feature flags come from their own selector, so the one for plain keys skips them.
                        continue;
                    }

                    if (Convert(setting, store, options, prefixes, warnings) is not { } converted)
                    {
                        continue;
                    }

                    into.Add(converted);
                    if (converted.Kind == AzureAppConfigurationSettingKind.KeyValue && IsJson(converted.ContentType) && converted.Value is not null
                        && Flatten(converted, converted.Value, prefixes, json, warnings) is { } values)
                    {
                        flattened[converted] = values;
                    }
                }

                if (++pages >= MaxPages && page.ContinuationToken is not null)
                {
                    warnings.Add($"Stopped listing Azure App Configuration {store} keys {keyFilter} after {MaxPages} pages; some key-values were not read.");
                    break;
                }
            }
        }
        catch (Exception ex) when (AzureKeyVaultSecrets.IsUnreadable(ex))
        {
            throw new RequestFailedException($"Azure App Configuration {store} sent a response that could not be read.", ex);
        }
        catch (AggregateException ex) when (AzureKeyVaultSecrets.IsUnreachable(ex, cancellationToken))
        {
            throw AzureKeyVaultSecrets.Unreachable($"Azure App Configuration {store}", ex);
        }
        catch (AggregateException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw AzureKeyVaultSecrets.Cancelled(ex, cancellationToken);
        }
    }

    private static AzureAppConfigurationSetting? Convert(ConfigurationSetting setting, string store, AzureAppConfigurationOptions options, IReadOnlyList<string> prefixes, List<string> warnings)
    {
        var label = string.IsNullOrEmpty(setting.Label) ? null : setting.Label;
        var contentType = setting.ContentType;
        var basic = new AzureAppConfigurationSetting(setting.Key, label, store)
        {
            ContentType = contentType,
            LastModified = setting.LastModified,
            IsReadOnly = setting.IsReadOnly == true,
            Tags = new Dictionary<string, string>(setting.Tags ?? new Dictionary<string, string>(), StringComparer.Ordinal),
        };
        var raw = ReadValue(setting);

        if (setting.Key.StartsWith(FeatureFlagPrefix, StringComparison.Ordinal))
        {
            return ToFeatureFlag(basic, raw, prefixes, warnings);
        }

        if (IsContentType(contentType, KeyVaultReferenceContentType) || setting is SecretReferenceConfigurationSetting)
        {
            if (!options.IncludeKeyVaultReferences)
            {
                return null;
            }

            var secretId = ReadSecretUri(raw);
            if (secretId is null)
            {
                warnings.Add($"Azure App Configuration Key Vault reference {setting.Key} ({basic.LabelText}) in {store} has no valid secret URI.");
            }

            return basic with { Kind = AzureAppConfigurationSettingKind.KeyVaultReference, SecretId = secretId, ConfigurationKey = Trim(setting.Key, prefixes) };
        }

        return basic with { Value = raw, ConfigurationKey = Trim(setting.Key, prefixes) };
    }

    // The provider flattens a value with a JSON content type the way a JSON settings file is read: {"Count":3} under
    // Orders:Retry sets Orders:Retry:Count to 3, and a JSON string sets the key itself. Like its JsonFlattener, a string
    // gives its text, a number its JSON text, true and false give True and False, null gives an empty string, an empty
    // property name gives a key that ends in ':', and an empty object or array sets nothing. The trimmed prefixes apply
    // to the flattened keys, and comments are skipped. Null means the value sets its own key as it is stored: it is not
    // valid JSON, it nests deeper than the provider reads, or a bound for the load was passed.
    //
    // The store's answer is not trusted. Each key is built only when its element is reached, from its parent's key, and
    // the elements read from one key-value, the values from one load and the key characters for one load are bounded,
    // with warnings that name a key, its label and the store, never a value.
    internal static IReadOnlyList<AzureAppConfigurationSetting>? Flatten(AzureAppConfigurationSetting whole, string raw, IReadOnlyList<string> prefixes, JsonBudget budget, List<string> warnings)
    {
        if (budget.Stopped)
        {
            return null;
        }

        if (budget.ValuesLeft <= 0)
        {
            budget.WarnOnce(whole.Store, warnings);
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                budget.ValuesLeft--;
                return [whole with { Value = root.ToString() }];
            }

            var values = new List<AzureAppConfigurationSetting>();
            var limit = Math.Min(MaxJsonValues, budget.ValuesLeft);
            var pending = new Stack<JsonFrame>();
            pending.Push(new JsonFrame(whole.Key, root));
            var elements = 0;
            while (pending.TryPeek(out var frame))
            {
                if (!frame.MoveNext(out var segment, out var child))
                {
                    pending.Pop();
                    continue;
                }

                var container = child.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
                if (++elements > MaxJsonValues)
                {
                    warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Azure App Configuration key {whole.Key} ({whole.LabelText}) in {whole.Store} holds more than {MaxJsonValues:N0} JSON elements; only the first {MaxJsonValues:N0} are traced."));
                    break;
                }

                if (!container && values.Count == limit)
                {
                    budget.WarnOnce(whole.Store, warnings);
                    break;
                }

                var length = frame.Key.Length + KeyDelimiter.Length + segment.Length;
                if (length > budget.KeyCharactersLeft)
                {
                    budget.Stop(whole, warnings);
                    return null;
                }

                budget.KeyCharactersLeft -= length;
                var key = string.Concat(frame.Key, KeyDelimiter, segment);
                if (container)
                {
                    pending.Push(new JsonFrame(key, child));
                }
                else
                {
                    values.Add(whole with { ConfigurationKey = Trim(key, prefixes), Value = child.ToString() });
                }
            }

            budget.ValuesLeft -= values.Count;
            return values;
        }
    }

    // A content type counts as JSON as the provider counts it: the main type is application and one of the parts of the
    // subtype split on '+' is json, case ignored. The provider leaves out the Key Vault reference and feature flag types
    // only when they match exactly; here case is ignored, so a reference typed in other case is still kept as a
    // reference and its content is never shown as values.
    private static bool IsJson(string? contentType)
    {
        var mediaType = contentType?.Split(';')[0].Trim();
        if (string.IsNullOrEmpty(mediaType) || IsContentType(mediaType, KeyVaultReferenceContentType) || IsContentType(mediaType, FeatureFlagContentType))
        {
            return false;
        }

        var slash = mediaType.IndexOf('/', StringComparison.Ordinal);
        return slash > 0
            && mediaType.AsSpan(0, slash).Equals("application", StringComparison.OrdinalIgnoreCase)
            && mediaType[(slash + 1)..].Split('+').Any(part => part.Equals("json", StringComparison.OrdinalIgnoreCase));
    }

    internal sealed class JsonBudget
    {
        private bool _warned;

        public int ValuesLeft { get; set; } = MaxJsonValuesPerLoad;

        public int KeyCharactersLeft { get; set; } = MaxJsonKeyCharactersPerLoad;

        public bool Stopped { get; private set; }

        public void WarnOnce(string store, List<string> warnings)
        {
            if (!_warned)
            {
                _warned = true;
                warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Stopped flattening JSON key-values from Azure App Configuration {store} after {MaxJsonValuesPerLoad:N0} values; the rest are traced as they are stored."));
            }
        }

        public void Stop(AzureAppConfigurationSetting at, List<string> warnings)
        {
            Stopped = true;
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Stopped flattening JSON key-values from Azure App Configuration {at.Store} at key {at.Key} ({at.LabelText}): the flattened keys would pass {MaxJsonKeyCharactersPerLoad:N0} characters, so it and the rest are traced as they are stored."));
        }
    }

    // One object or array being flattened: its key, and how far its children have been read.
    private sealed class JsonFrame
    {
        private readonly bool _isObject;
        private JsonElement.ObjectEnumerator _properties;
        private JsonElement.ArrayEnumerator _items;
        private int _index;

        public JsonFrame(string key, JsonElement element)
        {
            Key = key;
            _isObject = element.ValueKind == JsonValueKind.Object;
            if (_isObject)
            {
                _properties = element.EnumerateObject();
            }
            else
            {
                _items = element.EnumerateArray();
            }
        }

        public string Key { get; }

        public bool MoveNext(out string segment, out JsonElement element)
        {
            if (_isObject ? _properties.MoveNext() : _items.MoveNext())
            {
                segment = _isObject ? _properties.Current.Name : (_index++).ToString(CultureInfo.InvariantCulture);
                element = _isObject ? _properties.Current.Value : _items.Current;
                return true;
            }

            segment = string.Empty;
            element = default;
            return false;
        }
    }

    // The SDK turns Key Vault references and feature flags into typed settings; reading Value gives their JSON back.
    private static string? ReadValue(ConfigurationSetting setting)
    {
        try
        {
            return setting.Value;
        }
        catch (Exception ex) when (AzureKeyVaultSecrets.IsUnreadable(ex))
        {
            return null;
        }
    }

    private static AzureAppConfigurationSetting ToFeatureFlag(AzureAppConfigurationSetting basic, string? raw, IReadOnlyList<string> prefixes, List<string> warnings)
    {
        var name = basic.Key[FeatureFlagPrefix.Length..];
        bool? enabled = null;
        var filters = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(raw ?? string.Empty);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    name = id.GetString()!;
                }

                if (root.TryGetProperty("enabled", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    enabled = on.GetBoolean();
                }

                if (root.TryGetProperty("conditions", out var conditions) && conditions.ValueKind == JsonValueKind.Object
                    && conditions.TryGetProperty("client_filters", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    filters.AddRange(list.EnumerateArray()
                        .Select(filter => filter.ValueKind == JsonValueKind.Object && filter.TryGetProperty("name", out var filterName) && filterName.ValueKind == JsonValueKind.String ? filterName.GetString() : null)
                        .Where(name => name is not null)
                        .Select(name => name!));
                }
            }
        }
        catch (JsonException)
        {
            // Reported below, without the value.
        }

        if (enabled is null)
        {
            warnings.Add($"Azure App Configuration feature flag {basic.Key} ({basic.LabelText}) in {basic.Store} could not be read.");
        }

        // The .NET provider sets FeatureManagement:<name> to true or false, or, with filters, FeatureManagement:<name>:EnabledFor.
        // It trims that key by the prefixes, as it trims the keys of key-values.
        var value = enabled is null || (enabled == true && filters.Count > 0) ? null : (enabled == true ? "True" : "False");
        return basic with
        {
            Kind = AzureAppConfigurationSettingKind.FeatureFlag,
            ConfigurationKey = Trim("FeatureManagement:" + name, prefixes),
            Enabled = enabled,
            Filters = filters,
            Value = value,
        };
    }

    private static Uri? ReadSecretUri(string? raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw ?? string.Empty);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("uri", out var uri)
                && uri.ValueKind == JsonValueKind.String
                && Uri.TryCreate(uri.GetString(), UriKind.Absolute, out var parsed)
                && parsed.Scheme == Uri.UriSchemeHttps
                && string.IsNullOrEmpty(parsed.UserInfo)
                ? parsed
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsContentType(string? contentType, string expected) =>
        contentType is not null && (string.Equals(contentType.Trim(), expected, StringComparison.OrdinalIgnoreCase)
            || contentType.TrimStart().StartsWith(expected + ";", StringComparison.OrdinalIgnoreCase));

    private static string Trim(string key, IReadOnlyList<string> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && key.Length > prefix.Length)
            {
                return key[prefix.Length..];
            }
        }

        return key;
    }

    private static void Add(Dictionary<string, List<AzureAppConfigurationSetting>> index, string key, AzureAppConfigurationSetting setting)
    {
        if (!index.TryGetValue(key, out var list))
        {
            index[key] = list = [];
        }

        list.Add(setting);
    }
}
