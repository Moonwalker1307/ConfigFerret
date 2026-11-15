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

    private static readonly string[] ErrorMessageFields = ["message", "ErrorMessage"];

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

    /// <summary>
    /// Throws <see cref="HttpRequestException"/> when a string or property name in <paramref name="doc"/> holds a lone
    /// surrogate escape such as <c>"\ud800"</c>. JsonElement throws InvalidOperationException from TryGetProperty, Name
    /// and GetString on one, so loaders that read with those call this first instead of guarding every read.
    /// </summary>
    public static void RequireValidText(JsonDocument doc, string service, Uri uri)
    {
        try
        {
            RequireValidText(doc.RootElement, depth: 0);
        }
        catch (InvalidOperationException ex)
        {
            throw new HttpRequestException($"{service} returned a response for {uri.AbsolutePath} whose text is not valid UTF-16.", ex);
        }
    }

    private static void RequireValidText(JsonElement element, int depth)
    {
        // JsonDocument already refuses nesting deeper than 64, so this recursion is bounded.
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    _ = property.Name;
                    RequireValidText(property.Value, depth + 1);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    RequireValidText(item, depth + 1);
                }

                break;
            case JsonValueKind.String:
                _ = element.GetString();
                break;
            default:
                break;
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

            foreach (var name in ErrorMessageFields)
            {
                if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return Shorten(value.GetString() ?? string.Empty);
                }
            }

            // HashiCorp Vault returns {"errors": ["permission denied"]}.
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                var texts = errors.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()).Take(3).ToList();
                return texts.Count == 0 ? null : Shorten(string.Join("; ", texts));
            }
        }
        catch (InvalidOperationException)
        {
            // A string that is not valid UTF-16 (a lone surrogate escape): report the status alone.
            return null;
        }
        catch (JsonException)
        {
            // Not JSON (for example an HTML error page from a proxy): report the status alone.
            return null;
        }

        return null;
    }

    private static string Shorten(string text) => text.Length > MaxErrorMessageLength ? text[..MaxErrorMessageLength] + "..." : text;
}
