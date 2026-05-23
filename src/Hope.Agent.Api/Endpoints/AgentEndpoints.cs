using System.Security.Claims;
using System.Text;
using Hope.Agent.Application.Agents;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Hope.Agent.Api.Endpoints;

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
            var userId = ResolveUserId(user);
            var corr = http.TraceIdentifier;
            var resp = await runtime.RunAsync(new AgentRequest(userId, req.ConversationId, req.Message, req.Profile, corr), ct);
            return TypedResults.Ok(resp);
        });

        grp.MapPost("/stream", async (
            [FromBody] AgentChatRequest req,
            [FromServices] IAgentRuntime runtime,
            ClaimsPrincipal user,
            HttpContext http,
            CancellationToken ct) =>
        {
            var userId = ResolveUserId(user);
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.Append("X-Accel-Buffering", "no");
            await foreach (var chunk in runtime.StreamAsync(new AgentRequest(userId, req.ConversationId, req.Message, req.Profile, http.TraceIdentifier, Stream: true), ct))
            {
                var line = $"data: {chunk.Replace("\n", "\\n")}\n\n";
                await http.Response.WriteAsync(line, Encoding.UTF8, ct);
                await http.Response.Body.FlushAsync(ct);
            }
            await http.Response.WriteAsync("data: [DONE]\n\n", ct);
        });

        return app;
    }

    private static Guid ResolveUserId(ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : Guid.Empty;
    }
}

public sealed record AgentChatRequest(string Message, Guid? ConversationId = null, string? Profile = null);
