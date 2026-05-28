using System.Security.Claims;
using System.Text.Json;
using Hope.Agent.Application.Abstractions;
using Hope.Agent.Application.Security;
using Hope.Agent.Domain.Audit;

namespace Hope.Agent.Api.Middleware;

internal sealed class AuditLoggingMiddleware(
    RequestDelegate next,
    IAuditSink auditSink,
    IPhiRedactor phiRedactor,
    IWebHostEnvironment env,
    ILogger<AuditLoggingMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.Request.Path.StartsWithSegments("/healthz"))
        {
            await next(ctx);
            return;
        }

        var started = DateTimeOffset.UtcNow;
        try
        {
            await next(ctx);
        }
        finally
        {
            var userIdText = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var actor = ctx.User.FindFirstValue(ClaimTypes.Email)
                ?? userIdText
                ?? "anonymous";
            var path = ctx.Request.Path.ToString();
            var isOpenApiAccess = path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase);
            var redactedPath = phiRedactor.Redact(ctx.Request.Path + ctx.Request.QueryString);
            var durationMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
            var ip = ctx.Connection.RemoteIpAddress?.ToString();
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["method"] = ctx.Request.Method,
                ["statusCode"] = ctx.Response.StatusCode,
                ["durationMs"] = durationMs,
                ["ip"] = ip,
                ["userAgent"] = ctx.Request.Headers.UserAgent.ToString(),
                ["isOpenApiAccess"] = isOpenApiAccess,
            });

            var evt = new AuditEvent
            {
                Id = Guid.CreateVersion7(),
                OccurredAt = DateTimeOffset.UtcNow,
                UserId = Guid.TryParse(userIdText, out var parsed) ? parsed : null,
                Actor = actor,
                Action = ctx.Request.Method,
                ResourceType = isOpenApiAccess ? "openapi" : "http",
                ResourceId = redactedPath,
                CorrelationId = ctx.TraceIdentifier,
                Reason = ctx.Response.StatusCode >= 400 ? $"HTTP {ctx.Response.StatusCode}" : null,
                PayloadJson = payload,
            };

            if (isOpenApiAccess && env.IsProduction())
            {
                log.LogWarning("OpenAPI accessed in production by {Actor} from {Ip}", actor, ip ?? "unknown");
            }

            try
            {
                await auditSink.WriteAsync(evt, ctx.RequestAborted);
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
            {
                // Request was cancelled; avoid noisy error logging.
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to persist audit event for {Method} {Path}", ctx.Request.Method, redactedPath);
            }
        }
    }
}