namespace Hope.Agent.Api.Middleware;

internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["Content-Security-Policy"] = "default-src 'self'; script-src 'self' https://cdn.jsdelivr.net; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; font-src 'self' https://cdn.jsdelivr.net; connect-src 'self' https://api.openai.com https://api.anthropic.com https://generativelanguage.googleapis.com; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; upgrade-insecure-requests";
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["X-XSS-Protection"] = "1; mode=block";
        h["Referrer-Policy"] = "strict-origin-when-cross-origin";
        h["X-Permitted-Cross-Domain-Policies"] = "none";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        h["Permissions-Policy"] = "geolocation=(), camera=(), microphone=(), payment=(), usb=(), magnetometer=(), gyroscope=(), accelerometer=(), ambient-light-sensor=(), encrypted-media=(), fullscreen=(), picture-in-picture=()";
        h["Cache-Control"] = "no-store, no-cache, must-revalidate, proxy-revalidate, max-age=0";
        h["Pragma"] = "no-cache";
        h["Expires"] = "0";
        h["X-Request-Id"] = ctx.TraceIdentifier;
        if (ctx.Request.IsHttps)
        {
            h["Expect-CT"] = "max-age=86400, enforce";
            h["Strict-Transport-Security"] = "max-age=63072000; includeSubDomains; preload";
        }
        h.Remove("Server");
        h["Server"] = "Hope.Agent/1.0";
        return next(ctx);
    }
}
