using System.Reflection;

namespace ConfigFerret.Web;

/// <summary>Middleware and endpoints the app adds.</summary>
internal static class Hosting
{
    /// <summary>The content security policy. Blazor's script and its WebSocket come from this origin only.</summary>
    internal const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; "
        + "font-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>
    /// The host names the app answers when <c>AllowedHosts</c> isn't set: the ones a browser on the same machine uses.
    /// </summary>
    internal const string DefaultAllowedHosts = "localhost;127.0.0.1;[::1]";

    private static readonly string[] BrandFiles = ["icon.png", "logo.png"];

    /// <summary>
    /// Answers only the host names in <c>AllowedHosts</c>, or <see cref="DefaultAllowedHosts"/> when it isn't set, so a
    /// page whose name is re-pointed at 127.0.0.1 (DNS rebinding) can't use the app. ASP.NET Core's own host filtering
    /// does the checking; <c>*</c> turns it off.
    /// </summary>
    public static WebApplicationBuilder AnswerKnownHostsOnly(this WebApplicationBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration["AllowedHosts"]))
        {
            builder.Configuration["AllowedHosts"] = DefaultAllowedHosts;
        }

        // Kubernetes and load balancers probe a container by its IP address, so /healthz is answered ahead of the host
        // filtering. The framework adds that filtering in a startup filter, and the first startup filter registered runs
        // first. The branch answers "ok" and nothing else.
        builder.Services.Insert(0, ServiceDescriptor.Transient<IStartupFilter, HealthCheckStartupFilter>());
        return builder;
    }

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

/// <summary>Answers GET /healthz with "ok", for any host name.</summary>
internal sealed class HealthCheckStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.MapWhen(
            context => HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/healthz",
            health => health.UseSecurityHeaders().Run(context => Results.Text("ok").ExecuteAsync(context)));
        next(app);
    };
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
