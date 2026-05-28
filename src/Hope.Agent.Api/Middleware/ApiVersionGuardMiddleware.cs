namespace Hope.Agent.Api.Middleware;

internal sealed class ApiVersionGuardMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> AcceptedHeaderVersions =
    [
        "1",
        "1.0",
        "v1",
        "v1.0",
    ];

    public async Task InvokeAsync(HttpContext ctx)
    {
        ctx.Response.OnStarting(() =>
        {
            ctx.Response.Headers["API-Supported-Versions"] = "1.0";
            return Task.CompletedTask;
        });

        var path = ctx.Request.Path.Value;
        if (!string.IsNullOrEmpty(path) && path.StartsWith("/v", StringComparison.OrdinalIgnoreCase))
        {
            var slashIndex = path.IndexOf('/', 1);
            var versionSegment = slashIndex > 0 ? path[1..slashIndex] : path[1..];
            if (!string.Equals(versionSegment, "v1", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(versionSegment, "v1.0", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = "Unsupported API version",
                    supported = "1.0",
                });
                return;
            }

            if (ctx.Request.Headers.TryGetValue("X-API-Version", out var apiVersion)
                && !string.IsNullOrWhiteSpace(apiVersion)
                && !AcceptedHeaderVersions.Contains(apiVersion.ToString().ToLowerInvariant()))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = "Unsupported X-API-Version header",
                    supported = "1.0",
                });
                return;
            }
        }

        await next(ctx);
    }
}