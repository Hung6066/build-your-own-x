namespace Hope.Agent.Api.Middleware;

/// <summary>
/// Rejects POST, PUT, and PATCH requests whose Content-Type is not application/json.
/// Guards against content-type confusion attacks and ensures the JSON deserializer
/// is never invoked on a non-JSON body (e.g., XML, multipart, text/plain).
/// </summary>
internal sealed class ContentTypeGuardMiddleware(
    RequestDelegate next,
    ILogger<ContentTypeGuardMiddleware> logger)
{
    // Path prefixes that carry non-JSON bodies (SSE transport, WebSocket upgrade, health probes).
    private static readonly string[] SkipPrefixes =
    [
        "/mcp",     // MCP SSE / JSON-RPC transport — enforced by SDK itself
        "/healthz", // GET only, included for belt-and-suspenders
        "/hubs",    // SignalR WebSocket upgrade (GET)
    ];

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (RequiresJsonBody(ctx.Request) && !HasJsonContentType(ctx.Request))
        {
            logger.LogWarning(
                "ContentTypeGuard: rejected {Method} {Path} — Content-Type '{ContentType}' is not application/json",
                ctx.Request.Method,
                ctx.Request.Path,
                ctx.Request.ContentType ?? "(none)");

            ctx.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            await ctx.Response.WriteAsJsonAsync(new
            {
                type = "https://tools.ietf.org/html/rfc9110#section-15.5.16",
                title = "Unsupported Media Type",
                status = 415,
                detail = "Content-Type must be 'application/json'.",
            });
            return;
        }

        await next(ctx);
    }

    private static bool RequiresJsonBody(HttpRequest request)
    {
        // Only mutation methods are expected to carry a JSON body.
        if (!HttpMethods.IsPost(request.Method)
            && !HttpMethods.IsPut(request.Method)
            && !HttpMethods.IsPatch(request.Method))
        {
            return false;
        }

        // Skip well-known non-JSON paths.
        foreach (var prefix in SkipPrefixes)
        {
            if (request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    // HasJsonContentType() is an ASP.NET Core extension that accepts
    // "application/json" with or without charset/params (RFC 9110 compliant).
    private static bool HasJsonContentType(HttpRequest request) =>
        request.HasJsonContentType();
}
