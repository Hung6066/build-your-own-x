using System.Diagnostics;
using System.Text.Json;
using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.Application.Eventing;
using Hope.Agent.Application.LLM;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.MultiAgent.Orchestration;

internal static class MultiAgentTelemetry
{
    public static readonly ActivitySource Source = new("Hope.Agent.MultiAgent");
}

internal sealed class ChiefMedicalAgent(
    IEnumerable<IAgentRole> roles,
    ILLMRouter llm,
    IEventPublisher events,
    ILogger<ChiefMedicalAgent> log) : IMultiAgentOrchestrator
{
    private readonly Dictionary<string, IAgentRole> _byName = roles.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<IAgentRole> _all = [.. roles];

    public async Task<MultiAgentResult> DispatchAsync(AgentTask task, CancellationToken ct)
    {
        using var act = MultiAgentTelemetry.Source.StartActivity("chief.dispatch");
        act?.SetTag("task.intent", task.Intent);
        act?.SetTag("task.id", task.TaskId);
        act?.SetTag("task.user", task.UserId);

        await events.PublishAsync("agent.task.created", task.TaskId.ToString(),
            JsonSerializer.Serialize(new { task.TaskId, task.UserId, task.Intent, task.Priority }), ct);

        var trace = new List<AgentRoleResult>();
        var initial = await SelectRoleAsync(task, ct);
        log.LogInformation("Chief routing task {Id} (intent={Intent}) → {Role}", task.TaskId, task.Intent, initial.Name);

        var current = initial;
        var currentTask = task;
        for (int hop = 0; hop < 4; hop++)
        {
            using var hopAct = MultiAgentTelemetry.Source.StartActivity($"role.{current.Name}");
            hopAct?.SetTag("role.name", current.Name);
            var result = await current.HandleAsync(currentTask, ct);
            trace.Add(result);

            await events.PublishAsync("agent.role.completed", task.TaskId.ToString(),
                JsonSerializer.Serialize(new { task.TaskId, current.Name, result.Success, result.Output }), ct);

            if (result.Handoffs is { Count: > 0 } && hop < 3)
            {
                var next = result.Handoffs[0];
                if (_byName.TryGetValue(next.TargetRole, out var nextRole))
                {
                    currentTask = currentTask with { Intent = next.TargetRole, Input = next.Payload };
                    current = nextRole;
                    continue;
                }
            }
            break;
        }

        var final = trace[^1];
        await events.PublishAsync("agent.task.completed", task.TaskId.ToString(),
            JsonSerializer.Serialize(new { task.TaskId, FinalRole = final.Role, final.Success }), ct);

        return new MultiAgentResult(task.TaskId, final.Role, final.Output, trace);
    }

    private async Task<IAgentRole> SelectRoleAsync(AgentTask task, CancellationToken ct)
    {
        // Cheap intent match first
        var direct = _all.FirstOrDefault(r =>
            r.Intents.Any(i => string.Equals(i, task.Intent, StringComparison.OrdinalIgnoreCase)));
        if (direct is not null) return direct;

        // Otherwise, ask the LLM to classify into one of the available roles.
        var roster = string.Join("\n", _all.Select(r => $"- {r.Name}: {r.Description} (intents: {string.Join(",", r.Intents)})"));
        var prompt = $$"""
            You are a medical operations router. Pick ONE agent role that best handles the task.
            Output strict JSON: {"role":"<name>"}.

            Available roles:
            {{roster}}

            Task:
            intent: {{task.Intent}}
            input: {{task.Input}}
            """;
        try
        {
            var chat = llm.SelectChat();
            var resp = await chat.CompleteAsync(new ChatRequest(
                [new ChatMessage("system", "Output only valid JSON."), new ChatMessage("user", prompt)],
                Temperature: 0f), ct);
            var start = resp.Content.IndexOf('{', StringComparison.Ordinal);
            var end = resp.Content.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                using var doc = JsonDocument.Parse(resp.Content[start..(end + 1)]);
                var name = doc.RootElement.GetProperty("role").GetString();
                if (!string.IsNullOrEmpty(name) && _byName.TryGetValue(name, out var picked)) return picked;
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Role classification failed; falling back to clinical role");
        }
        return _byName.GetValueOrDefault("clinical") ?? _all[0];
    }
}
