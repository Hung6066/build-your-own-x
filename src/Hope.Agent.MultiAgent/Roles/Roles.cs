using System.Globalization;
using System.Text;
using System.Text.Json;
using Hope.Agent.Application.Agents;
using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.Application.Agents.ReAct;
using Hope.Agent.Application.Governance;
using Hope.Agent.Application.Learning;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Rag;
using Hope.Agent.Application.Tools;
using Microsoft.Extensions.Options;

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

        // If the scheduler found no available slot, hand off to the MCMF optimizer for retry
        if (output.Contains("\"success\":false", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("no_slot", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentRoleResult(Name, false, output, null,
                [new AgentHandoff("optimization", "Standard scheduling found no available slot; request MCMF optimization", task.Input)]);
        }

        return new AgentRoleResult(Name, true, output);
    }
}

internal sealed class ClinicalAgent(
    IRetriever retriever,
    ILLMRouter llm,
    IReActLoop? reactLoop = null,
    IReflector? reflector = null,
    ITreeOfThoughts? treeOfThoughts = null,
    IPatientMemoryService? patientMemory = null,
    IOptions<GovernancePolicyOptions>? governanceOptions = null) : IAgentRole
{
    // Emergency triggers loaded from GovernancePolicyOptions (externalised from hard-coded array).
    // Falls back to a minimal built-in list only when governance options are not registered.
    private static readonly string[] _defaultEmergencyTriggers =
    [
        "stroke", "cardiac arrest", "respiratory failure", "code blue",
    ];

    private string[] EmergencyTriggers =>
        governanceOptions?.Value.EmergencyTriggers ?? _defaultEmergencyTriggers;

    public string Name => "clinical";
    public string Description => "Clinical reasoning, guideline retrieval, drug interaction triage.";
    public IReadOnlyList<string> Intents { get; } = ["clinical", "diagnosis", "reasoning", "guideline", "drug_interaction"];

    public async Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
    {
        // Retrieve cross-workflow patient memory (non-blocking; enriches reasoning context)
        var memoryContext = string.Empty;
        if (patientMemory is not null && task.UserId != Guid.Empty)
        {
            var memories = await patientMemory.RetrieveAsync(task.UserId, task.Input, topK: 3, ct: ct);
            if (memories.Count > 0)
                memoryContext = "Previous patient history:\n" + string.Join("\n", memories.Select((m, i) => $"{i + 1}. {m}")) + "\n\n";
        }

        // Retrieve relevant clinical guidelines (always — provides grounding for all paths)
        var hits = await retriever.SearchAsync(new RetrievalQuery(task.Input, "clinical_guidelines", TopK: 8, FinalK: 4), ct);
        var ctxBlock = new StringBuilder();
        if (memoryContext.Length > 0) ctxBlock.Append(memoryContext);
        foreach (var h in hits)
            ctxBlock.Append("[Source: ").Append(h.Title).Append("] ").AppendLine(h.Content);

        var citations = new Dictionary<string, string>
        {
            ["citations"] = JsonSerializer.Serialize(hits.Select(h => new { h.Title, h.Url, h.Score })),
        };

        var contextSuffix = ctxBlock.Length > 0 ? $"Clinical Context:\n{ctxBlock}" : null;
        string answer;

        // Path 1: Tree of Thoughts — parallel multi-branch exploration (highest quality)
        if (treeOfThoughts is not null)
        {
            var totOpts = new ToTOptions
            {
                BranchCount = 3,
                MaxStepsPerBranch = 3,
                Temperature = 0.6f,
                SystemPromptSuffix = contextSuffix,
            };
            var totResult = await treeOfThoughts.RunAsync(task, [], totOpts, ct);
            answer = totResult.BestAnswer;
            citations["tot_branches"] = totResult.Branches.Count.ToString(CultureInfo.InvariantCulture);
            citations["tot_winner_score"] = totResult.WinnerScore.ToString("F2", CultureInfo.InvariantCulture);
        }
        // Path 2: ReAct — iterative multi-step reasoning
        else if (reactLoop is not null)
        {
            var opts = new ReActOptions
            {
                MaxIterations = 5,
                Temperature = 0.2f,
                EnableReflection = reflector is not null,
                SystemPromptSuffix = contextSuffix,
            };
            var result = await reactLoop.RunAsync(task, [], opts, ct);
            answer = result.FinalAnswer;
            citations["react_steps"] = result.Steps.Count.ToString(CultureInfo.InvariantCulture);
            if (result.ReflectionCritique is { Length: > 0 } critique)
                citations["reflection_critique"] = critique;
        }
        // Path 3: One-shot LLM with RAG context
        else
        {
            var chat = llm.SelectChat();
            var resp = await chat.CompleteAsync(new ChatRequest(
                [
                    new ChatMessage("system", "You are a clinical reasoning assistant. Ground every claim in the supplied context. If context is insufficient, say so explicitly. Never invent dosages."),
                    new ChatMessage("user", $"Context:\n{ctxBlock}\n\nQuestion: {task.Input}"),
                ],
                Temperature: 0.2f), ct);
            answer = resp.Content;

            if (reflector is not null)
            {
                try
                {
                    var reflection = await reflector.CritiqueAndRefineAsync(task.Input, answer, ct);
                    answer = reflection.RefinedAnswer;
                    citations["reflection_score"] = reflection.Score.ToString("F2", CultureInfo.InvariantCulture);
                    citations["reflection_critique"] = reflection.Critique;
                }
                catch { /* reflection is non-blocking */ }
            }
        }

        // Persist outcome as patient memory for future cross-workflow recall (non-blocking)
        if (patientMemory is not null && task.UserId != Guid.Empty && !string.IsNullOrWhiteSpace(answer))
        {
            var summary = answer.Length > 500 ? answer[..500] : answer;
            _ = patientMemory.WriteAsync(task.UserId, $"Q: {task.Input}\nA: {summary}", ct: ct);
        }

        // Emergency detection: if the answer mentions a life-threatening condition, hand off immediately.
        // EmergencyTriggers are loaded from GovernancePolicyOptions (not hard-coded).
        IReadOnlyList<AgentHandoff>? handoffs = null;
        var matched = EmergencyTriggers.FirstOrDefault(m => answer.Contains(m, StringComparison.OrdinalIgnoreCase));
        if (matched is not null)
            handoffs = [new AgentHandoff("emergency", $"Clinical reasoning detected potential emergency: {matched}", answer)];

        return new AgentRoleResult(Name, true, answer, citations, handoffs);
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

internal sealed class ComplianceAgent(IGovernanceGate? gate = null) : IAgentRole
{
    public string Name => "compliance";
    public string Description => "Validates PHI handling, RBAC, and policy adherence before downstream actions.";
    public IReadOnlyList<string> Intents { get; } = ["compliance", "hipaa", "phi", "policy"];

    // Fallback patterns used only when IGovernanceGate is not registered in DI.
    private static readonly string[] _fallbackPhiMarkers = ["ssn", "social security", "credit card", "passport"];

    public Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
    {
        // AGT-backed PHI scan via PromptInjectionDetector with CustomPatterns = PhiMarkers.
        // Falls back to a simple string-contains check when gate is not registered.
        IReadOnlyList<string> violations = gate is not null
            ? gate.ScanForForbiddenPatterns(task.Input)
            : _fallbackPhiMarkers.Where(p =>
                task.Input.Contains(p, StringComparison.OrdinalIgnoreCase)).ToList();

        var success = violations.Count == 0;
        var output = success
            ? "compliance:ok"
            : $"compliance:blocked markers={string.Join(',', violations)}";

        // Emit handoff to clinical agent so it can provide a safe alternative response
        IReadOnlyList<AgentHandoff>? handoffs = violations.Count > 0
            ? [new AgentHandoff("clinical",
                $"Compliance blocked due to PHI markers: {string.Join(", ", violations)}. Please provide a safe, de-identified alternative.",
                task.Input)]
            : null;

        return Task.FromResult(new AgentRoleResult(Name, success, output, new Dictionary<string, string>
        {
            ["violations"] = string.Join(',', violations),
        }, handoffs));
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
