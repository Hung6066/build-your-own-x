using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Security;
using Hope.Agent.Application.Tools;
using Hope.Agent.Application.Workflows;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Hope.Agent.AgentRuntime.Roles;

/// <summary>
/// Reminder Agent — creates durable medication and follow-up reminder workflows via Temporal.
/// Computes adherence risk score to decide reminder frequency and escalates to care team
/// when a patient misses 3+ doses or appointments.
/// Reference: Epic MyChart reminders, Amazon Connect outbound call, Suki care plan integration.
/// </summary>
internal sealed class ReminderAgentRole(
    IWorkflowDispatcher workflows,
    IToolRegistry tools,
    IPhiRedactor phi,
    ILogger<ReminderAgentRole> log) : IAgentRole
{
    public string Name => "reminder";
    public string Description => "Creates durable medication and follow-up reminders with adherence risk scoring and care-team escalation.";
    public IReadOnlyList<string> Intents =>
    [
        "medication_reminder", "nhac_thuoc", "nhac_tai_kham",
        "create_reminder", "follow_up", "adherence", "nhac_lich",
    ];

    public async Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
    {
        log.LogInformation("[Reminder] UserId={UserId} Input={Input}", task.UserId, phi.Redact(task.Input));

        task.Context.TryGetValue("patient_id", out var rawPatientId);
        _ = Guid.TryParse(rawPatientId, out var patientId);
        task.Context.TryGetValue("medication", out var medication);
        task.Context.TryGetValue("dosage", out var dosage);
        task.Context.TryGetValue("frequency", out var frequency);
        task.Context.TryGetValue("channel", out var channel);
        task.Context.TryGetValue("duration_days", out var rawDuration);
        task.Context.TryGetValue("risk_score", out var rawRisk);

        _ = int.TryParse(rawDuration, out var durationDays);
        _ = int.TryParse(rawRisk, out var riskScore);

        if (durationDays <= 0) durationDays = 30;
        if (riskScore <= 0) riskScore = ComputeDefaultRiskScore(task.Context);

        var reminderId = $"REM-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.CreateVersion7().ToString("N")[..8].ToUpperInvariant()}";
        var input = new MedicationReminderInput(
            PatientId: patientId == Guid.Empty ? Guid.CreateVersion7() : patientId,
            UserId: task.UserId,
            MedicationName: medication ?? ExtractMedicationFromText(task.Input),
            Dosage: dosage ?? string.Empty,
            Frequency: frequency ?? "once_daily",
            StartAt: DateTimeOffset.UtcNow.AddHours(1),
            DurationDays: durationDays,
            PreferredChannel: channel ?? "zalo",
            AdherenceRiskScore: riskScore,
            ReminderId: reminderId);

        var workflowId = $"reminder-{input.PatientId:N}-{Guid.CreateVersion7():N}";
        var started = await workflows.StartMedicationReminderAsync(input, workflowId, ct)
            .ConfigureAwait(false);

        if (tools.Find("create_reminder_record") is { } persistTool)
        {
            var toolCtx = new ToolInvocationContext(task.UserId, task.ConversationId ?? Guid.Empty, task.CorrelationId ?? string.Empty);
            var toolArgs = JsonSerializer.Serialize(new
            {
                reminder_id = reminderId,
                patient_id = input.PatientId.ToString(),
                workflow_id = started.WorkflowId,
                reminder_type = "medication",
                medication_name = input.MedicationName,
                dosage = input.Dosage,
                frequency = input.Frequency,
                start_at = input.StartAt.ToString("O"),
                duration_days = input.DurationDays,
                preferred_channel = input.PreferredChannel,
                adherence_risk_score = input.AdherenceRiskScore,
                status = "scheduled",
            });

            await persistTool.InvokeAsync(toolArgs, toolCtx, ct).ConfigureAwait(false);
        }

        log.LogInformation("[Reminder] Workflow started: {WorkflowId} RiskScore={Risk}",
            started.WorkflowId, riskScore);

        var riskLabel = riskScore switch
        {
            > 60 => "cao (nhắc 3 lần/lần)",
            > 30 => "trung bình (nhắc 2 lần/lần)",
            _ => "thấp (nhắc 1 lần/lần)",
        };

        return new AgentRoleResult(
            Role: Name,
            Success: true,
            Output:
                $"✅ Đã tạo lịch nhắc nhở cho {input.MedicationName} ({input.Dosage}).\n" +
                $"• Kênh: {input.PreferredChannel.ToUpperInvariant()}\n" +
                $"• Thời gian: {durationDays} ngày kể từ hôm nay\n" +
                $"• Tần suất nhắc: {riskLabel}\n\n" +
                "Bệnh nhân bỏ thuốc 3 lần liên tiếp → bác sĩ phụ trách sẽ được thông báo.",
            Metadata: new Dictionary<string, string>
            {
                ["reminder_id"] = reminderId,
                ["workflow_id"] = started.WorkflowId,
                ["risk_score"] = riskScore.ToString(),
                ["medication"] = input.MedicationName,
                ["channel"] = input.PreferredChannel,
            });
    }

    /// <summary>
    /// Default adherence risk score when not provided by caller.
    /// Mirrors the scoring logic in AGENT_WORKFLOWS section 4.2.
    /// </summary>
    private static int ComputeDefaultRiskScore(IReadOnlyDictionary<string, string> ctx)
    {
        var score = 0;

        if (ctx.TryGetValue("age", out var ageStr) && int.TryParse(ageStr, out var age) && age > 65)
            score += 20;

        if (ctx.TryGetValue("medication_count", out var medStr) && int.TryParse(medStr, out var medCount) && medCount > 5)
            score += 15;

        if (ctx.TryGetValue("chronic_conditions", out var ccStr) && int.TryParse(ccStr, out var cc))
            score += cc * 10;

        if (ctx.TryGetValue("no_show_history", out var nsStr) && nsStr == "true")
            score += 25;

        return Math.Min(score, 100);
    }

    private static string ExtractMedicationFromText(string input)
    {
        // Simple extraction — in production the LLM pipeline extracts entities first.
        var lower = input.ToLowerInvariant();
        foreach (var keyword in new[] { "metformin", "amlodipine", "warfarin", "lisinopril", "atorvastatin", "aspirin" })
        {
            if (lower.Contains(keyword))
                return char.ToUpperInvariant(keyword[0]) + keyword[1..];
        }
        return "thuốc theo đơn";
    }
}
