using System.Security.Claims;
using Hope.Agent.Application.Security;
using Microsoft.AspNetCore.Mvc;

namespace Hope.Agent.Api.Endpoints;

public static class ApprovalEndpoints
{
    public static IEndpointRouteBuilder MapApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/security/approvals").RequireAuthorization().WithTags("Approvals");

        grp.MapGet("/pending", async (
            [FromServices] IToolApprovalRequestStore store,
            [FromQuery] int take = 100,
            CancellationToken ct = default) =>
        {
            var list = await store.PendingAsync(Math.Clamp(take, 1, 500), ct);
            return Results.Ok(list);
        });

        grp.MapGet("/", async (
            [FromServices] IToolApprovalRequestStore store,
            [FromQuery] DateTimeOffset? from,
            [FromQuery] DateTimeOffset? to,
            [FromQuery] int take = 100,
            CancellationToken ct = default) =>
        {
            var f = from ?? DateTimeOffset.UtcNow.AddDays(-7);
            var t = to ?? DateTimeOffset.UtcNow;
            var list = await store.QueryAsync(f, t, Math.Clamp(take, 1, 500), ct);
            return Results.Ok(list);
        });

        grp.MapPost("/{id:guid}/approve", async (
            Guid id,
            HttpContext http,
            [FromServices] IToolApprovalGate gate,
            [FromBody] ApprovalDecisionDto? body,
            CancellationToken ct) =>
        {
            var decidedBy = ResolveUserId(http);
            if (decidedBy is null) return Results.Unauthorized();
            var ok = await gate.CompleteAsync(id, approved: true, body?.Reason, decidedBy.Value, ct);
            return ok ? Results.NoContent() : Results.NotFound(new { error = "not_pending_or_expired" });
        });

        grp.MapPost("/{id:guid}/deny", async (
            Guid id,
            HttpContext http,
            [FromServices] IToolApprovalGate gate,
            [FromBody] ApprovalDecisionDto? body,
            CancellationToken ct) =>
        {
            var decidedBy = ResolveUserId(http);
            if (decidedBy is null) return Results.Unauthorized();
            var ok = await gate.CompleteAsync(id, approved: false, body?.Reason, decidedBy.Value, ct);
            return ok ? Results.NoContent() : Results.NotFound(new { error = "not_pending_or_expired" });
        });

        return app;
    }

    private static Guid? ResolveUserId(HttpContext http)
    {
        var sub = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                  ?? http.User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var g) ? g : null;
    }

    public sealed record ApprovalDecisionDto(string? Reason);
}
