using System.Security.Claims;
using Hope.Agent.Application.Abstractions;
using Hope.Agent.Application.LLM;
using Hope.Agent.Domain.Memory;
using Hope.Agent.Shared;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Hope.Agent.Api.Endpoints;

public static class MemoryEndpoints
{
    public static IEndpointRouteBuilder MapMemoryEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/memory").RequireAuthorization().WithTags("Memory");

        grp.MapPost("/upsert", async (
            [FromBody] MemoryUpsertRequest req,
            [FromServices] IMemoryStore store,
            [FromServices] ILLMRouter llm,
            [FromServices] IClock clock,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var userId = ResolveUserId(user, req.UserId);
            var emb = await llm.SelectEmbedding().EmbedAsync(new EmbeddingRequest([req.Content]), ct);
            var record = new MemoryRecord
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                ConversationId = req.ConversationId,
                Kind = req.Kind,
                Content = req.Content,
                Source = req.Source,
                Importance = req.Importance,
                CreatedAt = clock.UtcNow,
            };
            await store.UpsertAsync(record, emb.Vectors[0], ct);
            return TypedResults.Ok(new { record.Id });
        });

        grp.MapPost("/search", async (
            [FromBody] MemorySearchRequest req,
            [FromServices] IMemoryStore store,
            [FromServices] ILLMRouter llm,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var userId = ResolveUserId(user, req.UserId);
            var emb = await llm.SelectEmbedding().EmbedAsync(new EmbeddingRequest([req.Query]), ct);
            var hits = await store.SearchAsync(userId, emb.Vectors[0], req.TopK, req.Kind, ct);
            return TypedResults.Ok(hits);
        });

        return app;
    }

    private static Guid ResolveUserId(ClaimsPrincipal user, Guid? requested)
    {
        if (requested is { } r && r != Guid.Empty) return r;
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : Guid.Empty;
    }
}

public sealed record MemoryUpsertRequest(
    string Content,
    MemoryKind Kind = MemoryKind.Semantic,
    Guid? UserId = null,
    Guid? ConversationId = null,
    string? Source = null,
    float Importance = 0.5f);

public sealed record MemorySearchRequest(
    string Query,
    int TopK = 5,
    MemoryKind? Kind = null,
    Guid? UserId = null);
