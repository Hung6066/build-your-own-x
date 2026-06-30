using System.Security.Claims;
using Hope.Agent.Application.Learning;
using Hope.Agent.Application.Observability;
using Hope.Agent.Application.Prompts;
using Hope.Agent.Domain.Learning;
using Hope.Agent.Shared;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Hope.Agent.Api.Endpoints;

public static class LearningEndpoints
{
    public static IEndpointRouteBuilder MapLearningEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/learning").RequireAuthorization().WithTags("Learning");

        grp.MapPost("/feedback", async (
            [FromBody] FeedbackRequest req,
            [FromServices] IFeedbackStore store,
            [FromServices] IAdaptiveRouter router,
            [FromServices] IClock clock,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var userId = ResolveUserId(user, req.UserId);
            var fb = new Feedback
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                ConversationId = req.ConversationId,
                Rating = Math.Sign(req.Rating),
                Comment = req.Comment,
                Provider = req.Provider,
                Model = req.Model,
                Intent = req.Intent,
                CreatedAt = clock.UtcNow,
            };
            await store.RecordAsync(fb, ct);
            HopeMeters.FeedbackRecorded.Add(1, new KeyValuePair<string, object?>("rating", fb.Rating));

            if (!string.IsNullOrWhiteSpace(req.Provider) && !string.IsNullOrWhiteSpace(req.Model) && !string.IsNullOrWhiteSpace(req.Intent))
            {
                await router.RecordOutcomeAsync(req.Intent!, req.Provider!, req.Model!, fb.Rating, latencyMs: 0, failed: fb.Rating < 0, ct);
            }
            return Results.Accepted($"/v1/learning/feedback/{fb.Id}", new { fb.Id });
        });

        grp.MapGet("/feedback/{conversationId:guid}", async (
            Guid conversationId,
            [FromServices] IFeedbackStore store,
            CancellationToken ct) =>
        {
            var items = await store.RecentByConversationAsync(conversationId, take: 50, ct);
            return Results.Ok(items);
        });

        grp.MapGet("/eval/runs", async (
            [FromServices] IEvaluationHarness harness,
            CancellationToken ct) =>
        {
            var runs = await harness.RecentRunsAsync(take: 20, ct);
            return Results.Ok(runs);
        });

        grp.MapPost("/eval/run", async (
            [FromQuery] string? suite,
            [FromServices] IEvaluationHarness harness,
            CancellationToken ct) =>
        {
            var run = await harness.RunSuiteAsync(suite ?? "default", ct);
            return Results.Ok(run);
        });

        // ── Trend analysis ──────────────────────────────────────────────────────
        grp.MapGet("/eval/trend", async (
            [FromQuery] string? suite,
            [FromQuery] int? days,
            [FromServices] IEvaluationHarness harness,
            CancellationToken ct) =>
        {
            var trend = await harness.GetTrendAsync(suite ?? "default", days ?? 30, ct);
            return Results.Ok(trend);
        }).WithSummary("Score trend over time — use to verify the agent is improving.");

        grp.MapGet("/eval/metrics", async (
            [FromQuery] string? suite,
            [FromQuery] int? days,
            [FromServices] IEvaluationHarness harness,
            CancellationToken ct) =>
        {
            var metrics = await harness.GetMetricsAsync(suite ?? "default", days ?? 30, ct);
            return Results.Ok(metrics);
        }).WithSummary("Evaluation metrics: task success, hallucination, tool-call accuracy, faithfulness, latency, cost.");

        // ── Elo leaderboard ─────────────────────────────────────────────────────
        grp.MapGet("/eval/leaderboard", async (
            [FromQuery] string? suite,
            [FromQuery] int? take,
            [FromServices] IEvaluationHarness harness,
            CancellationToken ct) =>
        {
            var board = await harness.GetLeaderboardAsync(suite ?? "default", take ?? 20, ct);
            return Results.Ok(board);
        }).WithSummary("Completed runs ranked by Elo — higher = smarter agent version.");

        grp.MapPost("/eval/tournament", async (
            [FromQuery] string? suite,
            [FromServices] IEvaluationHarness harness,
            CancellationToken ct) =>
        {
            try
            {
                var result = await harness.RunEloTournamentAsync(suite ?? "default", ct);
                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithSummary("Co-Scientist-style Elo tournament between the two most recent runs.");

        grp.MapPost("/prompts/{promptName}/optimize", async (
            string promptName,
            [FromBody] PromptOptimizeRequest? req,
            [FromServices] IPromptOptimizationService optimizer,
            CancellationToken ct) =>
        {
            var result = await optimizer.OptimizeAsync(
                promptName,
                req?.Suite ?? "default",
                req?.AutoPromote,
                ct);
            return Results.Ok(result);
        }).WithSummary("DSPy-style prompt optimization loop: generate candidates, evaluate, optionally promote.");

        // ── Eval case management ────────────────────────────────────────────────
        grp.MapGet("/eval/cases", async (
            [FromQuery] string? suite,
            [FromServices] IEvalCaseStore cases,
            CancellationToken ct) =>
        {
            var list = await cases.GetBySuiteAsync(suite ?? "default", ct);
            return Results.Ok(list);
        });

        grp.MapPost("/eval/cases", async (
            [FromBody] AddEvalCaseRequest req,
            [FromServices] IEvalCaseStore cases,
            CancellationToken ct) =>
        {
            var c = new Hope.Agent.Domain.Learning.EvalCase
            {
                Id = Guid.CreateVersion7(),
                Suite = req.Suite ?? "default",
                Name = req.Name,
                UserMessage = req.UserMessage,
                ReferenceAnswer = req.ReferenceAnswer,
                Tags = req.Tags,
                Active = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            var saved = await cases.AddAsync(c, ct);
            return Results.Created($"/v1/learning/eval/cases/{saved.Id}", saved);
        });

        grp.MapDelete("/eval/cases/{id:guid}", async (
            Guid id,
            [FromServices] IEvalCaseStore cases,
            CancellationToken ct) =>
        {
            var deleted = await cases.DeleteAsync(id, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        return app;
    }

    private static Guid ResolveUserId(ClaimsPrincipal user, Guid? hint)
    {
        if (hint is { } h) return h;
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return Guid.TryParse(sub, out var g) ? g : Guid.Empty;
    }
}

public sealed record FeedbackRequest(
    Guid ConversationId,
    int Rating,
    string? Comment = null,
    string? Provider = null,
    string? Model = null,
    string? Intent = null,
    Guid? UserId = null);

public sealed record AddEvalCaseRequest(
    string Name,
    string UserMessage,
    string ReferenceAnswer,
    string? Suite = null,
    string? Tags = null);

public sealed record PromptOptimizeRequest(string? Suite = null, bool? AutoPromote = null);
