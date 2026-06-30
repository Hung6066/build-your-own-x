using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.Application.Security;
using Hope.Agent.Application.Workflows;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.AgentRuntime.Roles;

/// <summary>
/// Insurance Verification Agent — verifies BHYT eligibility, checks supplementary coverage,
/// suggests ICD-10/CPT codes, and returns a patient cost estimate before the visit.
/// Reference: Accenture Solutions.AI, Amazon Comprehend Medical, Epic real-time eligibility, Suki point-of-care coding.
/// </summary>
internal sealed class InsuranceVerificationAgentRole(
    IWorkflowDispatcher workflows,
    IPhiRedactor phi,
    ILogger<InsuranceVerificationAgentRole> log) : IAgentRole
{
    public string Name => "insurance";
    public string Description => "Verifies BHYT/private insurance coverage, suggests ICD-10 codes, and estimates patient cost.";
    public IReadOnlyList<string> Intents =>
    [
        "insurance_check", "kiem_tra_bao_hiem", "verify_coverage",
        "claim_preview", "bhyt", "coverage", "chi_phi_kham",
    ];

    public async Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
    {
        log.LogInformation("[Insurance] UserId={UserId} Input={Input}", task.UserId, phi.Redact(task.Input));

        task.Context.TryGetValue("patient_id", out var rawPatientId);
        _ = Guid.TryParse(rawPatientId, out var patientId);
        task.Context.TryGetValue("insurance_card", out var insuranceCard);
        task.Context.TryGetValue("specialty", out var specialty);
        task.Context.TryGetValue("chief_complaint", out var complaint);

        // Start the admission workflow's insurance sub-step via a dedicated scheduling workflow
        // that runs insurance verification in parallel with slot lookup (mirrors AGENT_WORKFLOWS section 3).
        var input = new AppointmentSchedulingInput(
            PatientId: patientId == Guid.Empty ? Guid.CreateVersion7() : patientId,
            UserId: task.UserId,
            ChiefComplaint: complaint ?? task.Input,
            Urgency: "normal",
            InsuranceCardNumber: insuranceCard);

        var workflowId = $"insurance-{input.PatientId:N}-{Guid.CreateVersion7():N}";
        var started = await workflows.StartAppointmentSchedulingAsync(input, workflowId, ct)
            .ConfigureAwait(false);

        log.LogInformation("[Insurance] Workflow started: {WorkflowId}", started.WorkflowId);

        return new AgentRoleResult(
            Role: Name,
            Success: true,
            Output: BuildInitialResponse(insuranceCard, specialty),
            Metadata: new Dictionary<string, string>
            {
                ["workflow_id"] = started.WorkflowId,
                ["insurance_card"] = string.IsNullOrWhiteSpace(insuranceCard) ? "pending" : "[REDACTED]",
                ["specialty"] = specialty ?? "unknown",
            });
    }

    private static string BuildInitialResponse(string? card, string? specialty)
    {
        if (string.IsNullOrEmpty(card))
            return "Vui lòng cung cấp số thẻ BHYT hoặc chụp ảnh thẻ để tôi kiểm tra quyền lợi bảo hiểm của bạn.";

        return
            $"Đang kiểm tra thẻ BHYT [ĐÃ ẨN] cho chuyên khoa {specialty ?? "tổng quát"}.\n" +
            "Kết quả sẽ bao gồm:\n" +
            "• Trạng thái thẻ (còn hạn / hết hạn)\n" +
            "• Mức hưởng BHYT (%)\n" +
            "• Đúng/trái tuyến\n" +
            "• Bảo hiểm bổ sung (nếu có)\n" +
            "• Chi phí dự kiến bệnh nhân phải trả\n\n" +
            "Vui lòng chờ trong giây lát...";
    }
}
