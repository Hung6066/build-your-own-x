using Hope.Agent.Application.Learning;
using Hope.Agent.Domain.Learning;
using Hope.Agent.Shared;
using Microsoft.AspNetCore.Mvc;

namespace Hope.Agent.Api.Endpoints;

public static class ShadowEndpoints
{
    public sealed record ChallengerRequest(
        string Intent,
        string ChallengerProvider,
        double TrafficFraction = 0.1,
        int MinSamples = 50,
        double PromotionWinRate = 0.55);

    public static IEndpointRouteBuilder MapShadowEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/learning/challengers").RequireAuthorization().WithTags("ShadowAB");

        grp.MapPost("/", async (
            [FromBody] ChallengerRequest req,
            [FromServices] IShadowComparator shadow,
            [FromServices] IClock clock,
            CancellationToken ct) =>
        {
            var cfg = new ChallengerConfig
            {
                Id = Guid.CreateVersion7(),
                Intent = req.Intent,
                ChallengerProvider = req.ChallengerProvider,
                TrafficFraction = req.TrafficFraction,
                MinSamples = req.MinSamples,
                PromotionWinRate = req.PromotionWinRate,
                Active = true,
                CreatedAt = clock.UtcNow,
            };
            await shadow.UpsertChallengerAsync(cfg, ct);
            return Results.Ok(cfg);
        });

        grp.MapGet("/{intent}", async (
            string intent,
            [FromServices] IShadowComparator shadow,
            CancellationToken ct) =>
        {
            var cfg = await shadow.GetActiveChallengerAsync(intent, ct);
            return cfg is null ? Results.NotFound() : Results.Ok(cfg);
        });

        app.MapGet("/v1/learning/shadow/{intent}", async (
            string intent,
            [FromServices] IShadowComparator shadow,
            [FromQuery] int take = 50,
            CancellationToken ct = default) =>
        {
            var list = await shadow.RecentAsync(intent, take, ct);
            return Results.Ok(list);
        }).RequireAuthorization().WithTags("ShadowAB");

        return app;
    }
}
