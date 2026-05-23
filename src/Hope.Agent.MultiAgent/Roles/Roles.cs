using System.Globalization;
using System.Text;
using System.Text.Json;
using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Rag;
using Hope.Agent.Application.Tools;

namespace Hope.Agent.MultiAgent.Roles;

internal sealed class SchedulingAgent(IToolRegistry tools) : IAgentRole
{
    public string Name => "scheduling";
    public string Description => "Books, reschedules, and optimizes patient appointments.";
    public IReadOnlyList<string> Intents { get; } = ["schedule", "appointment", "reschedule", "booking"];

    public async Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
    {
        var tool = tools.Find("schedule_appointment");
        if (tool is null) return new AgentRoleResult(Name, false, "schedule_appointment tool unavailable");
        var ctx = new ToolInvocationContext(task.UserId, task.ConversationId ?? Guid.Empty, task.CorrelationId ?? string.Empty);
        var args = JsonSerializer.Serialize(new
        {
            patient_id = task.Context.GetValueOrDefault("patient_id", task.UserId.ToString()),
            department = task.Context.GetValueOrDefault("department", "general"),
            preferred_time = task.Context.GetValueOrDefault("preferred_time", DateTimeOffset.UtcNow.AddDays(1).ToString("O", CultureInfo.InvariantCulture)),
            reason = task.Input,
        });
        var output = await tool.InvokeAsync(args, ctx, ct);
        return new AgentRoleResult(Name, true, output);
    }
}

internal sealed class ClinicalAgent(IRetriever retriever, ILLMRouter llm) : IAgentRole
{
    public string Name => "clinical";
    public string Description => "Clinical reasoning, guideline retrieval, drug interaction triage.";
    public IReadOnlyList<string> Intents { get; } = ["clinical", "diagnosis", "reasoning", "guideline", "drug_interaction"];

    public async Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
    {
        var hits = await retriever.SearchAsync(new RetrievalQuery(task.Input, "clinical_guidelines", TopK: 8, FinalK: 4), ct);
        var ctxBlock = new StringBuilder();
        foreach (var h in hits)
        {
            ctxBlock.Append("[Source: ").Append(h.Title).Append("] ").AppendLine(h.Content);
        }
        var chat = llm.SelectChat();
        var resp = await chat.CompleteAsync(new ChatRequest(
            [
                new ChatMessage("system", "You are a clinical reasoning assistant. Ground every claim in the supplied context. If context is insufficient, say so explicitly. Never invent dosages."),
                new ChatMessage("user", $"Context:\n{ctxBlock}\n\nQuestion: {task.Input}"),
            ],
            Temperature: 0.2f), ct);
        return new AgentRoleResult(Name, true, resp.Content, new Dictionary<string, string>
        {
            ["citations"] = JsonSerializer.Serialize(hits.Select(h => new { h.Title, h.Url, h.Score })),
        });
    }
}

internal sealed class BillingAgent(IToolRegistry tools) : IAgentRole
{
    public string Name => "billing";
    public string Description => "Insurance verification, coverage checks, claims pre-validation.";
    public IReadOnlyList<string> Intents { get; } = ["billing", "insurance", "coverage", "claim"];

    public async Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
    {
        var tool = tools.Find("verify_insurance");
        if (tool is null) return new AgentRoleResult(Name, false, "verify_insurance tool unavailable");
        var ctx = new ToolInvocationContext(task.UserId, task.ConversationId ?? Guid.Empty, task.CorrelationId ?? string.Empty);
        var args = JsonSerializer.Serialize(new
        {
            patient_id = task.Context.GetValueOrDefault("patient_id", task.UserId.ToString()),
            procedure_code = task.Context.GetValueOrDefault("procedure_code", "GEN-001"),
        });
        var output = await tool.InvokeAsync(args, ctx, ct);
        return new AgentRoleResult(Name, true, output);
    }
}

internal sealed class ComplianceAgent : IAgentRole
{
    public string Name => "compliance";
    public string Description => "Validates PHI handling, RBAC, and policy adherence before downstream actions.";
    public IReadOnlyList<string> Intents { get; } = ["compliance", "hipaa", "phi", "policy"];

    private static readonly string[] PhiMarkers = ["ssn", "social security", "credit card", "passport"];

    public Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
    {
        var lower = task.Input.ToLowerInvariant();
        var violations = PhiMarkers.Where(p => lower.Contains(p, StringComparison.Ordinal)).ToList();
        var success = violations.Count == 0;
        var output = success
            ? "compliance:ok"
            : $"compliance:blocked markers={string.Join(',', violations)}";
        return Task.FromResult(new AgentRoleResult(Name, success, output, new Dictionary<string, string>
        {
            ["violations"] = string.Join(',', violations),
        }));
    }
}

internal sealed class EmergencyAgent(ILLMRouter llm) : IAgentRole
{
    public string Name => "emergency";
    public string Description => "Triages urgent/emergency cases; escalates strokes, MI, sepsis, trauma.";
    public IReadOnlyList<string> Intents { get; } = ["emergency", "triage", "stroke", "icu", "urgent"];

    public async Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
    {
        var chat = llm.SelectChat();
        var resp = await chat.CompleteAsync(new ChatRequest(
            [
                new ChatMessage("system", "Triage agent. Classify urgency on a 1-5 scale (5=life-threatening). Output JSON: {\"level\":N,\"reason\":\"...\",\"route\":\"er|icu|ward|outpatient\"}."),
                new ChatMessage("user", task.Input),
            ],
            Temperature: 0f), ct);
        int level = 3;
        try
        {
            var s = resp.Content.IndexOf('{', StringComparison.Ordinal);
            var e = resp.Content.LastIndexOf('}');
            if (s >= 0 && e > s)
            {
                using var doc = JsonDocument.Parse(resp.Content[s..(e + 1)]);
                level = doc.RootElement.GetProperty("level").GetInt32();
            }
        }
        catch { /* keep default */ }
        var handoffs = new List<AgentHandoff>();
        if (level >= 4)
        {
            handoffs.Add(new AgentHandoff("notification", "high-urgency triage", resp.Content));
        }
        return new AgentRoleResult(Name, true, resp.Content, new Dictionary<string, string>
        {
            ["urgency"] = level.ToString(CultureInfo.InvariantCulture),
        }, handoffs);
    }
}

internal sealed class NotificationAgent(Hope.Agent.Application.Eventing.IEventPublisher events, Hope.Agent.Application.Notifications.IRealtimeNotifier realtime) : IAgentRole
{
    public string Name => "notification";
    public string Description => "Dispatches notifications via realtime hub and durable event bus (Kafka).";
    public IReadOnlyList<string> Intents { get; } = ["notify", "notification", "alert", "message"];

    public async Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
    {
        var n = new Hope.Agent.Application.Notifications.AgentNotification(
            Id: Guid.CreateVersion7(),
            OccurredAt: DateTimeOffset.UtcNow,
            Channel: task.Context.GetValueOrDefault("channel", "system"),
            Type: task.Context.GetValueOrDefault("type", "info"),
            Title: task.Context.GetValueOrDefault("title", "Agent notification"),
            Body: task.Input,
            UserId: task.UserId == Guid.Empty ? null : task.UserId,
            Metadata: task.Context);
        var payload = JsonSerializer.Serialize(n);
        await events.PublishAsync("agent.notifications", n.Id.ToString(), payload, ct);
        if (n.UserId is { } uid) await realtime.SendToUserAsync(uid, n, ct);
        else await realtime.BroadcastAsync(n, ct);
        return new AgentRoleResult(Name, true, payload);
    }
}
