namespace Hope.Agent.Application.Workflows;

/// <summary>
/// Abstraction over the durable workflow engine (Temporal). Lets the API/agents start, query,
/// signal, and cancel long-running clinical workflows without depending on the Temporal SDK directly.
/// </summary>
public interface IWorkflowDispatcher
{
    Task<WorkflowStartResult> StartPatientAdmissionAsync(PatientAdmissionInput input, string? workflowId = null, CancellationToken ct = default);

    Task<WorkflowStartResult> StartEmergencyTriageAsync(EmergencyTriageInput input, string? workflowId = null, CancellationToken ct = default);

    Task SignalApprovalAsync(string workflowId, ApprovalDecision decision, CancellationToken ct = default);

    Task<WorkflowStatus> GetStatusAsync(string workflowId, CancellationToken ct = default);

    Task CancelAsync(string workflowId, string reason, CancellationToken ct = default);
}

public sealed record WorkflowStartResult(string WorkflowId, string RunId, DateTimeOffset StartedAt);

public sealed record WorkflowStatus(
    string WorkflowId,
    string RunId,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? ClosedAt,
    string? Result,
    string? FailureReason);

public sealed record PatientAdmissionInput(
    Guid PatientId,
    Guid UserId,
    string ReasonForAdmission,
    string? InsuranceProvider = null,
    string? PreferredDoctorId = null,
    int Priority = 5);

public sealed record EmergencyTriageInput(
    Guid PatientId,
    Guid UserId,
    string Symptoms,
    string? Location = null);

public sealed record ApprovalDecision(string Step, bool Approved, string? Reason, Guid ApproverId);
