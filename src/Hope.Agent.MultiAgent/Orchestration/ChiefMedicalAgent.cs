using System.Diagnostics;
using System.Text.Json;
using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.Application.Eventing;
using Hope.Agent.Application.Governance;
using Hope.Agent.Application.Learning;
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
    ILogger<ChiefMedicalAgent> log,
    IAdaptiveRouter? adaptiveRouter = null,
    IGovernanceGate? gate = null) : IMultiAgentOrchestrator
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

        // Governance gate: verify the initial intent is permitted before any role executes.
        // The gate is optional — when not registered the check is skipped (e.g. unit tests).
        if (gate is not null)
        {
            var decision = await gate.EvaluateIntentAsync(
                "did:mesh:hope-agent", task.Intent, null, ct);
            if (!decision.Allowed)
            {
                var reason = decision.DenyReason
                    ?? $"Policy '{decision.PolicyName}' denied intent '{task.Intent}'";
                log.LogWarning("Governance gate blocked task {Id} intent='{Intent}': {Reason}",
                    task.TaskId, task.Intent, reason);
                await events.PublishAsync("agent.governance.denied", task.TaskId.ToString(),
                    JsonSerializer.Serialize(new { task.TaskId, task.Intent, reason }), ct);
                return new MultiAgentResult(task.TaskId, "governance",
                    $"governance:denied reason={reason}", []);
            }
        }

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
        // Cheap intent match first (no LLM call needed)
        var direct = _all.FirstOrDefault(r =>
            r.Intents.Any(i => string.Equals(i, task.Intent, StringComparison.OrdinalIgnoreCase)));
        if (direct is not null) return direct;

        // LLM classification with adaptive provider selection.
        // IAdaptiveRouter uses a UCB1 bandit to prefer providers that historically
        // respond faster and with fewer failures for this intent.
        RouterChoice? adaptiveChoice = null;
        if (adaptiveRouter is not null)
        {
            try { adaptiveChoice = await adaptiveRouter.SelectChatAsync(task.Intent, ct); }
            catch (Exception ex) { log.LogWarning(ex, "Adaptive router selection failed; using default provider"); }
        }

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

        var sw = Stopwatch.StartNew();
        try
        {
            var chat = llm.SelectChat(adaptiveChoice?.Provider);
            var resp = await chat.CompleteAsync(new ChatRequest(
                [new ChatMessage("system", "Output only valid JSON."), new ChatMessage("user", prompt)],
                Temperature: 0f), ct);
            sw.Stop();

            // Feed the outcome back to the bandit so it can update its estimates
            if (adaptiveRouter is not null)
            {
                await adaptiveRouter.RecordOutcomeAsync(
                    task.Intent, resp.Provider, resp.Model,
                    reward: 1.0, latencyMs: sw.Elapsed.TotalMilliseconds, failed: false, ct);
            }

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
            sw.Stop();
            log.LogWarning(ex, "Role classification failed; falling back to clinical role");

            if (adaptiveRouter is not null && adaptiveChoice is not null)
            {
                await adaptiveRouter.RecordOutcomeAsync(
                    task.Intent, adaptiveChoice.Provider, adaptiveChoice.Model,
                    reward: 0.0, latencyMs: sw.Elapsed.TotalMilliseconds, failed: true, ct);
            }
        }

        return _byName.GetValueOrDefault("clinical") ?? _all[0];
    }
}
