using System.Text.Json;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Tools;
using Hope.Agent.Application.Workflows;

namespace Hope.Agent.Tools;

public sealed class PersistMedicalSummaryTool(IMedicalSummaryStore summaryStore) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "persist_medical_summary",
        "Persists a generated medical summary/SOAP note to Postgres and returns the saved summary record.",
        """
        {
          "type": "object",
          "properties": {
            "summary_id": {"type": "string", "description": "Pre-generated summary ID for idempotency"},
            "patient_id": {"type": "string"},
            "summary_type": {"type": "string", "description": "soap | pre_visit | patient_friendly | discharge"},
            "audience": {"type": "string", "description": "clinician | patient"},
            "specialty": {"type": "string"},
            "source_context": {"type": "string"},
            "summary_text": {"type": "string"},
            "model": {"type": "string"},
            "status": {"type": "string", "default": "completed"}
          },
          "required": ["patient_id", "summary_text"]
        }
        """);

    public async Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;
        var summaryId = args.TryGetProperty("summary_id", out var sid) && !string.IsNullOrWhiteSpace(sid.GetString())
            ? sid.GetString()!
            : $"SUM-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.CreateVersion7().ToString("N")[..8].ToUpperInvariant()}";

        var patientIdText = args.GetProperty("patient_id").GetString();
        var patientId = Guid.TryParse(patientIdText, out var parsedPatientId)
            ? parsedPatientId
            : (Guid?)null;
        var now = DateTimeOffset.UtcNow;

        var summaryType = args.TryGetProperty("summary_type", out var st) ? st.GetString() ?? "soap" : "soap";
        var audience = args.TryGetProperty("audience", out var aud) ? aud.GetString() ?? "clinician" : "clinician";
        var specialty = args.TryGetProperty("specialty", out var sp) ? sp.GetString() : null;
        var sourceContext = args.TryGetProperty("source_context", out var src) ? src.GetString() ?? string.Empty : string.Empty;
        var summaryText = args.GetProperty("summary_text").GetString() ?? string.Empty;
        var model = args.TryGetProperty("model", out var modelValue) ? modelValue.GetString() : null;
        var status = args.TryGetProperty("status", out var statusValue) ? statusValue.GetString() ?? "completed" : "completed";

        await summaryStore.SaveAsync(new MedicalSummaryWrite(
            SummaryId: summaryId,
            PatientId: patientId,
            UserId: context.UserId,
            SummaryType: summaryType,
            Audience: audience,
            Specialty: specialty,
            SourceContext: sourceContext,
            SummaryText: summaryText,
            Model: model,
            Status: status,
            CreatedAt: now,
            CorrelationId: context.CorrelationId), ct).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            summary_id = summaryId,
            patient_id = patientIdText,
            summary_type = summaryType,
            audience,
            status,
            persisted_at = now.ToString("O"),
        });
    }
}

