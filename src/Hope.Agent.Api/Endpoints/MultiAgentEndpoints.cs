using System.Security.Claims;
using Hope.Agent.Api.Middleware;
using Hope.Agent.Application.Agents.Multi;
using Microsoft.AspNetCore.Mvc;

namespace Hope.Agent.Api.Endpoints;

public static class MultiAgentEndpoints
{
    public static IEndpointRouteBuilder MapMultiAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/multi-agent")
            .RequireAuthorization()
            .WithTags("MultiAgent")
            .WithBodySizeLimit(64 * 1024)
            .WithRequestValidation()
            .WithIdempotency();

        grp.MapPost("/dispatch", async (
            [FromBody] MultiAgentDispatchRequest req,
            [FromServices] IMultiAgentOrchestrator chief,
            ClaimsPrincipal user,
            HttpContext http,
            CancellationToken ct) =>
        {
            var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
            var userId = Guid.TryParse(sub, out var id) ? id : Guid.Empty;
            var task = new AgentTask(
                TaskId: Guid.CreateVersion7(),
                UserId: userId,
                Intent: req.Intent,
                Input: req.Input,
                Context: req.Context ?? [],
                ConversationId: req.ConversationId,
                CorrelationId: http.TraceIdentifier,
                Priority: req.Priority);
            var result = await chief.DispatchAsync(task, ct);
            return Results.Ok(result);
        });

        return app;
    }
}

public sealed record MultiAgentDispatchRequest(
    string Intent,
    string Input,
    Dictionary<string, string>? Context = null,
    Guid? ConversationId = null,
    int Priority = 5);
