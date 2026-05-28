using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Hope.Agent.Api.Middleware;
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
        var grp = app.MapGroup("/v1/agent")
            .RequireAuthorization()
            .WithTags("Agent")
            .WithBodySizeLimit(64 * 1024)
            .WithRequestValidation();  // 64 KB — message cap already enforced at 8000 chars

        grp.MapPost("/chat", async (
            [FromBody] AgentChatRequest req,
            [FromServices] IAgentRuntime runtime,
            ClaimsPrincipal user,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Message))
                return Results.BadRequest("Message is required.");

            if (req.Message.Length > 8000)
                return Results.BadRequest("Message exceeds maximum length (8000).");

            var lowered = req.Message.ToLowerInvariant();
            if (lowered.Contains("drop table", StringComparison.Ordinal)
                || lowered.Contains("delete from", StringComparison.Ordinal)
                || lowered.Contains("'; --", StringComparison.Ordinal))
            {
                return Results.BadRequest("Suspicious input detected.");
            }

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
    [StringLength(8000, MinimumLength = 1)]
    [RegularExpression(@"^[\p{L}\p{N}\p{P}\p{Z}]+$", ErrorMessage = "Message contains invalid characters")]
    string Message,
    Guid? ConversationId = null,
    Dictionary<string, string>? Context = null);

