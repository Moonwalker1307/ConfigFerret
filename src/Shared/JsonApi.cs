using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ConfigFerret.Shared;

/// <summary>
/// Small GET-and-parse helper shared by the ConfigFerret.GitHub and ConfigFerret.Octopus packages (compiled into each).
/// Credentials go only into request headers, never into URLs, exceptions or logs.
/// </summary>
internal static class JsonApi
{
    private const int MaxErrorMessageLength = 200;

    /// <summary>Checks that <paramref name="uri"/> is absolute and uses HTTPS, or plain HTTP only when allowed or on loopback.</summary>
    public static Uri RequireSafeBaseAddress(Uri? uri, bool allowUnencryptedHttp, string optionName)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            throw new ArgumentException($"{optionName} must be an absolute URL.", optionName);
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && (allowUnencryptedHttp || uri.IsLoopback)))
        {
            throw new ArgumentException(
                $"{optionName} must use https so the credential is not sent in clear text. Turn on AllowUnencryptedHttp to use http anyway.",
                optionName);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException($"{optionName} must not hold credentials. Pass them in the options instead.", optionName);
        }

        // Keep any path prefix (for example https://example.com/octopus/) when relative paths are resolved against it.
        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }

    /// <summary>Creates a client that does not follow redirects, so credential headers never reach another host.</summary>
    public static HttpClient CreateClient()
    {
#pragma warning disable CA2000 // The client owns and disposes the handler.
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
#pragma warning restore CA2000
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(100) };
    }

    public static async Task<JsonDocument> GetAsync(
        HttpClient client,
        Uri uri,
        Action<HttpRequestHeaders> addHeaders,
        string service,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ConfigFerret", typeof(JsonApi).Assembly.GetName().Version?.ToString(3) ?? "0"));
        addHeaders(request.Headers);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(DescribeFailure(response.StatusCode, uri, service, body), null, response.StatusCode);
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException($"{service} returned a response that is not JSON for {uri.AbsolutePath}.", ex);
        }
    }

    private static string DescribeFailure(HttpStatusCode status, Uri uri, string service, string body)
    {
        var message = $"{service} returned {(int)status} {status} for {uri.AbsolutePath}.";
        if ((int)status is >= 300 and < 400)
        {
            return message + " Redirects are not followed, so the credential is never sent to another address; use the final URL.";
        }

        var detail = ReadErrorMessage(body);
        return detail is null ? message : $"{message} {detail}";
    }

    // GitHub returns {"message": ...}, Octopus returns {"ErrorMessage": ...}. Neither echoes the credential back.
    private static string? ReadErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var name in (ReadOnlySpan<string>)["message", "ErrorMessage"])
            {
                if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString() ?? string.Empty;
                    return text.Length > MaxErrorMessageLength ? text[..MaxErrorMessageLength] + "..." : text;
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
