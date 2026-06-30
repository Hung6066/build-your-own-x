using System.Security.Claims;
using Hope.Agent.Api.Middleware;
using Hope.Agent.Application.Autonomy;
using Hope.Agent.Domain.Autonomy;
using Microsoft.AspNetCore.Mvc;

namespace Hope.Agent.Api.Endpoints;

public static class AutonomyEndpoints
{
    public static IEndpointRouteBuilder MapAutonomyEndpoints(this IEndpointRouteBuilder app)
    {
        var patients = app.MapGroup("/v1/patients")
            .RequireAuthorization()
            .WithTags("Patients")
            .WithBodySizeLimit(64 * 1024)
            .WithRequestValidation();

        patients.MapGet("/{patientId:guid}/timeline", async (
            Guid patientId,
            [FromServices] IPatientTimelineService timeline,
            [FromQuery] int take = 100,
            CancellationToken ct = default) =>
        {
            var result = await timeline.GetTimelineAsync(patientId, Math.Clamp(take, 1, 500), ct);
            return Results.Ok(result);
        });

        var suggestions = app.MapGroup("/v1/agents")
            .RequireAuthorization()
            .WithTags("Agent Suggestions")
            .WithBodySizeLimit(64 * 1024)
            .WithRequestValidation()
            .WithIdempotency();

        suggestions.MapPost("/suggestions", async (
            [FromBody] SuggestionRequest req,
            [FromServices] IAgentSuggestionService service,
            ClaimsPrincipal user,
            HttpContext http,
            CancellationToken ct) =>
        {
            var userId = ResolveUserId(user);
            var result = await service.SuggestAsync(req.PatientId, userId, req.Goal, http.TraceIdentifier, ct);
            return Results.Ok(result);
        });

        var autonomy = app.MapGroup("/v1/autonomy")
            .RequireAuthorization()
            .WithTags("Autonomy");

        autonomy.MapGet("/decisions", async (
            [FromServices] IAgentDecisionStore store,
            [FromQuery] Guid? patientId,
            [FromQuery] Guid? userId,
            [FromQuery] DateTimeOffset? from,
            [FromQuery] DateTimeOffset? to,
            [FromQuery] int take = 100,
            CancellationToken ct = default) =>
        {
            var until = to ?? DateTimeOffset.UtcNow;
            var start = from ?? until.AddDays(-7);
            var rows = await store.QueryAsync(patientId, userId, start, until, Math.Clamp(take, 1, 500), ct);
            return Results.Ok(rows);
        });

        autonomy.MapGet("/actions", async (
            [FromServices] IAutonomousActionStore store,
            [FromQuery] AutonomousActionStatus? status,
            [FromQuery] DateTimeOffset? from,
            [FromQuery] DateTimeOffset? to,
            [FromQuery] int take = 100,
            CancellationToken ct = default) =>
        {
            var until = to ?? DateTimeOffset.UtcNow;
            var start = from ?? until.AddDays(-7);
            var rows = await store.QueryAsync(status, start, until, Math.Clamp(take, 1, 500), ct);
            return Results.Ok(rows);
        });

        autonomy.MapPost("/actions/{id}/approve", async (
            string id,
            [FromServices] IAutonomousActionStore actions,
            [FromServices] IAgentDecisionStore decisions,
            [FromBody] ActionDecisionRequest? body,
            CancellationToken ct) =>
        {
            var action = await actions.GetByActionIdAsync(id, ct);
            if (action is null) return Results.NotFound(new { error = "action_not_found" });
            action.Status = AutonomousActionStatus.Approved;
            action.Error = null;
            action.ScheduledFor ??= DateTimeOffset.UtcNow;
            await actions.UpdateAsync(action, ct);
            await decisions.UpdateStatusAsync(action.DecisionId, AgentDecisionStatus.Approved, body?.Reason ?? "approved", ct);
            return Results.NoContent();
        });

        autonomy.MapPost("/actions/{id}/deny", async (
            string id,
            [FromServices] IAutonomousActionStore actions,
            [FromServices] IAgentDecisionStore decisions,
            [FromBody] ActionDecisionRequest? body,
            CancellationToken ct) =>
        {
            var action = await actions.GetByActionIdAsync(id, ct);
            if (action is null) return Results.NotFound(new { error = "action_not_found" });
            action.Status = AutonomousActionStatus.Denied;
            action.Error = body?.Reason ?? "denied";
            await actions.UpdateAsync(action, ct);
            await decisions.UpdateStatusAsync(action.DecisionId, AgentDecisionStatus.Denied, action.Error, ct);
            return Results.NoContent();
        });

        autonomy.MapPost("/daily-review/run", async (
            [FromServices] IAutonomyDailyReviewService service,
            CancellationToken ct) =>
        {
            var reviewed = await service.RunOnceAsync(DateTimeOffset.UtcNow, force: true, ct);
            return Results.Ok(new { reviewed });
        });

        autonomy.MapPost("/agi-like/run", async (
            [FromServices] IAutonomyAgiLikeService service,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var userId = ResolveUserId(user);
            var result = await service.RunOnceAsync(userId, force: true, ct);
            return Results.Ok(result);
        });

        autonomy.MapGet("/agi-like/status", async (
            [FromServices] IAutonomyAgiLikeService service,
            CancellationToken ct) =>
        {
            var result = await service.GetStatusAsync(ct);
            return Results.Ok(result);
        });

        autonomy.MapPost("/level5/eval-gate/run", async (
            [FromServices] IAutonomyLevel5ControlService service,
            HttpContext http,
            [FromBody] EvalGateRequest? body,
            CancellationToken ct) =>
        {
            var result = await service.RunEvalGateAsync(body?.SuiteName ?? "level5_operational", http.TraceIdentifier, ct);
            return Results.Ok(result);
        });

        autonomy.MapPost("/level5/drift/detect", async (
            [FromServices] IAutonomyLevel5ControlService service,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await service.DetectDriftAsync(http.TraceIdentifier, ct);
            return Results.Ok(result);
        });

        autonomy.MapGet("/level5/readiness", async (
            [FromServices] IAutonomyLevel5ControlService service,
            CancellationToken ct) =>
        {
            var result = await service.GetReadinessAsync(ct);
            return Results.Ok(result);
        });

        autonomy.MapGet("/goals", async (
            [FromServices] IAutonomyGoalStore store,
            [FromQuery] Guid? patientId,
            [FromQuery] AutonomyGoalStatus? status,
            [FromQuery] DateTimeOffset? from,
            [FromQuery] DateTimeOffset? to,
            [FromQuery] int take = 100,
            CancellationToken ct = default) =>
        {
            var until = to ?? DateTimeOffset.UtcNow;
            var start = from ?? until.AddDays(-7);
            var rows = await store.QueryAsync(patientId, status, start, until, Math.Clamp(take, 1, 500), ct);
            return Results.Ok(rows);
        });

        autonomy.MapGet("/reflections", async (
            [FromServices] IAutonomyReflectionStore store,
            [FromQuery] Guid? patientId,
            [FromQuery] DateTimeOffset? from,
            [FromQuery] DateTimeOffset? to,
            [FromQuery] int take = 100,
            CancellationToken ct = default) =>
        {
            var until = to ?? DateTimeOffset.UtcNow;
            var start = from ?? until.AddDays(-7);
            var rows = await store.QueryAsync(patientId, start, until, Math.Clamp(take, 1, 500), ct);
            return Results.Ok(rows);
        });

        autonomy.MapGet("/learning-facts", async (
            [FromServices] IAutonomyLearningFactStore store,
            [FromQuery] AutonomyLearningFactKind? kind,
            [FromQuery] int take = 100,
            CancellationToken ct = default) =>
        {
            var rows = await store.QueryAsync(kind, Math.Clamp(take, 1, 500), ct);
            return Results.Ok(rows);
        });

        return app;
    }

    private static Guid ResolveUserId(ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : Guid.Empty;
    }
}

public sealed record SuggestionRequest(Guid PatientId, string Goal);
public sealed record ActionDecisionRequest(string? Reason);
public sealed record EvalGateRequest(string? SuiteName);
