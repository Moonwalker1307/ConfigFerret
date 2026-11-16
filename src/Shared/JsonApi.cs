using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ConfigFerret.Shared;

/// <summary>
/// Small GET-and-parse helper shared by the ConfigFerret source packages and the desktop and web app (compiled into each).
/// Credentials go only into request headers, never into URLs, exceptions or logs.
/// </summary>
internal static class JsonApi
{
    /// <summary>
    /// The largest response body that is read. Paged lists send at most a few MB a page, but Octopus projects/all and a
    /// recursive Consul read are not paged, and a Vault secret can be as large as Vault's 32 MB request limit, so this
    /// leaves room for those and still keeps a broken or hostile server from filling memory.
    /// </summary>
    public const int MaxResponseBytes = 64 * 1024 * 1024;

    private const int MaxErrorMessageLength = 200;

    // An error answer is read only for the reason it gives, which is short, so only its start is read.
    private const int MaxErrorBodyBytes = 64 * 1024;

    // A reason that repeats this many characters of the credential in a row, or all of a shorter one, is not shown.
    private const int CredentialPartLength = 4;

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

    public static Task<JsonDocument> GetAsync(
        HttpClient client,
        Uri uri,
        Action<HttpRequestHeaders> addHeaders,
        string service,
        CancellationToken cancellationToken) =>
        GetAsync(client, uri, addHeaders, service, null, cancellationToken);

    /// <summary>
    /// Like <see cref="GetAsync(HttpClient, Uri, Action{HttpRequestHeaders}, string, CancellationToken)"/>, and calls
    /// <paramref name="onSuccess"/> with a successful response before its body is parsed, for example to read a paging
    /// header or to reject a sign-in page sent with a success status. It may throw <see cref="HttpRequestException"/>.
    /// </summary>
    public static Task<JsonDocument> GetAsync(
        HttpClient client,
        Uri uri,
        Action<HttpRequestHeaders> addHeaders,
        string service,
        Action<HttpResponseMessage>? onSuccess,
        CancellationToken cancellationToken) =>
        GetAsync(client, uri, addHeaders, service, onSuccess, null, cancellationToken);

    /// <summary>
    /// Like <see cref="GetAsync(HttpClient, Uri, Action{HttpRequestHeaders}, string, Action{HttpResponseMessage}, CancellationToken)"/>,
    /// and leaves the server's reason out of an error when it repeats <paramref name="credential"/> or four of its
    /// characters in a row, ignoring case.
    /// </summary>
    public static async Task<JsonDocument> GetAsync(
        HttpClient client,
        Uri uri,
        Action<HttpRequestHeaders> addHeaders,
        string service,
        Action<HttpResponseMessage>? onSuccess,
        string? credential,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ConfigFerret", typeof(JsonApi).Assembly.GetName().Version?.ToString(3) ?? "0"));
        addHeaders(request.Headers);

        // HttpClient.Timeout stops at the headers when they are read first, so the same limit is applied to the body here.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(client.Timeout);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var start = await ReadStartAsync(response.Content, MaxErrorBodyBytes, timeout.Token).ConfigureAwait(false);
                throw new HttpRequestException(DescribeFailure(response.StatusCode, uri, service, start, credential), null, response.StatusCode);
            }

