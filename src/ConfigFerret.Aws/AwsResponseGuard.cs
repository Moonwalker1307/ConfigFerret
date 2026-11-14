using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;

namespace ConfigFerret.Aws;

/// <summary>
/// Checks each successful AWS response before the SDK reads it. The SDK's JSON readers loop forever, using more and
/// more memory, when a list or structure in a response is a plain value instead (for example <c>"SecretList":"x"</c>).
/// AWS itself never sends that, but a proxy, a test endpoint or a <see cref="AwsConnectionOptions.ServiceUrl"/> might, so
/// such a response is turned into an error the SDK reports instead.
/// </summary>
internal sealed class AwsResponseGuard : HttpMessageHandler
{
    // The members of the responses ConfigFerret reads that the SDK expects to be lists of structures, structures
    // (or maps), or lists. A response where one of them is anything else is refused.
    private static readonly HashSet<string> ListsOfStructures = new(StringComparer.Ordinal)
    {
        "SecretList", "Tags", "SecretValues", "Errors", "Parameters", "ExternalSecretRotationMetadata", "ReplicationStatus", "Filters",
    };

    private static readonly HashSet<string> Structures = new(StringComparer.Ordinal) { "RotationRules", "SecretVersionsToStages" };

    private static readonly HashSet<string> Lists = new(StringComparer.Ordinal) { "VersionStages", "Values" };

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 256 };

    private readonly HttpMessageInvoker _inner;

    public AwsResponseGuard(HttpMessageHandler inner, bool disposeInner) => _inner = new HttpMessageInvoker(inner, disposeInner);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await _inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return response;
        }

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length > 0 && !IsReadable(body))
        {
            response.Dispose();
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                RequestMessage = request,
                Content = new StringContent(
                    """{"__type":"InvalidResponseException","message":"The response is not the JSON this operation returns, so ConfigFerret did not read it."}""",
                    Encoding.UTF8,
                    "application/x-amz-json-1.1"),
            };
        }

        // The body was read here, so hand the SDK a fresh copy with the same headers.
        var content = new ByteArrayContent(body);
        foreach (var header in response.Content.Headers)
        {
            content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Content.Dispose();
        response.Content = content;
        return response;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>True when <paramref name="body"/> is JSON whose lists and structures have the shapes the SDK expects.</summary>
    internal static bool IsReadable(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body, ParseOptions);
            return doc.RootElement.ValueKind == JsonValueKind.Object && Check(doc.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Check(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var value = property.Value;
                    var kind = value.ValueKind;
                    if (kind != JsonValueKind.Null)
                    {
                        if (ListsOfStructures.Contains(property.Name)
                            && (kind != JsonValueKind.Array || value.EnumerateArray().Any(i => i.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))))
                        {
                            return false;
                        }

                        if ((Structures.Contains(property.Name) && kind != JsonValueKind.Object) || (Lists.Contains(property.Name) && kind != JsonValueKind.Array))
                        {
                            return false;
                        }
                    }

                    if (!Check(value))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.Array:
                return element.EnumerateArray().All(Check);
            default:
                return true;
        }
    }
}

/// <summary>Gives the SDK clients that never follow redirects and check responses with <see cref="AwsResponseGuard"/>.</summary>
internal sealed class GuardedHttpClientFactory(HttpMessageHandler? handler) : HttpClientFactory
{
    public override HttpClient CreateHttpClient(IClientConfig clientConfig)
    {
        ArgumentNullException.ThrowIfNull(clientConfig);
#pragma warning disable CA2000 // The client owns and disposes the guard, and the guard the handler it created.
        var guard = handler is null
            ? new AwsResponseGuard(new HttpClientHandler { AllowAutoRedirect = false }, disposeInner: true)
            : new AwsResponseGuard(handler, disposeInner: false);
#pragma warning restore CA2000
        var client = new HttpClient(guard, disposeHandler: true);
        if (clientConfig.Timeout is { } timeout && timeout > TimeSpan.Zero)
        {
            client.Timeout = timeout;
        }

        return client;
    }

    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;
}