public sealed class CreateReminderRecordTool(IReminderRecordStore reminderStore) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "create_reminder_record",
        "Persists a durable medication or follow-up reminder record to Postgres before/after workflow scheduling.",
        """
        {
          "type": "object",
          "properties": {
            "reminder_id": {"type": "string", "description": "Pre-generated reminder ID for idempotency"},
            "patient_id": {"type": "string"},
            "workflow_id": {"type": "string"},
            "reminder_type": {"type": "string", "description": "medication | follow_up"},
            "medication_name": {"type": "string"},
            "dosage": {"type": "string"},
            "frequency": {"type": "string"},
            "start_at": {"type": "string", "format": "date-time"},
            "duration_days": {"type": "integer"},
            "preferred_channel": {"type": "string"},
            "adherence_risk_score": {"type": "integer"},
            "status": {"type": "string", "default": "scheduled"}
          },
          "required": ["patient_id", "medication_name", "frequency", "start_at", "duration_days"]
        }
        """);

    public async Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;
        var reminderId = args.TryGetProperty("reminder_id", out var rid) && !string.IsNullOrWhiteSpace(rid.GetString())
            ? rid.GetString()!
            : $"REM-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.CreateVersion7().ToString("N")[..8].ToUpperInvariant()}";

        var patientIdText = args.GetProperty("patient_id").GetString();
        var patientId = Guid.TryParse(patientIdText, out var parsedPatientId)
            ? parsedPatientId
            : context.UserId;
        var startAt = args.TryGetProperty("start_at", out var startValue)
            && DateTimeOffset.TryParse(startValue.GetString(), out var parsedStart)
                ? parsedStart
                : DateTimeOffset.UtcNow.AddHours(1);
        var durationDays = args.TryGetProperty("duration_days", out var durationValue)
            && durationValue.TryGetInt32(out var parsedDuration)
                ? parsedDuration
                : 30;
        var riskScore = args.TryGetProperty("adherence_risk_score", out var riskValue)
            && riskValue.TryGetInt32(out var parsedRisk)
                ? parsedRisk
                : 30;
        var now = DateTimeOffset.UtcNow;

        var reminderType = args.TryGetProperty("reminder_type", out var typeValue) ? typeValue.GetString() ?? "medication" : "medication";
        var medicationName = args.GetProperty("medication_name").GetString() ?? "thuốc theo đơn";
        var dosage = args.TryGetProperty("dosage", out var dosageValue) ? dosageValue.GetString() : null;
        var frequency = args.GetProperty("frequency").GetString() ?? "once_daily";
        var preferredChannel = args.TryGetProperty("preferred_channel", out var channelValue) ? channelValue.GetString() ?? "zalo" : "zalo";
        var workflowId = args.TryGetProperty("workflow_id", out var workflowValue) ? workflowValue.GetString() : null;
        var status = args.TryGetProperty("status", out var statusValue) ? statusValue.GetString() ?? "scheduled" : "scheduled";

        await reminderStore.SaveAsync(new ReminderRecordWrite(
            ReminderId: reminderId,
            PatientId: patientId,
            UserId: context.UserId,
            WorkflowId: workflowId,
            ReminderType: reminderType,
            MedicationName: medicationName,
            Dosage: dosage,
            Frequency: frequency,
            StartAt: startAt,
            DurationDays: durationDays,
            PreferredChannel: preferredChannel,
            AdherenceRiskScore: riskScore,
            Status: status,
            CreatedAt: now,
            CorrelationId: context.CorrelationId), ct).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            reminder_id = reminderId,
            patient_id = patientId.ToString(),
            workflow_id = workflowId,
            medication_name = medicationName,
            status,
            persisted_at = now.ToString("O"),
        });
    }
}

public sealed class UpdateReminderStatusTool(IReminderRecordStore reminderStore) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "update_reminder_status",
        "Updates a persisted reminder record with confirmation/missed/escalation status.",
        """
        {
          "type": "object",
          "properties": {
            "reminder_id": {"type": "string"},
            "status": {"type": "string"},
            "confirmed_count": {"type": "integer"},
            "missed_count": {"type": "integer"},
            "last_confirmed_at": {"type": "string", "format": "date-time"},
            "last_missed_at": {"type": "string", "format": "date-time"},
            "escalation_reason": {"type": "string"}
          },
          "required": ["reminder_id", "status"]
        }
        """);

    public async Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;
        var reminderId = args.GetProperty("reminder_id").GetString() ?? string.Empty;
        var status = args.GetProperty("status").GetString() ?? "scheduled";
        var confirmedCount = args.TryGetProperty("confirmed_count", out var cc) && cc.TryGetInt32(out var ccv)
            ? ccv
            : (int?)null;
        var missedCount = args.TryGetProperty("missed_count", out var mc) && mc.TryGetInt32(out var mcv)
            ? mcv
            : (int?)null;
        var lastConfirmedAt = args.TryGetProperty("last_confirmed_at", out var lc)
            && DateTimeOffset.TryParse(lc.GetString(), out var lcv)
                ? lcv
                : (DateTimeOffset?)null;
        var lastMissedAt = args.TryGetProperty("last_missed_at", out var lm)
            && DateTimeOffset.TryParse(lm.GetString(), out var lmv)
                ? lmv
                : (DateTimeOffset?)null;
        var escalationReason = args.TryGetProperty("escalation_reason", out var er) ? er.GetString() : null;
        var now = DateTimeOffset.UtcNow;

        await reminderStore.UpdateStatusAsync(new ReminderStatusWrite(
            ReminderId: reminderId,
            Status: status,
            ConfirmedCount: confirmedCount,
            MissedCount: missedCount,
            LastConfirmedAt: lastConfirmedAt,
            LastMissedAt: lastMissedAt,
            EscalationReason: escalationReason,
            UpdatedAt: now), ct).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            reminder_id = reminderId,
            status,
            updated_at = now.ToString("O"),
        });
    }
}
