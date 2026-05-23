using System.Security.Claims;
using Hope.Agent.Application.Learning;
using Hope.Agent.Application.Observability;
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
