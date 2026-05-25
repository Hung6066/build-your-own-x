namespace Hope.Agent.Application.Workflows;

/// <summary>
/// Abstraction over the durable workflow engine (Temporal). Lets the API/agents start, query,
/// signal, and cancel long-running clinical workflows without depending on the Temporal SDK directly.
/// </summary>
public interface IWorkflowDispatcher
{
    Task<WorkflowStartResult> StartPatientAdmissionAsync(PatientAdmissionInput input, string? workflowId = null, CancellationToken ct = default);

    Task<WorkflowStartResult> StartEmergencyTriageAsync(EmergencyTriageInput input, string? workflowId = null, CancellationToken ct = default);

    Task<WorkflowStartResult> StartAppointmentSchedulingAsync(AppointmentSchedulingInput input, string? workflowId = null, CancellationToken ct = default);

    Task<WorkflowStartResult> StartMedicationReminderAsync(MedicationReminderInput input, string? workflowId = null, CancellationToken ct = default);

    Task SignalReminderConfirmationAsync(string workflowId, ReminderConfirmation confirmation, CancellationToken ct = default);

    Task<WorkflowStartResult> StartAuditReportAsync(AuditReportInput input, string? workflowId = null, CancellationToken ct = default);

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

// ── Appointment Scheduling ──────────────────────────────────────────────────

public sealed record AppointmentSchedulingInput(
    Guid PatientId,
    Guid UserId,
    string ChiefComplaint,
    string Urgency = "normal",
    string? PreferredDoctorId = null,
    string? PreferredTime = null,
    string? InsuranceCardNumber = null);

public sealed record AppointmentSchedulingResult(
    string BookingId,
    string DoctorName,
    string Specialty,
    DateTimeOffset AppointmentTime,
    string InsuranceSummary,
    IReadOnlyList<string> StepLog);

// ── Medication Reminder ─────────────────────────────────────────────────────

public sealed record MedicationReminderInput(
    Guid PatientId,
    Guid UserId,
    string MedicationName,
    string Dosage,
    string Frequency,
    DateTimeOffset StartAt,
    int DurationDays,
    string PreferredChannel = "zalo",
    int AdherenceRiskScore = 30);

public sealed record ReminderConfirmation(string WorkflowId, bool Confirmed, string? Note);

// ── Audit Report ────────────────────────────────────────────────────────────

public sealed record AuditReportInput(
    Guid RequestedBy,
    string ReportType,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    string ExportFormat = "json");

public sealed record AuditReportResult(
    string ReportId,
    string ReportType,
    string NarrativeSummary,
    string ExportPath,
    string IntegrityHash,
    IReadOnlyList<string> StepLog);
