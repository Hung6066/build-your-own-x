using System.Net;
using Hope.Agent.Application.Security;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Hope.Agent.Api.Middleware;

/// <summary>
/// Global exception handler that:
/// <list type="bullet">
///   <item>Logs the full exception detail internally (structured, with correlation ID).</item>
///   <item>Returns an opaque <see cref="ProblemDetails"/> to the caller — no stack trace,
///         no internal type names, no raw exception messages in production.</item>
///   <item>Passes exception messages through <see cref="IPhiRedactor"/> before logging
///         so PHI in exception context does not reach log storage unredacted.</item>
/// </list>
/// </summary>
internal sealed class SafeExceptionHandler(
    IPhiRedactor phiRedactor,
    IWebHostEnvironment env,
    ILogger<SafeExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext ctx,
        Exception exception,
        CancellationToken ct)
    {
        var correlationId = ctx.TraceIdentifier;
        var redactedMessage = phiRedactor.Redact(exception.Message);

        // Log full detail server-side (never sent to client).
        log.LogError(
            exception,
            "Unhandled exception [{CorrelationId}] {ExceptionType}: {RedactedMessage}",
            correlationId,
            exception.GetType().Name,
            redactedMessage);

        var (statusCode, title) = MapException(exception);

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            // In development expose a redacted hint; in production always opaque.
            Detail = env.IsDevelopment()
                ? $"{exception.GetType().Name}: {redactedMessage}"
                : "An unexpected error occurred. Reference the correlation ID when reporting this issue.",
            Extensions =
            {
                ["correlationId"] = correlationId,
                ["traceId"] = ctx.TraceIdentifier,
            },
        };

        // Do NOT include Instance (request path) in production — it may contain PHI in query-strings.
        if (env.IsDevelopment())
            problem.Instance = phiRedactor.Redact(ctx.Request.Path + ctx.Request.QueryString);

        ctx.Response.StatusCode = statusCode;
        await ctx.Response.WriteAsJsonAsync(problem, ct);
        return true;
    }

    private static (int statusCode, string title) MapException(Exception ex) => ex switch
    {
        ArgumentException or ArgumentNullException => (StatusCodes.Status400BadRequest, "Invalid request."),
        UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "Access denied."),
        KeyNotFoundException => (StatusCodes.Status404NotFound, "Resource not found."),
        TimeoutException or TaskCanceledException or OperationCanceledException =>
            (StatusCodes.Status504GatewayTimeout, "The operation timed out."),
        NotSupportedException or NotImplementedException =>
            (StatusCodes.Status501NotImplemented, "Not implemented."),
        _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred."),
    };
}
