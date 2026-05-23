using Hope.Agent.Application.Knowledge;
using Microsoft.AspNetCore.Mvc;

namespace Hope.Agent.Api.Endpoints;

public static class KnowledgeEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/kg").RequireAuthorization().WithTags("Knowledge");

        grp.MapGet("/entities", async (
            [FromQuery] string q,
            [FromServices] IKnowledgeGraphStore store,
            [FromQuery] int take = 20,
            CancellationToken ct = default) =>
        {
            var results = await store.SearchEntitiesAsync(q ?? string.Empty, take, ct);
            return Results.Ok(results);
        });

        grp.MapGet("/neighbors/{id}", async (
            string id,
            [FromServices] IKnowledgeGraphStore store,
            [FromQuery] int depth = 1,
            CancellationToken ct = default) =>
        {
            var results = await store.NeighborsAsync(id, depth, ct);
            return Results.Ok(results);
        });

        return app;
    }
}
