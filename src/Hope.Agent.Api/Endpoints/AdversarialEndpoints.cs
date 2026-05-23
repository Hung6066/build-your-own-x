using Hope.Agent.Application.Security;
using Microsoft.AspNetCore.Mvc;

namespace Hope.Agent.Api.Endpoints;

public static class AdversarialEndpoints
{
    public static IEndpointRouteBuilder MapAdversarialEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/security/adversarial").RequireAuthorization().WithTags("Adversarial");

        grp.MapGet("/", async (
            [FromServices] IAdversarialPatternStore store,
            [FromQuery] int take = 100,
            CancellationToken ct = default) =>
        {
            var list = await store.AllAsync(take, ct);
            return Results.Ok(list);
        });

        grp.MapPost("/{id:guid}/promote", async (
            Guid id,
            [FromServices] IAdversarialPatternStore store,
            CancellationToken ct) =>
        {
            await store.PromoteAsync(id, ct);
            return Results.NoContent();
        });

        grp.MapPost("/{id:guid}/demote", async (
            Guid id,
            [FromServices] IAdversarialPatternStore store,
            CancellationToken ct) =>
        {
            await store.DemoteAsync(id, ct);
            return Results.NoContent();
        });

        return app;
    }
}
