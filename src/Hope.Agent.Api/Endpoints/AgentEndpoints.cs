using System.Security.Claims;
using Hope.Agent.Application.Agents;
using Microsoft.AspNetCore.Mvc;

namespace Hope.Agent.Api.Endpoints;

/// <summary>
/// Agent chat endpoints — single-turn and streaming conversation with the Hope.Agent runtime.
/// </summary>
public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/agent").RequireAuthorization().WithTags("Agent");

        grp.MapPost("/chat", async (
            [FromBody] AgentChatRequest req,
            [FromServices] IAgentRuntime runtime,
            ClaimsPrincipal user,
            HttpContext http,
            CancellationToken ct) =>
        {
            var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
            var userId = Guid.TryParse(sub, out var id) ? id : Guid.Empty;

            var request = new AgentRequest(
                UserId: userId,
                ConversationId: req.ConversationId,
                Message: req.Message,
                CorrelationId: http.TraceIdentifier);

            var result = await runtime.RunAsync(request, ct);
            return Results.Ok(result);
        }).RequireRateLimiting("agent-concurrency");

        return app;
    }
}

public sealed record AgentChatRequest(
    string Message,
    Guid? ConversationId = null,
    Dictionary<string, string>? Context = null);

