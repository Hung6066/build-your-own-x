using Hope.Agent.Application.Insights;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Hope.Agent.Api.Endpoints;

public static class InsightEndpoints
{
    public static IEndpointRouteBuilder MapInsightEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/insights")
            .RequireAuthorization()
            .WithTags("Insights");

        grp.MapGet("/", async (
            [FromQuery] Guid userId,
            [FromQuery] int? days,
            [FromServices] ISessionInsightService svc,
            CancellationToken ct) =>
        {
            var rows = await svc.RecentAsync(userId, days ?? 7, ct);
            return Results.Ok(rows);
        });

        grp.MapGet("/search", async (
            [FromQuery] Guid userId,
            [FromQuery] string q,
            [FromQuery] int? take,
            [FromServices] ISessionInsightService svc,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest(new { error = "q required" });
            var rows = await svc.SearchAsync(userId, q, take ?? 20, ct);
            return Results.Ok(rows);
        });

        grp.MapPost("/generate", async (
            [FromBody] GenerateInsightRequest req,
            [FromServices] ISessionInsightService svc,
            CancellationToken ct) =>
        {
            var end = req.PeriodEnd ?? DateTimeOffset.UtcNow;
            var start = req.PeriodStart ?? end.AddDays(-7);
            var s = await svc.GenerateAsync(req.UserId, start, end, ct);
            return s is null ? Results.NoContent() : Results.Ok(s);
        });

        return app;
    }

    public sealed record GenerateInsightRequest(Guid UserId, DateTimeOffset? PeriodStart, DateTimeOffset? PeriodEnd);
}
