using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ConfigFerret.Desktop.Services;

/// <summary>A repository the signed-in GitHub user can read.</summary>
/// <param name="FullName">owner/name.</param>
/// <param name="Description">The repository description, or empty.</param>
/// <param name="DefaultBranch">The default branch, for example main.</param>
/// <param name="IsPrivate">True for a private or internal repository.</param>
/// <param name="Language">The main language, or empty.</param>
/// <param name="PushedAt">When the repository was last pushed to.</param>
public sealed record RepositoryInfo(string FullName, string Description, string DefaultBranch, bool IsPrivate, string Language, DateTimeOffset? PushedAt)
{
    /// <summary>The owner (user or organization).</summary>
    public string Owner => FullName[..FullName.IndexOf('/', StringComparison.Ordinal)];

    /// <summary>The repository name.</summary>
    public string Name => FullName[(FullName.IndexOf('/', StringComparison.Ordinal) + 1)..];
}

/// <summary>
/// The GitHub REST API calls the repository overview needs: who is signed in, which repositories they can read, the
/// repository's deployment environments, and a download of its files. The token is sent only in the Authorization
/// header, only to the API address's own scheme, host and port, and never appears in an exception message. Redirects
/// are followed here rather than by the HTTP client, so a redirect to another host (such as the tarball download
/// server) never carries the token.
/// </summary>
public sealed class GitHubAccount
{
    /// <summary>The largest repository download accepted, unpacked.</summary>
    public const long MaxDownloadBytes = 512L * 1024 * 1024;

    /// <summary>The most entries (files, folders, links) a repository download may hold.</summary>
    public const int MaxDownloadEntries = 100_000;

    // Room for the tar headers and padding around the file contents: a pax entry takes at most a few blocks.
    private const long TarOverheadPerEntry = 4096;
    private const int MaxRedirects = 5;
    private const int MaxRepositories = 2000;
    private readonly HttpClient _http;
    private readonly Uri _apiBase;
    private readonly string _token;

    /// <summary>Creates a client.</summary>
    /// <param name="http">The HTTP client. Not disposed by this class.</param>
    /// <param name="apiBase">The API address, https://api.github.com/ or https://HOST/api/v3/ for GitHub Enterprise Server.</param>
    /// <param name="token">A token that can read the repositories (classic with repo, or fine-grained with Contents, Variables and Environments read).</param>
    public GitHubAccount(HttpClient http, Uri apiBase, string token)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentNullException.ThrowIfNull(apiBase);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (apiBase.Scheme != Uri.UriSchemeHttps && !apiBase.IsLoopback)
        {
            throw new ArgumentException("The GitHub API address must use https, so the token is not sent in clear text.", nameof(apiBase));
        }

