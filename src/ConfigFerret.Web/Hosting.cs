using System.Reflection;

namespace ConfigFerret.Web;

/// <summary>Middleware and endpoints the app adds.</summary>
internal static class Hosting
{
    /// <summary>The content security policy. Blazor's script and its WebSocket come from this origin only.</summary>
    internal const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; "
        + "font-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    private static readonly string[] BrandFiles = ["icon.png", "logo.png"];

    /// <summary>Adds headers that stop the pages being framed, sniffed or leaking their address.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            await next(context).ConfigureAwait(false);
        });

    /// <summary>Serves the logo and icon, which live in the repository's assets folder, from /brand/.</summary>
    public static IEndpointRouteBuilder MapBrandAssets(this IEndpointRouteBuilder endpoints)
    {
        foreach (var resource in BrandFiles.Select(name => "brand/" + name))
        {
            endpoints.MapGet("/" + resource, (HttpContext context) =>
            {
                var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
                if (stream is null)
                {
                    return Results.NotFound();
                }

                context.Response.Headers.CacheControl = "public, max-age=3600";
                return Results.Stream(stream, "image/png");
            });
        }

        return endpoints;
    }
}

/// <summary>Checks that the server answers on /healthz, for the container's HEALTHCHECK.</summary>
internal static class HealthCheck
{
    public static async Task<int> RunAsync()
    {
        var port = (Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080").Split(';', ',')[0].Trim();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/healthz")).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
    }
}
