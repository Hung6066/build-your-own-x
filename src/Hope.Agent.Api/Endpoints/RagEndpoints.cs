using Hope.Agent.Api.Middleware;
using Hope.Agent.Application.Rag;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Hope.Agent.Api.Endpoints;

public static class RagEndpoints
{
    public static IEndpointRouteBuilder MapRagEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/rag")
            .RequireAuthorization()
            .WithTags("RAG")
            .WithBodySizeLimit(512 * 1024)   // 512 KB — clinical documents can be large
            .WithRequestValidation()
            .WithIdempotency();

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

        grp.MapPost("/agentic/query", async (
            ClaimsPrincipal user,
            HttpContext http,
            [FromBody] AgenticRagQueryRequest req,
            [FromServices] IAgenticRagService service,
            CancellationToken ct) =>
        {
            var userId = ResolveUserId(user);
            var tenantId = ResolveTenantId(http, req.TenantId);
            var result = await service.RunAsync(new AgenticRagRequest(
                req.Query,
                userId,
                tenantId,
                req.PatientId,
                req.ConversationId,
                req.Goal,
                req.Corpora,
                req.MaxIterations,
                http.TraceIdentifier), ct);
            return TypedResults.Ok(result);
        });

        grp.MapGet("/agentic/runs/{runId}", async (
            string runId,
            [FromServices] IAgenticRagService service,
            CancellationToken ct) =>
        {
            var trace = await service.GetTraceAsync(runId, ct);
            return trace is null ? Results.NotFound() : Results.Ok(trace);
        });

        grp.MapGet("/agentic/runs/{runId}/provenance", async (
            string runId,
            [FromServices] IAgenticRagService service,
            CancellationToken ct) =>
        {
            var trace = await service.GetTraceAsync(runId, ct);
            if (trace is null) return Results.NotFound();
            return Results.Ok(new
            {
                trace.Run.RunId,
                trace.Run.Query,
                trace.Run.Status,
                trace.Run.ContextSufficient,
                trace.Run.Confidence,
                selectedCorpora = trace.Run.SelectedCorporaJson,
                citations = trace.Run.CitationsJson,
                retrievals = trace.Retrievals.Select(x => new
                {
                    x.Corpus,
                    x.Source,
                    x.ReferenceId,
                    x.Title,
                    x.Score,
                    x.Url,
                    excerpt = x.Content.Length <= 420 ? x.Content : x.Content[..420],
                }),
                assessments = trace.Assessments,
            });
        });

        return app;
    }

    private static Guid ResolveUserId(ClaimsPrincipal user)
        => Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub"), out var id)
            ? id
            : Guid.Empty;

    private static Guid? ResolveTenantId(HttpContext http, Guid? bodyTenantId)
    {
        if (bodyTenantId is not null) return bodyTenantId;
        return Guid.TryParse(http.Request.Headers["X-Tenant-Id"].FirstOrDefault(), out var id) ? id : null;
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

public sealed record AgenticRagQueryRequest(
    string Query,
    Guid? TenantId = null,
    Guid? PatientId = null,
    Guid? ConversationId = null,
    string? Goal = null,
    string[]? Corpora = null,
    int? MaxIterations = null);
