using System.Security.Claims;
using Hope.Agent.Application.Rag;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Hope.Agent.Api.Endpoints;

public static class RagEndpoints
{
    public static IEndpointRouteBuilder MapRagEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/rag").RequireAuthorization().WithTags("RAG");

        grp.MapPost("/documents", async (
            [FromBody] IngestDocumentRequest req,
            [FromServices] IIngestionService svc,
            CancellationToken ct) =>
        {
            var ingest = new IngestRequest(
                req.Title,
                req.Content,
                req.Collection ?? "clinical_guidelines",
                req.Source ?? "manual",
                req.Url,
                req.Metadata);
            if (req.Async)
            {
                await svc.EnqueueAsync(ingest, ct);
                return Results.Accepted();
            }
            var result = await svc.IngestAsync(ingest, ct);
            return Results.Ok(result);
        });

        grp.MapGet("/documents/{id:guid}", async (
            Guid id,
            [FromServices] IDocumentStore store,
            CancellationToken ct) =>
        {
            var doc = await store.GetAsync(id, ct);
            return doc is null ? Results.NotFound() : Results.Ok(doc);
        });

        grp.MapPost("/search", async (
            [FromBody] RagSearchRequest req,
            [FromServices] IRetriever retriever,
            CancellationToken ct) =>
        {
            var hits = await retriever.SearchAsync(new RetrievalQuery(
                req.Query,
                req.Collection ?? "clinical_guidelines",
                req.TopK,
                req.FinalK,
                req.MetadataFilter,
                req.Rerank), ct);
            return TypedResults.Ok(hits);
        });

        return app;
    }
}

public sealed record IngestDocumentRequest(
    string Title,
    string Content,
    string? Collection = null,
    string? Source = null,
    string? Url = null,
    Dictionary<string, string>? Metadata = null,
    bool Async = false);

public sealed record RagSearchRequest(
    string Query,
    string? Collection = null,
    int TopK = 8,
    int FinalK = 4,
    Dictionary<string, string>? MetadataFilter = null,
    bool Rerank = true);