        _apiBase = apiBase.AbsoluteUri.EndsWith('/') ? apiBase : new Uri(apiBase.AbsoluteUri + "/");
        _token = token;
    }

    /// <summary>The default API address.</summary>
    public static Uri DefaultApiBase { get; } = new("https://api.github.com/");

    /// <summary>The signed-in user's login.</summary>
    public async Task<string> GetLoginAsync(CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync("user", cancellationToken).ConfigureAwait(false);
        return document.RootElement.TryGetProperty("login", out var login) && login.ValueKind == JsonValueKind.String
            ? login.GetString()!
            : throw new HttpRequestException("GitHub did not say who the token belongs to.");
    }

    /// <summary>Every repository the user owns, collaborates on or can read through an organization, most recently pushed first.</summary>
    public async Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<RepositoryInfo>();
        for (var page = 1; result.Count < MaxRepositories; page++)
        {
            using var document = await GetJsonAsync(
                $"user/repos?per_page=100&page={page}&sort=pushed&affiliation=owner,collaborator,organization_member",
                cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var count = 0;
            foreach (var repo in document.RootElement.EnumerateArray())
            {
                count++;
                if (Text(repo, "full_name") is not { } fullName || !fullName.Contains('/', StringComparison.Ordinal))
                {
                    continue;
                }

                DateTimeOffset? pushed = repo.TryGetProperty("pushed_at", out var p) && p.ValueKind == JsonValueKind.String && p.TryGetDateTimeOffset(out var when) ? when : null;
                result.Add(new RepositoryInfo(
                    fullName,
                    Text(repo, "description") ?? string.Empty,
                    Text(repo, "default_branch") ?? "main",
                    repo.TryGetProperty("private", out var priv) && priv.ValueKind == JsonValueKind.True,
                    Text(repo, "language") ?? string.Empty,
                    pushed));
            }

            if (count < 100)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>The repository's deployment environments, by name. Empty when it has none or the token can't see them.</summary>
    public async Task<IReadOnlyList<string>> ListEnvironmentsAsync(string fullName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = await GetJsonAsync($"repos/{RepositoryPath(fullName)}/environments?per_page=100", cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("environments", out var environments) || environments.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return environments.EnumerateArray().Select(e => Text(e, "name")).OfType<string>().ToList();
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return [];
        }
    }

    /// <summary>
    /// Downloads the repository's files at <paramref name="gitRef"/> into a new folder under <paramref name="parentDirectory"/>
    /// and returns it. Only regular files are written: links, devices and paths that would leave the folder are skipped.
    /// </summary>
    public async Task<string> DownloadAsync(string fullName, string gitRef, string parentDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitRef);
        var repository = RepositoryPath(fullName);
        if (gitRef is "." or "..")
        {
            throw new ArgumentException($"'{gitRef}' is not a branch, tag or commit.", nameof(gitRef));
        }

        using var response = await SendAsync($"repos/{repository}/tarball/{Uri.EscapeDataString(gitRef)}", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, fullName);
        var folder = Path.Join(parentDirectory, $"{repository.Replace('/', '_')}-{Guid.NewGuid():N}");

        // Private repositories: only the signed-in user may read the files, even under a shared temp folder.
        var target = (OperatingSystem.IsWindows()
            ? Directory.CreateDirectory(folder)
            : Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)).FullName;
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await ExtractAsync(body, target, cancellationToken).ConfigureAwait(false);
        return target;
    }

    /// <summary>
    /// Extracts a GitHub tarball (.tar.gz with one top folder) into <paramref name="target"/>, dropping the top folder.
    /// Stops with an error once the unpacked files pass <paramref name="maxBytes"/>, the archive holds more than
    /// <paramref name="maxEntries"/> entries, or more is unpacked than those could need, skipped entries included.
    /// </summary>
    /// <exception cref="InvalidOperationException">The data is not a valid tarball, or it is too large.</exception>
    internal static async Task ExtractAsync(Stream gzip, string target, CancellationToken cancellationToken, long maxBytes = MaxDownloadBytes, int maxEntries = MaxDownloadEntries)
    {
        try
        {
            await ExtractCoreAsync(gzip, target, maxBytes, maxEntries, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            // The gzip or tar reader's way of saying the download is corrupt. The app shows InvalidOperationException messages.
            throw new InvalidOperationException($"GitHub sent a repository archive that could not be read: {ex.Message}", ex);
        }
    }

    private static async Task ExtractCoreAsync(Stream gzip, string target, long maxBytes, int maxEntries, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        var tooLarge = $"The repository is larger than {maxBytes / 1024 / 1024} MB unpacked or has more than {maxEntries} files. Clone it and open the folder instead.";
        long written = 0;
        var entries = 0;
        await using var unzipped = new GZipStream(gzip, CompressionMode.Decompress);
        await using var limited = new ReadLimitStream(unzipped, maxBytes + (maxEntries * TarOverheadPerEntry), tooLarge);
        await using var reader = new TarReader(limited);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
        {
            if (++entries > maxEntries)
            {
                throw new InvalidOperationException(tooLarge);
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                continue;
            }

            // Drop the "owner-repo-sha/" folder GitHub puts everything in.
            var name = entry.Name.Replace('\\', '/');
            var slash = name.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0 || slash == name.Length - 1)
            {
                continue;
            }

            var relative = name[(slash + 1)..];
            var path = Path.GetFullPath(Path.Join(root, relative));
            if (!path.StartsWith(root, StringComparison.Ordinal) || relative.Split('/').Any(part => part is ".." or "."))
            {
                continue;
            }

            written += entry.Length;
            if (written > maxBytes)
            {
                throw new InvalidOperationException(tooLarge);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var file = File.Create(path);
            if (entry.DataStream is { } data)
            {
                await data.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(path, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, path);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // Sends a GET and follows up to MaxRedirects redirects itself. The token goes only to the API's own origin, and a
    // redirect may not leave https (except on loopback, as the API address itself may).
    private async Task<HttpResponseMessage> SendAsync(string path, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        var uri = new Uri(_apiBase, path);
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (IsApiOrigin(uri))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            }

            request.Headers.UserAgent.ParseAdd("ConfigFerret-Desktop");
            var response = await _http.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
            {
                return response;
            }

            response.Dispose();
            var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (redirects >= MaxRedirects)
            {
                throw new HttpRequestException($"GitHub redirected more than {MaxRedirects} times.");
            }

            if (next.Scheme != Uri.UriSchemeHttps && !(next.Scheme == Uri.UriSchemeHttp && next.IsLoopback))
            {
                throw new HttpRequestException("GitHub redirected to an address that is not https. The redirect was not followed.");
            }

            uri = next;
        }
    }

    private bool IsApiOrigin(Uri uri) =>
        Uri.Compare(uri, _apiBase, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static void EnsureSuccess(HttpResponseMessage response, string what)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var reason = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "the token was refused. Check that it is valid and not expired",
            HttpStatusCode.Forbidden => "the token is not allowed to read this, or the rate limit was hit",
            HttpStatusCode.NotFound => "it was not found, or the token cannot see it",
            _ => $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}",
        };
        throw new HttpRequestException($"Could not read {what} from GitHub: {reason}.", null, response.StatusCode);
    }

    // owner/name as GitHub allows them: letters, digits, '-', '_' and '.', and never "." or "..". Anything else could
    // move the API path (repos/../user) or the download folder, so it is refused rather than escaped.
    private static string RepositoryPath(string fullName)
    {
        var parts = fullName.Split('/');
        return parts.Length == 2 && parts.All(IsNamePart)
            ? fullName
            : throw new ArgumentException($"'{fullName}' is not a repository name of the form owner/name.", nameof(fullName));
    }

    private static bool IsNamePart(string part) =>
        part.Length > 0 && part is not ("." or "..") && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>Passes reads through until <c>limit</c> bytes were read, then fails, so a decompression bomb stops early.</summary>
internal sealed class ReadLimitStream(Stream inner, long limit, string message) : Stream
{
    private long _read;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Count(int read)
    {
        _read += read;
        return _read > limit ? throw new InvalidOperationException(message) : read;
    }
}