            var body = await ReadBodyAsync(response.Content, uri, service, timeout.Token).ConfigureAwait(false);
            onSuccess?.Invoke(response);
            return Parse(body, uri, service);
        }
        catch (OperationCanceledException ex) when (IsTimeout(ex, timeout, cancellationToken))
        {
            throw new HttpRequestException(
                string.Create(CultureInfo.InvariantCulture, $"{service} did not send a complete response for {uri.AbsolutePath} within {client.Timeout.TotalSeconds} seconds."),
                ex);
        }
    }

    /// <summary>
    /// Parses the body <paramref name="service"/> returned for <paramref name="uri"/>, and throws
    /// <see cref="HttpRequestException"/> when it is not JSON. For callers that send their requests themselves.
    /// </summary>
    public static JsonDocument Parse(string body, Uri uri, string service)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException($"{service} returned a response that is not JSON for {uri.AbsolutePath}.", ex);
        }
    }

    // Our own timer, or HttpClient's, which reports its Timeout as a cancellation with a TimeoutException inside. A
    // cancellation by the caller's token is left as it is.
    private static bool IsTimeout(OperationCanceledException ex, CancellationTokenSource timeout, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && (timeout.IsCancellationRequested || ex.InnerException is TimeoutException);

    /// <summary>
    /// Reads <paramref name="content"/> like ReadAsStringAsync, but stops with <see cref="HttpRequestException"/> at
    /// <see cref="MaxResponseBytes"/> instead of buffering whatever the server sends, and reads a charset .NET does not
    /// know as UTF-8 instead of failing. For callers that send their requests themselves.
    /// </summary>
    public static async Task<string> ReadBodyAsync(HttpContent content, Uri uri, string service, CancellationToken cancellationToken)
    {
        var tooLarge = $"{service} returned a response for {uri.AbsolutePath} that is larger than {MaxResponseBytes / (1024 * 1024)} MB.";
        if (content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new HttpRequestException(tooLarge);
        }

        using var buffer = new MemoryStream();
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    throw new HttpRequestException(tooLarge);
                }

                buffer.Write(chunk, 0, read);
            }
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, EncodingOf(content), detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    // At most the first maxBytes of content, whatever length it declares, read as ReadBodyAsync reads it.
    private static async Task<string> ReadStartAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        var start = new byte[maxBytes];
        var length = 0;
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            int read;
            while (length < maxBytes && (read = await stream.ReadAsync(start.AsMemory(length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                length += read;
            }
        }

        using var reader = new StreamReader(new MemoryStream(start, 0, length), EncodingOf(content), detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    // The charset of the Content-Type, else UTF-8, as in ReadAsStringAsync. A byte order mark is honoured too.
    private static Encoding EncodingOf(HttpContent content)
    {
        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        try
        {
            return string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
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

    private static string DescribeFailure(HttpStatusCode status, Uri uri, string service, string body, string? credential)
    {
        var message = $"{service} returned {(int)status} {status} for {uri.AbsolutePath}.";
        if ((int)status is >= 300 and < 400)
        {
            return message + " Redirects are not followed, so the credential is never sent to another address; use the final URL.";
        }

        var detail = ReadErrorMessage(body);
        return detail is null || (!string.IsNullOrEmpty(credential) && HoldsPartOf(detail, credential)) ? message : $"{message} {detail}";
    }

    // Whether text repeats credential, or a run of CredentialPartLength of its characters, in any case.
    private static bool HoldsPartOf(string text, string credential)
    {
        var length = Math.Min(CredentialPartLength, credential.Length);
        for (var i = 0; i + length <= credential.Length; i++)
        {
            if (text.AsSpan().Contains(credential.AsSpan(i, length), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // GitHub returns {"message": ...}, Octopus returns {"ErrorMessage": ...}, Google Cloud {"error": {"message": ...}},
    // GitLab and other OAuth servers {"error": "invalid_token", "error_description": ...}. The body comes from the server,
    // so what is shown is cut short and has no control or format characters.
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
                    return Shown(value.GetString());
                }
            }

            // HashiCorp Vault returns {"errors": ["permission denied"]}.
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                var texts = errors.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()).Take(3).ToList();
                return texts.Count == 0 ? null : Shown(string.Join("; ", texts));
            }

            // Google Cloud APIs return {"error": {"code": 403, "message": "...", "status": "PERMISSION_DENIED"}}.
            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var errorMessage)
                && errorMessage.ValueKind == JsonValueKind.String)
            {
                return Shown(errorMessage.GetString());
            }

            // OAuth servers, GitLab among them, return {"error": "insufficient_scope", "error_description": "..."}.
            var code = doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? Shown(e.GetString()) : null;
            var description = doc.RootElement.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            if (code is not null || description is not null)
            {
                return code is null ? Shown(description) : Shown(Shown(description) is null ? code : $"{code}: {description}");
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

    // The text without control or format characters (which could rewrite or hide what a terminal or log shows), cut
    // short without splitting a surrogate pair, or null when nothing is left.
    private static string? Shown(string? text)
    {
        var clean = string.Concat((text ?? string.Empty).Where(c => !char.IsControl(c)
            && CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))).Trim();
        if (clean.Length == 0)
        {
            return null;
        }

        if (clean.Length <= MaxErrorMessageLength)
        {
            return clean;
        }

        var cut = char.IsHighSurrogate(clean[MaxErrorMessageLength - 1]) ? MaxErrorMessageLength - 1 : MaxErrorMessageLength;
        return clean[..cut] + "...";
    }
}
