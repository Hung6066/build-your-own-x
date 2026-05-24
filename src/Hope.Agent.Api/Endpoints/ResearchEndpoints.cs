using Hope.Agent.Application.Research;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Hope.Agent.Api.Endpoints;

/// <summary>
/// Deep Research endpoints — wraps GeminiDeepResearchAgent (Gemini 2.5 Flash/Pro with Google Search
/// grounding + extended thinking).  Inspired by Deep Research Max (Google I/O 2025).
/// </summary>
public static class ResearchEndpoints
{
    public static IEndpointRouteBuilder MapResearchEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/research").RequireAuthorization().WithTags("Research");

        grp.MapPost("", async (
            [FromBody] ResearchRequest request,
            [FromServices] IDeepResearchAgent agent,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
                return Results.BadRequest(new { error = "Query is required." });

            var report = await agent.ResearchAsync(request, ct);
            return Results.Ok(report);
        })
        .WithSummary("Run a grounded Deep Research pass using Gemini + Google Search.")
        .WithDescription("""
            Fast mode: single grounded call (gemini-2.5-flash, ~10s).
            Max  mode: three-phase plan → search → synthesise (gemini-2.5-pro quality, ~60s).
            Requires LLM:Gemini:ApiKey to be set.
            """);

        return app;
    }
}
