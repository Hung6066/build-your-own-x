namespace Hope.Agent.Api.Middleware;

internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["X-Permitted-Cross-Domain-Policies"] = "none";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        h["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
        if (ctx.Request.IsHttps)
            h["Strict-Transport-Security"] = "max-age=63072000; includeSubDomains";
        return next(ctx);
    }
}
