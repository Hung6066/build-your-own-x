using Hope.Agent.Application.Agents;
using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.Application.Autonomy;
using Hope.Agent.Application.Eventing;
using Hope.Agent.Application.Notifications;
using Hope.Agent.Domain.Autonomy;
using System.Text.Json;
using Temporalio.Activities;

namespace Hope.Agent.Workflows.Activities;

/// <summary>
/// Activities are the only place where a workflow can do non-deterministic IO.
/// All clinical operations are delegated to the multi-agent orchestrator.
/// </summary>
public sealed class ClinicalActivities
{
    private readonly IMultiAgentOrchestrator orchestrator;
    private readonly IEventPublisher publisher;
    private readonly IRealtimeNotifier notifier;
    private readonly IWorkflowOutcomeSink? outcomeSink;
    private readonly IAutonomyDecisionService? autonomy;

    public ClinicalActivities(
        IMultiAgentOrchestrator orchestrator,
        IEventPublisher publisher,
        IRealtimeNotifier notifier,
        IWorkflowOutcomeSink? outcomeSink = null,
        IAutonomyDecisionService? autonomy = null)
    {
        this.orchestrator = orchestrator;
        this.publisher = publisher;
        this.notifier = notifier;
        this.outcomeSink = outcomeSink;
        this.autonomy = autonomy;
    }

    [Activity]
    public async Task<AgentDispatchResult> DispatchAgentAsync(AgentDispatchInput input)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var task = new AgentTask(
            TaskId: Guid.CreateVersion7(),
            UserId: input.UserId,
            Intent: input.Intent,
            Input: input.Input,
            Context: input.Context ?? new Dictionary<string, string>(),
            ConversationId: input.ConversationId,
            CorrelationId: input.CorrelationId,
            Priority: input.Priority);

        var result = await orchestrator.DispatchAsync(task, ct).ConfigureAwait(false);
        if (autonomy is not null)
        {
            try
            {
                var success = result.Trace.Count > 0 && result.Trace[^1].Success;
                var evaluation = autonomy.Evaluate(new AutonomyEvaluationRequest(
                    input.UserId,
                    TryGetPatientId(input.Context),
                    input.ConversationId,
                    input.Intent,
                    result.FinalRole,
                    input.Input,
                    null,
                    null,
                    success ? 0.86 : 0.55,
                    input.CorrelationId));
                await autonomy.RecordDecisionAsync(new AgentDecisionWrite(
                    input.UserId,
                    TryGetPatientId(input.Context),
                    input.ConversationId,
                    input.Intent,
                    result.FinalRole,
                    input.Input.Length <= 512 ? input.Input : input.Input[..512],
                    null,
                    JsonSerializer.Serialize(new { trace_count = result.Trace.Count, final_role = result.FinalRole }),
                    null,
                    evaluation.RiskLevel,
                    success ? 0.86 : 0.55,
                    evaluation.PolicyDecision,
                    evaluation.DecisionStatus,
                    evaluation.Reason,
                    input.CorrelationId), ct).ConfigureAwait(false);
            }
            catch
            {
                // Autonomy ledger failures must not break workflow activities.
            }
        }

        // Record the outcome into the learning system.
        // Must be awaited here: fire-and-forget can outlive the activity scope and
        // then use disposed scoped dependencies (DbContext, repositories).
        if (outcomeSink is not null)
        {
            var outcome = new WorkflowOutcome(
                WorkflowType: input.Intent,
                Intent: input.Intent,
                Role: result.FinalRole,
                Success: result.Trace.Count > 0 && result.Trace[^1].Success,
                RewardSignal: result.Trace.Count > 0 && result.Trace[^1].Success ? 1.0 : 0.0,
                CorrelationId: input.CorrelationId,
                Context: input.Context is { Count: > 0 } ? input.Context : null);
            try
            {
                await outcomeSink.RecordAsync(outcome, ct).ConfigureAwait(false);
            }
            catch
            {
                // Ignore learning-path failures so the clinical activity result remains stable.
            }
        }

        return new AgentDispatchResult(result.TaskId, result.FinalRole, result.Output, result.Trace.Count);
    }

    [Activity]
    public async Task NotifyAsync(NotificationActivityInput input)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var notification = new AgentNotification(
            Id: Guid.CreateVersion7(),
            OccurredAt: DateTimeOffset.UtcNow,
            Channel: input.Channel,
            Type: input.Type,
            Title: input.Title,
            Body: input.Body,
            UserId: input.UserId,
            Metadata: input.Metadata);

        if (input.UserId is Guid uid)
        {
            await notifier.SendToUserAsync(uid, notification, ct).ConfigureAwait(false);
        }
        else
        {
            await notifier.BroadcastAsync(notification, ct).ConfigureAwait(false);
        }
    }

    [Activity]
    public async Task PublishEventAsync(EventActivityInput input)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var json = JsonSerializer.Serialize(input.Payload);
        await publisher.PublishAsync(input.Topic, input.Key, json, ct).ConfigureAwait(false);
    }

    [Activity]
    public Task<bool> ApprovalReceivedSentinelAsync(string workflowId) =>
        // No-op marker — workflows wait for the approval signal; this activity exists so the
        // step is durable in workflow history with a clear name for the dashboard timeline.
        Task.FromResult(true);

    [Activity]
    public Task<string> GenerateBusinessIdAsync(BusinessIdActivityInput input)
    {
        var date = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
        var suffix = Guid.CreateVersion7().ToString("N")[..input.SuffixLength].ToUpperInvariant();
        return Task.FromResult($"{input.Prefix}-{date}-{suffix}");
    }

    private static Guid? TryGetPatientId(IReadOnlyDictionary<string, string>? context)
    {
        if (context is not null
            && context.TryGetValue("patient_id", out var raw)
            && Guid.TryParse(raw, out var patientId))
            return patientId;
        return null;
    }
}

public sealed record AgentDispatchInput(
    Guid UserId,
    string Intent,
    string Input,
    Dictionary<string, string>? Context = null,
    Guid? ConversationId = null,
    string? CorrelationId = null,
    int Priority = 5);

public sealed record AgentDispatchResult(Guid TaskId, string Role, string Output, int Hops);

public sealed record NotificationActivityInput(
    string Channel,
    string Type,
    string Title,
    string Body,
    Guid? UserId = null,
    Dictionary<string, string>? Metadata = null);

public sealed record EventActivityInput(string Topic, string Key, object Payload);

public sealed record BusinessIdActivityInput(string Prefix, int SuffixLength = 8);
