using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.Application.Workflows;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.AgentRuntime.Roles;

/// <summary>
/// Appointment Scheduling Agent — maps chief complaint to specialty, ranks available slots,
/// runs insurance pre-check in parallel, then confirms booking.
/// Reference: Epic AI Scheduling, Google MedLM + Deloitte provider search.
/// </summary>
internal sealed class SchedulingAgentRole(
    IWorkflowDispatcher workflows,
    ILogger<SchedulingAgentRole> log) : IAgentRole
{
    public string Name => "scheduling";
    public string Description => "Books patient appointments: symptom → specialty routing, slot ranking, insurance pre-check.";
    public IReadOnlyList<string> Intents =>
    [
        "book_appointment", "schedule", "xep_lich_hen",
        "kham_benh", "dat_lich", "find_doctor", "tim_bac_si",
    ];

    public async Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
    {
        log.LogInformation("[Scheduling] PatientId={PatientId} Input={Input}",
            task.UserId, task.Input);

        task.Context.TryGetValue("patient_id", out var rawPatientId);
        _ = Guid.TryParse(rawPatientId, out var patientId);

        task.Context.TryGetValue("preferred_doctor", out var preferredDoctor);
        task.Context.TryGetValue("preferred_time", out var preferredTime);
        task.Context.TryGetValue("insurance_card", out var insuranceCard);

        var urgency = DetectUrgency(task.Input);

        var input = new AppointmentSchedulingInput(
            PatientId: patientId == Guid.Empty ? Guid.CreateVersion7() : patientId,
            UserId: task.UserId,
            ChiefComplaint: task.Input,
            Urgency: urgency,
            PreferredDoctorId: preferredDoctor,
            PreferredTime: preferredTime,
            InsuranceCardNumber: insuranceCard);

        var workflowId = $"scheduling-{input.PatientId:N}-{Guid.CreateVersion7():N}";

        var started = await workflows.StartAppointmentSchedulingAsync(input, workflowId, ct)
            .ConfigureAwait(false);

        return new AgentRoleResult(
            Role: Name,
            Success: true,
            Output: $"Lịch hẹn đang được xử lý. Workflow: {started.WorkflowId}. " +
                    $"Bạn sẽ nhận xác nhận qua Zalo/SMS trong ít phút.",
            Metadata: new Dictionary<string, string>
            {
                ["workflow_id"] = started.WorkflowId,
                ["urgency"] = urgency,
                ["chief_complaint"] = task.Input,
            });
    }

    private static string DetectUrgency(string input)
    {
        var lower = input.ToLowerInvariant();
        if (lower.Contains("khẩn") || lower.Contains("urgent") || lower.Contains("sớm nhất") ||
            lower.Contains("ngay") || lower.Contains("cấp") || lower.Contains("emergency"))
            return "urgent";
        if (lower.Contains("tuần này") || lower.Contains("hôm nay") || lower.Contains("sớm"))
            return "soon";
        return "normal";
    }
}
