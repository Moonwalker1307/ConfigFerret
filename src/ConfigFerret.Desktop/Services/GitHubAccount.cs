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
/// header and never appears in an exception message.
/// </summary>
public sealed class GitHubAccount
{
    /// <summary>The largest repository download accepted, unpacked.</summary>
    public const long MaxDownloadBytes = 512L * 1024 * 1024;

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
            using var document = await GetJsonAsync($"repos/{Escape(fullName)}/environments?per_page=100", cancellationToken).ConfigureAwait(false);
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
        using var request = Request($"repos/{Escape(fullName)}/tarball/{Uri.EscapeDataString(gitRef)}");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, fullName);
        var target = Directory.CreateDirectory(Path.Join(parentDirectory, $"{fullName.Replace('/', '_')}-{Guid.NewGuid():N}")).FullName;
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await ExtractAsync(body, target, cancellationToken).ConfigureAwait(false);
        return target;
    }

    /// <summary>Extracts a GitHub tarball (.tar.gz with one top folder) into <paramref name="target"/>, dropping the top folder.</summary>
    internal static async Task ExtractAsync(Stream gzip, string target, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        long written = 0;
        await using var unzipped = new GZipStream(gzip, CompressionMode.Decompress);
        await using var reader = new TarReader(unzipped);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
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
            if (written > MaxDownloadBytes)
            {
                throw new InvalidOperationException($"The repository is larger than {MaxDownloadBytes / 1024 / 1024} MB unpacked. Clone it and open the folder instead.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var file = File.Create(path);
            await entry.DataStream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var request = Request(path);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, path);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage Request(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_apiBase, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("ConfigFerret-Desktop");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

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

    private static string Escape(string fullName) => string.Join('/', fullName.Split('/').Select(Uri.EscapeDataString));

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
