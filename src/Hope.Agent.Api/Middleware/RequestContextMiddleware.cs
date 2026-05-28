namespace Hope.Agent.Api.Middleware;

internal sealed class RequestContextMiddleware(RequestDelegate next)
{
    private const string RequestIdHeader = "X-Request-Id";

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue(RequestIdHeader, out var incoming)
            && !string.IsNullOrWhiteSpace(incoming)
            && incoming.ToString().Length <= 128)
        {
            ctx.TraceIdentifier = incoming.ToString();
        }

        ctx.Response.OnStarting(() =>
        {
            ctx.Response.Headers[RequestIdHeader] = ctx.TraceIdentifier;
            return Task.CompletedTask;
        });

        await next(ctx);
    }
}