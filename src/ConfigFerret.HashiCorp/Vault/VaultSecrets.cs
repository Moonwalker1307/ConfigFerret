using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ConfigFerret.Shared;

namespace ConfigFerret.HashiCorp;

/// <summary>
/// A snapshot of the secrets in a HashiCorp Vault KV mount: their names, KV version 2 metadata and, only when asked,
/// their data. Load it with <see cref="LoadAsync(VaultOptions, CancellationToken)"/>, or build it from secrets you
/// already have, then pass it to <see cref="HashiCorpTracerExtensions.WithVault"/>.
/// </summary>
public sealed class VaultSecrets
{
    // A key longer than this is skipped rather than sent back in a URL, so a hostile server cannot make one too long.
    private const int MaxKeyLength = 1024;

    // A secret with more keys than this has the rest skipped.
    private const int MaxKeysPerSecret = 1000;

    private readonly Dictionary<string, VaultSecret> _byPath = new(StringComparer.Ordinal);

    /// <summary>Creates a snapshot from secrets you already have.</summary>
    /// <param name="secrets">The secrets. Later ones with a path already seen are ignored.</param>
    /// <param name="mount">The KV mount they are in.</param>
    /// <param name="location">Where the mount is, shown on each step, for example <c>vault.example.com:8200</c>.</param>
    /// <param name="warnings">Problems found while loading.</param>
    public VaultSecrets(IEnumerable<VaultSecret> secrets, string mount = "secret", string? location = null, IEnumerable<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentException.ThrowIfNullOrWhiteSpace(mount);
        Mount = mount.Trim('/');
        Location = string.IsNullOrWhiteSpace(location) ? "HashiCorp Vault" : location;
        var list = new List<VaultSecret>();
        foreach (var secret in secrets)
        {
            ArgumentNullException.ThrowIfNull(secret);
            var path = Normalize(secret.Path);
            if (path.Length > 0 && _byPath.TryAdd(path, secret))
            {
                list.Add(secret);
            }
        }

        Secrets = list;
        Warnings = [.. warnings ?? []];
    }

    /// <summary>The KV mount, for example <c>secret</c>.</summary>
    public string Mount { get; }

    /// <summary>Where the mount is: the Vault address and namespace, shown as each step's location.</summary>
    public string Location { get; }

    /// <summary>Every secret, in the order it was found.</summary>
    public IReadOnlyList<VaultSecret> Secrets { get; }

    /// <summary>Problems found while loading, such as a path the token could not list.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Returns the secret at <paramref name="path"/> inside the mount, or null.</summary>
    public VaultSecret? Find(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return _byPath.GetValueOrDefault(Normalize(path));
    }

