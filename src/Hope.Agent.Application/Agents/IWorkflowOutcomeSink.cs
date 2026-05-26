namespace Hope.Agent.Application.Agents;

/// <summary>
/// Records the outcome of a completed workflow dispatch back into the learning system
/// (<see cref="Hope.Agent.Application.Learning.ISkillLibrary"/> + <see cref="Hope.Agent.Application.Learning.IFeedbackStore"/>).
/// This closes the feedback loop: real-world outcomes inform future routing decisions
/// and optimization cost weights.
/// </summary>
public interface IWorkflowOutcomeSink
{
    Task RecordAsync(WorkflowOutcome outcome, CancellationToken ct);
}

/// <param name="WorkflowType">Workflow identifier, e.g. "appointment_scheduling".</param>
/// <param name="Intent">The agent intent that was dispatched.</param>
/// <param name="Role">Which agent role handled the task.</param>
/// <param name="Success">Whether the role returned <c>Success = true</c>.</param>
/// <param name="RewardSignal">
/// Normalized reward in [0, 1]. 1 = fully successful; 0 = failure.
/// Use fractional values for partial successes (e.g. 0.5 for "completed but suboptimal").
/// </param>
/// <param name="CorrelationId">Trace correlation ID for log correlation.</param>
/// <param name="Context">
/// Optional task context forwarded from the dispatched activity.
/// Used by adaptive-cost sinks to extract e.g. "doctor_id" and "specialty" for MCMF feedback.
/// </param>
public sealed record WorkflowOutcome(
    string WorkflowType,
    string Intent,
    string Role,
    bool Success,
    double RewardSignal,
    string? CorrelationId = null,
    IReadOnlyDictionary<string, string>? Context = null);
