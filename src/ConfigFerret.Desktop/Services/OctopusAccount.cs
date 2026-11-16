using System.Net;
using System.Text.Json;

namespace ConfigFerret.Desktop.Services;

/// <summary>An Octopus project, as offered in the project picker.</summary>
/// <param name="Name">The project name.</param>
/// <param name="Slug">The project slug.</param>
public sealed record OctopusProjectInfo(string Name, string Slug)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// The Octopus calls the repository overview needs to offer choices: the space's projects and environments. Reading the
/// variables themselves is done by ConfigFerret.Octopus. The API key is sent only in the X-Octopus-ApiKey header.
/// </summary>
public sealed class OctopusAccount
{
    private readonly HttpClient _http;
    private readonly Uri _server;
    private readonly string _apiKey;
    private readonly string? _space;
    private string? _spaceId;

    /// <summary>Creates a client.</summary>
    public OctopusAccount(HttpClient http, Uri server, string apiKey, string? space)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (server.Scheme != Uri.UriSchemeHttps && !server.IsLoopback)
        {
            throw new ArgumentException("The Octopus server address must use https, so the API key is not sent in clear text.", nameof(server));
        }

        _server = server.AbsoluteUri.EndsWith('/') ? server : new Uri(server.AbsoluteUri + "/");
        _apiKey = apiKey;
        _space = string.IsNullOrWhiteSpace(space) ? null : space.Trim();
    }

    /// <summary>The projects in the space, sorted by name.</summary>
    public async Task<IReadOnlyList<OctopusProjectInfo>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var space = await SpaceIdAsync(cancellationToken).ConfigureAwait(false);
        using var document = await GetAsync($"api/{space}/projects/all", cancellationToken).ConfigureAwait(false);
        return Items(document)
            .Select(p => new OctopusProjectInfo(Text(p, "Name") ?? string.Empty, Text(p, "Slug") ?? string.Empty))
            .Where(p => p.Name.Length > 0)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The environments in the space, in Octopus's order.</summary>
    public async Task<IReadOnlyList<string>> ListEnvironmentsAsync(CancellationToken cancellationToken = default)
    {
        var space = await SpaceIdAsync(cancellationToken).ConfigureAwait(false);
        using var document = await GetAsync($"api/{space}/environments/all", cancellationToken).ConfigureAwait(false);
        return Items(document).Select(e => Text(e, "Name")).OfType<string>().ToList();
    }

    private async Task<string> SpaceIdAsync(CancellationToken cancellationToken)
    {
        if (_spaceId is not null)
        {
            return _spaceId;
        }

        using var document = await GetAsync("api/spaces/all", cancellationToken).ConfigureAwait(false);
        var spaces = Items(document).ToList();
        var match = _space is null
            ? spaces.FirstOrDefault(s => s.ValueKind == JsonValueKind.Object && s.TryGetProperty("IsDefault", out var d) && d.ValueKind == JsonValueKind.True)
            : spaces.FirstOrDefault(s => string.Equals(Text(s, "Name"), _space, StringComparison.OrdinalIgnoreCase) || string.Equals(Text(s, "Id"), _space, StringComparison.OrdinalIgnoreCase));
        _spaceId = match.ValueKind == JsonValueKind.Object && Text(match, "Id") is { } id
            ? id
            : throw new HttpRequestException(_space is null ? "Octopus has no default space. Name the space in Connections." : $"Octopus has no space called {_space}.");
        return _spaceId;
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_server, path));
        request.Headers.Add("X-Octopus-ApiKey", _apiKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var reason = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "the API key was refused"
                : $"Octopus answered {(int)response.StatusCode} {response.ReasonPhrase}";
            throw new HttpRequestException($"Could not read {path} from Octopus: {reason}.", null, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static List<JsonElement> Items(JsonDocument document) =>
        document.RootElement.ValueKind == JsonValueKind.Array ? [.. document.RootElement.EnumerateArray()] : [];

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