    /// <summary>Reads the secrets from the Vault HTTP API.</summary>
    /// <exception cref="ArgumentException">An option is missing or not valid.</exception>
    /// <exception cref="HttpRequestException">Vault could not be reached, or answered with an error other than 403 or 404.</exception>
    public static async Task<VaultSecrets> LoadAsync(VaultOptions options, CancellationToken cancellationToken = default)
    {
        using var client = JsonApi.CreateClient();
        return await LoadAsync(options, client, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the secrets from the Vault HTTP API with your own <see cref="HttpClient"/> (for a proxy or a test handler).
    /// The client should not follow redirects, so the token never reaches another host.
    /// </summary>
    /// <exception cref="ArgumentException">An option is missing or not valid.</exception>
    /// <exception cref="HttpRequestException">Vault could not be reached, or answered with an error other than 403 or 404.</exception>
    public static async Task<VaultSecrets> LoadAsync(VaultOptions options, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        var address = JsonApi.RequireSafeBaseAddress(options.Address, options.AllowUnencryptedHttp, nameof(options.Address));
        var mount = Normalize(options.Mount ?? string.Empty);
        if (mount.Length == 0 || !ValidPath(mount))
        {
            throw new ArgumentException($"{nameof(options.Mount)} must be a mount path such as 'secret'.", nameof(options));
        }

        if (options.KvVersion is not (null or 1 or 2))
        {
            throw new ArgumentException($"{nameof(options.KvVersion)} must be 1, 2 or null to detect it.", nameof(options));
        }

        if (options.MaxDepth < 0 || options.MaxSecrets < 1 || options.MaxRequests < 1)
        {
            throw new ArgumentException($"{nameof(options.MaxDepth)} must be at least 0, and {nameof(options.MaxSecrets)} and {nameof(options.MaxRequests)} at least 1.", nameof(options));
        }

        RequireHeaderSafe(options.Token, nameof(options.Token));
        RequireHeaderSafe(options.Namespace, nameof(options.Namespace));

        var location = string.IsNullOrWhiteSpace(options.Namespace) ? address.Authority : $"{address.Authority} namespace {options.Namespace.Trim()}";
        var loader = new Loader(httpClient, new Uri(address, "v1/"), options, mount);
        await loader.RunAsync(cancellationToken).ConfigureAwait(false);
        return new VaultSecrets(loader.Secrets, mount, location, loader.Warnings);
    }

    internal static string Normalize(string path) =>
        string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool ValidPath(string path) => path.Length == 0 || path.Split('/').All(ValidSegment);

    private static bool ValidSegment(string segment) =>
        segment.Length is > 0 and <= MaxKeyLength && segment is not "." and not ".." && !segment.Contains('/', StringComparison.Ordinal) && !HasLoneSurrogate(segment);

    // Uri.EscapeDataString cannot encode half of a surrogate pair.
    private static bool HasLoneSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static string Escape(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

    private static void RequireHeaderSafe(string? value, string name)
    {
        if (value is not null && value.Any(c => char.IsControl(c) || c > '\x7e'))
        {
            // Never include the value: it may be the token.
            throw new ArgumentException($"{name} holds characters that cannot be sent in an HTTP header.", name);
        }
    }

    private sealed class Loader(HttpClient client, Uri api, VaultOptions options, string mount)
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly HashSet<string> _listed = new(StringComparer.Ordinal);
        private int _requests;
        private int _version;
        private bool _stopped;
        private bool _depthWarned;
        private int _skippedKeys;

        public List<VaultSecret> Secrets { get; } = [];

        public List<string> Warnings { get; } = [];

        private string MountEscaped => Escape(mount);

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            _version = options.KvVersion ?? await DetectVersionAsync(cancellationToken).ConfigureAwait(false);
            var roots = options.Paths.Count == 0 ? [string.Empty] : options.Paths.Select(p => p ?? string.Empty).ToList();
            foreach (var root in roots)
            {
                var path = Normalize(root);
                if (!ValidPath(path))
                {
                    Warnings.Add($"Skipped HashiCorp Vault path '{root}': it is not a valid path.");
                    continue;
                }

                await ReadRootAsync(path, cancellationToken).ConfigureAwait(false);
            }

            if (_skippedKeys > 0)
            {
                Warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Skipped {_skippedKeys} HashiCorp Vault keys whose names are empty, too long or not a single path segment."));
            }
        }

        private async Task<int> DetectVersionAsync(CancellationToken cancellationToken)
        {
            foreach (var endpoint in new[] { "sys/mounts/", "sys/internal/ui/mounts/" })
            {
                using var doc = await GetAsync(endpoint + MountEscaped, null, cancellationToken).ConfigureAwait(false);
                if (doc is null)
                {
                    continue;
                }

                var root = doc.RootElement;
                var info = JsonRead.Object(root, "data") is { } data && (JsonRead.Property(data, "type") is not null || JsonRead.Property(data, "options") is not null) ? data : root;
                var type = JsonRead.String(JsonRead.Property(info, "type"));
                if (type is null && JsonRead.Property(info, "options") is null)
                {
                    continue;
                }

                if (type is not null and not "kv" and not "generic")
                {
                    Warnings.Add($"HashiCorp Vault mount {mount} is a '{type}' secrets engine, not KV, so it may not list or read like one.");
                }

                var version = JsonRead.Object(info, "options") is { } o ? JsonRead.String(JsonRead.Property(o, "version")) : null;
                switch (version)
                {
                    case null or "" or "1":
                        return 1;
                    case "2":
                        return 2;
                    default:
                        Warnings.Add($"HashiCorp Vault mount {mount} reports KV version '{version}', which is not known; reading it as version 2. Set KvVersion to choose.");
                        return 2;
                }
            }

            if (!_stopped)
            {
                Warnings.Add($"Could not read the KV version of HashiCorp Vault mount {mount} from sys/mounts, so version 2 is assumed. Set KvVersion to choose, or allow read on sys/mounts/{mount}.");
            }

            return 2;
        }

        private async Task ReadRootAsync(string root, CancellationToken cancellationToken)
        {
            if (root.Length > 0 && (_version == 2 || options.IncludeValues))
            {
                await ReadSecretAsync(root, string.Empty, cancellationToken).ConfigureAwait(false);
            }

            var isFolder = await WalkAsync(root, cancellationToken).ConfigureAwait(false);

            // KV version 1 cannot tell a secret from nothing without reading its values, so a path that is not a folder
            // is taken to be a secret.
            if (root.Length > 0 && !isFolder && _version == 1 && !options.IncludeValues && !_stopped)
            {
                Add(new VaultSecret(root, string.Empty));
            }
        }

        // Lists root and, when recursive, the folders under it, breadth first. Returns true when root is a folder.
        private async Task<bool> WalkAsync(string root, CancellationToken cancellationToken)
        {
            var pending = new Queue<(string Folder, int Depth)>();
            pending.Enqueue((root, 0));
            var rootIsFolder = false;
            while (pending.Count > 0 && !_stopped)
            {
                var (folder, depth) = pending.Dequeue();
                if (!_listed.Add(folder))
                {
                    rootIsFolder |= folder == root;
                    continue;
                }

                var keys = await ListAsync(folder, cancellationToken).ConfigureAwait(false);
                if (keys is null)
                {
                    continue;
                }

                rootIsFolder |= folder == root;
                foreach (var key in keys)
                {
                    if (_stopped)
                    {
                        break;
                    }

                    var isFolder = key.EndsWith('/');
                    var name = isFolder ? key[..^1] : key;
                    if (!ValidSegment(name))
                    {
                        _skippedKeys++;
                        continue;
                    }

                    var path = Join(folder, name);
                    if (!isFolder)
                    {
                        await ReadSecretAsync(path, Relative(root, path), cancellationToken).ConfigureAwait(false);
                    }
                    else if (options.Recursive)
                    {
                        if (depth + 1 > options.MaxDepth)
                        {
                            if (!_depthWarned)
                            {
                                _depthWarned = true;
                                Warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Did not list HashiCorp Vault folders more than {options.MaxDepth} levels below {Describe(root)} (MaxDepth), for example {path}/."));
                            }
                        }
                        else
                        {
                            pending.Enqueue((path, depth + 1));
                        }
                    }
                }
            }

            return rootIsFolder;
        }

        private async Task<List<string>?> ListAsync(string folder, CancellationToken cancellationToken)
        {
            var prefix = _version == 2 ? $"{MountEscaped}/metadata/" : $"{MountEscaped}/";
            var relative = folder.Length == 0 ? $"{prefix}?list=true" : $"{prefix}{Escape(folder)}/?list=true";
            using var doc = await GetAsync(relative, $"listing {Describe(folder)}", cancellationToken).ConfigureAwait(false);
            if (doc is null)
            {
                return null;
            }

            if (JsonRead.Object(doc.RootElement, "data") is not { } data || JsonRead.Property(data, "keys") is not { ValueKind: JsonValueKind.Array } keys)
            {
                Warnings.Add($"HashiCorp Vault returned an unexpected response listing {Describe(folder)}: no data.keys array.");
                return null;
            }

            var result = new List<string>();
            foreach (var item in keys.EnumerateArray())
            {
                if (JsonRead.String(item) is { } key)
                {
                    result.Add(key);
                }
                else
                {
                    _skippedKeys++;
                }
            }

            return result;
        }

        // Reads one secret's metadata (version 2) and, when asked, its data, and adds the secret when it exists.
        private async Task ReadSecretAsync(string path, string relative, CancellationToken cancellationToken)
        {
            if (_seen.Contains(path))
            {
                return;
            }

            if (Secrets.Count >= options.MaxSecrets)
            {
                Stop(string.Create(CultureInfo.InvariantCulture, $"Stopped reading HashiCorp Vault after {options.MaxSecrets} secrets (MaxSecrets)."));
                return;
            }

            long? version = null;
            DateTimeOffset? updated = null;
            var deleted = false;
            var found = false;
            if (_version == 2)
            {
                using var meta = await GetAsync($"{MountEscaped}/metadata/{Escape(path)}", $"the metadata of {path}", cancellationToken).ConfigureAwait(false);
                if (meta is not null && JsonRead.Object(meta.RootElement, "data") is { } data)
                {
                    found = true;
                    version = JsonRead.Number(JsonRead.Property(data, "current_version"));
                    updated = JsonRead.Date(JsonRead.Property(data, "updated_time"));
                    if (version is { } v
                        && JsonRead.Object(data, "versions") is { } versions
                        && JsonRead.Object(versions, v.ToString(CultureInfo.InvariantCulture)) is { } current)
                    {
                        deleted = JsonRead.Property(current, "destroyed") is { ValueKind: JsonValueKind.True }
                            || JsonRead.String(JsonRead.Property(current, "deletion_time")) is { Length: > 0 };
                    }
                }
                else if (!options.IncludeValues)
                {
                    return;
                }
            }
            else if (!options.IncludeValues)
            {
                // A key listed under a KV version 1 folder is a secret; reading it would return its values.
                Add(new VaultSecret(path, relative));
                return;
            }

            Dictionary<string, string?>? values = null;
            if (options.IncludeValues && !_stopped)
            {
                var relativeUri = _version == 2 ? $"{MountEscaped}/data/{Escape(path)}" : $"{MountEscaped}/{Escape(path)}";
                using var doc = await GetAsync(relativeUri, $"the data of {path}", cancellationToken).ConfigureAwait(false);
                var data = doc is null ? null : JsonRead.Object(doc.RootElement, "data");
                var secretData = _version == 2 && data is { } d ? JsonRead.Object(d, "data") : data;
                if (secretData is { } sd)
                {
                    values = ReadValues(sd, path);
                }

                if (_version == 2 && data is { } wrapper && JsonRead.Object(wrapper, "metadata") is { } metadata)
                {
                    version ??= JsonRead.Number(JsonRead.Property(metadata, "version"));
                    updated ??= JsonRead.Date(JsonRead.Property(metadata, "created_time"));
                }

                found |= values is not null;
            }

            if (!found)
            {
                return;
            }

            Add(new VaultSecret(path, relative, values, version, updated, deleted));
            return;
        }

        private Dictionary<string, string?> ReadValues(JsonElement data, string path)
        {
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (name, value) in JsonRead.Properties(data))
            {
                if (values.Count >= MaxKeysPerSecret)
                {
                    Warnings.Add(string.Create(CultureInfo.InvariantCulture, $"HashiCorp Vault secret {path} has more than {MaxKeysPerSecret} keys; the rest were skipped."));
                    break;
                }

                if (name.Length > 0)
                {
                    values[name] = JsonRead.Text(value);
                }
            }

            return values;
        }

        private void Add(VaultSecret secret)
        {
            if (_seen.Add(secret.Path))
            {
                Secrets.Add(secret);
            }
        }

        private void Stop(string warning)
        {
            if (!_stopped)
            {
                _stopped = true;
                Warnings.Add(warning);
            }
        }

        // Returns null for 404, and for 403 too after adding a warning (unless what is null). Throws for other errors.
        private async Task<JsonDocument?> GetAsync(string relative, string? what, CancellationToken cancellationToken)
        {
            if (_requests >= options.MaxRequests)
            {
                Stop(string.Create(CultureInfo.InvariantCulture, $"Stopped reading HashiCorp Vault after {options.MaxRequests} requests (MaxRequests)."));
                return null;
            }

            _requests++;
            try
            {
                return await JsonApi.GetAsync(client, new Uri(api, relative), AddHeaders, "HashiCorp Vault", cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound
                || (what is null && ex.StatusCode is { } status && (int)status is >= 400 and < 500))
            {
                return null;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                Warnings.Add($"Skipped {what} in HashiCorp Vault: {ex.Message}");
                return null;
            }
        }

        private string Describe(string folder) => folder.Length == 0 ? $"mount {mount}" : $"{folder}/";

        private static string Relative(string root, string path) => root.Length == 0 ? path : path[(root.Length + 1)..];

        private void AddHeaders(HttpRequestHeaders headers)
        {
            headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(options.Token))
            {
                headers.Add("X-Vault-Token", options.Token);
            }

            if (!string.IsNullOrWhiteSpace(options.Namespace))
            {
                headers.Add("X-Vault-Namespace", options.Namespace.Trim());
            }

            // Vault rejects browser-style requests without it on some setups; it is harmless otherwise.
            headers.Add("X-Vault-Request", "true");
        }
    }
}
