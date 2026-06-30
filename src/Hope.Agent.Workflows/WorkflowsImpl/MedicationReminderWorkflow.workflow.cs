using Hope.Agent.Application.Workflows;
using Hope.Agent.Workflows.Activities;
using Microsoft.Extensions.Logging;
using Temporalio.Common;
using Temporalio.Workflows;

namespace Hope.Agent.Workflows.WorkflowsImpl;

/// <summary>
/// Durable medication reminder workflow.
/// Sends reminders on schedule, captures patient confirmations, and escalates to
/// care team after 3 consecutive missed doses/appointments.
/// Reference: Epic MyChart reminders, Amazon Connect outbound call, Temporal durable timers.
/// </summary>
[Workflow]
public class MedicationReminderWorkflow
{
    private string status = "scheduled";
    private int missedCount;
    private int confirmedCount;
    private ReminderConfirmation? latestConfirmation;

    [WorkflowRun]
    public async Task RunAsync(MedicationReminderInput input)
    {
        var actOpts = WorkflowCommon.DefaultActivityOptions(TimeSpan.FromMinutes(1));

        Workflow.Logger.LogInformation(
            "Reminder workflow started for patient {Patient} medication {Med}",
            input.PatientId, input.MedicationName);

        var endAt = input.StartAt.AddDays(input.DurationDays);
        var reminderId = input.ReminderId ?? $"REM-{input.PatientId:N}";

        // Reminder frequency depends on adherence risk score (mirrors AGENT_WORKFLOWS section 4.2)
        var remindersPerDose = input.AdherenceRiskScore switch
        {
            > 60 => 3,
            > 30 => 2,
            _ => 1,
        };

        // Send reminders until end of prescription duration
        while (Workflow.UtcNow < endAt)
        {
            // Wait until next scheduled reminder time
            var delay = input.StartAt - Workflow.UtcNow;
            if (delay > TimeSpan.Zero)
                await Workflow.DelayAsync(delay);

            status = "sending-reminder";
            latestConfirmation = null;

            // Build personalised reminder body
            var streakNote = confirmedCount > 0
                ? $"Bạn đã duy trì đều đặn {confirmedCount} lần liên tiếp — rất tốt! 🎉"
                : string.Empty;

            var body =
                $"⏰ Đã đến giờ uống {input.MedicationName} {input.Dosage}.\n" +
                (streakNote.Length > 0 ? streakNote + "\n" : string.Empty) +
                "Bấm ✅ để ghi nhận đã uống.";

            // ── Throttle check (token-bucket) before sending the reminder batch ──
            // Build a notification list for all remindersPerDose attempts and let
            // ThrottleNotificationsTool decide which ones to send, delay, or drop.
            var throttleNotifs = System.Text.Json.JsonSerializer.Serialize(
                Enumerable.Range(0, remindersPerDose).Select(i => new
                {
                    notification_id = $"REMIND-{input.MedicationName}-dose-{i + 1}",
                    patient_id = input.PatientId.ToString(),
                    channel = input.PreferredChannel,
                    urgency = input.AdherenceRiskScore > 60 ? "high" : "medium",
                    message = body,
                }).ToArray());

            var throttleCtx = new Dictionary<string, string> { ["notifications_json"] = throttleNotifs };
            var throttleDispatch = new AgentDispatchInput(
                input.UserId, "throttle_notify",
                $"Rate-limit {remindersPerDose} {input.PreferredChannel} reminders for {input.MedicationName}",
                throttleCtx, null, null, 5);
            var throttleResult = await Workflow.ExecuteActivityAsync(
                (ClinicalActivities a) => a.DispatchAgentAsync(throttleDispatch), actOpts);

            // Parse throttle decisions: only send attempts approved by the bucket
            var decisions = ParseThrottleDecisions(throttleResult.Output, remindersPerDose);

            for (var attempt = 0; attempt < remindersPerDose; attempt++)
            {
                var decision = attempt < decisions.Count ? decisions[attempt] : "send";
                if (decision == "drop")
                {
                    Workflow.Logger.LogInformation(
                        "Reminder attempt {N} for {Med} dropped by throttle (channel={Ch})",
                        attempt + 1, input.MedicationName, input.PreferredChannel);
                    continue;
                }

                var reminderMeta = new Dictionary<string, string>
                {
                    ["medication"] = input.MedicationName,
                    ["dosage"] = input.Dosage,
                    ["attempt"] = (attempt + 1).ToString(),
                    ["throttle_decision"] = decision,
                };
                var reminderNotify = new NotificationActivityInput(
                    input.PreferredChannel, "medication.reminder",
                    $"Nhắc uống {input.MedicationName}",
                    body, input.UserId, reminderMeta);
                await Workflow.ExecuteActivityAsync(
                    (ClinicalActivities a) => a.NotifyAsync(reminderNotify),
                    actOpts);

                if (attempt < remindersPerDose - 1)
                    await Workflow.DelayAsync(TimeSpan.FromMinutes(30));
            }

            status = "awaiting-confirmation";

            // Wait up to the next dose interval for patient to confirm
            var doseInterval = ParseDoseInterval(input.Frequency);
            var confirmed = await Workflow.WaitConditionAsync(
                () => latestConfirmation is not null,
                doseInterval);

            if (confirmed && latestConfirmation?.Confirmed == true)
            {
                confirmedCount++;
                missedCount = 0; // reset consecutive miss count
                status = "confirmed";
                await PersistReminderStatusAsync(input, reminderId, status, actOpts, Workflow.UtcNow);
                Workflow.Logger.LogInformation("Patient {Patient} confirmed {Med} dose #{Count}",
                    input.PatientId, input.MedicationName, confirmedCount);
            }
            else
            {
                missedCount++;
                status = $"missed-{missedCount}";
                await PersistReminderStatusAsync(input, reminderId, status, actOpts, lastMissedAt: Workflow.UtcNow);
                Workflow.Logger.LogWarning("Patient {Patient} missed {Med} dose. Consecutive misses: {Miss}",
                    input.PatientId, input.MedicationName, missedCount);

                // Escalate to care team after 3 consecutive misses (mirrors AGENT_WORKFLOWS section 4.3)
                if (missedCount >= 3)
                {
                    await EscalateToCareTeamAsync(input, actOpts);
                    await PersistReminderStatusAsync(
                        input,
                        reminderId,
                        "escalated",
                        actOpts,
                        lastMissedAt: Workflow.UtcNow,
                        escalationReason: $"Missed {input.MedicationName} 3 consecutive times");
                    missedCount = 0; // reset after escalation — don't spam every dose
                }
            }

            // Advance to next dose time
            input = input with { StartAt = input.StartAt + doseInterval };
        }

        status = "completed";
        await PersistReminderStatusAsync(input, reminderId, status, actOpts);
        Workflow.Logger.LogInformation("Reminder workflow completed for patient {Patient} medication {Med}",
            input.PatientId, input.MedicationName);
    }

    [WorkflowSignal]
    public Task ConfirmDoseAsync(ReminderConfirmation confirmation)
    {
        latestConfirmation = confirmation;
        return Task.CompletedTask;
    }

    [WorkflowQuery]
    public string GetStatus() => status;

    [WorkflowQuery]
    public int GetMissedCount() => missedCount;

    [WorkflowQuery]
    public int GetConfirmedCount() => confirmedCount;

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Task EscalateToCareTeamAsync(MedicationReminderInput input, ActivityOptions actOpts)
    {
        var escalateMeta = new Dictionary<string, string>
        {
            ["patient_id"] = input.PatientId.ToString(),
            ["medication"] = input.MedicationName,
            ["missed_count"] = "3",
        };
        var escalateNotify = new NotificationActivityInput(
            "care-team", "medication.missed",
            "⚠️ Cảnh báo tuân thủ thuốc",
            $"Bệnh nhân {input.PatientId} đã bỏ {input.MedicationName} " +
            $"3 lần liên tiếp. Vui lòng liên hệ bệnh nhân.",
            input.UserId, escalateMeta);
        return Workflow.ExecuteActivityAsync(
            (ClinicalActivities a) => a.NotifyAsync(escalateNotify),
            actOpts);
    }

    private Task PersistReminderStatusAsync(
        MedicationReminderInput input,
        string reminderId,
        string newStatus,
        ActivityOptions actOpts,
        DateTimeOffset? lastConfirmedAt = null,
        DateTimeOffset? lastMissedAt = null,
        string? escalationReason = null)
    {
        var ctx = new Dictionary<string, string>
        {
            ["reminder_id"] = reminderId,
            ["status"] = newStatus,
            ["confirmed_count"] = confirmedCount.ToString(),
            ["missed_count"] = missedCount.ToString(),
        };

        if (lastConfirmedAt.HasValue)
            ctx["last_confirmed_at"] = lastConfirmedAt.Value.ToString("O");
        if (lastMissedAt.HasValue)
            ctx["last_missed_at"] = lastMissedAt.Value.ToString("O");
        if (!string.IsNullOrWhiteSpace(escalationReason))
            ctx["escalation_reason"] = escalationReason;

        var dispatch = new AgentDispatchInput(
            input.UserId,
            "update_reminder_status",
            $"Update reminder {reminderId} status to {newStatus}",
            ctx,
            null,
            null,
            5);

        return Workflow.ExecuteActivityAsync(
            (ClinicalActivities a) => a.DispatchAgentAsync(dispatch),
            actOpts);
    }

    private static TimeSpan ParseDoseInterval(string frequency)
        => frequency.ToLowerInvariant() switch
        {
            "twice_daily" or "bid" => TimeSpan.FromHours(12),
            "three_daily" or "tid" => TimeSpan.FromHours(8),
            "weekly" => TimeSpan.FromDays(7),
            _ => TimeSpan.FromHours(24), // once_daily default
        };

    /// <summary>
    /// Parses the decisions array from ThrottleNotificationsTool output.
    /// Returns a list of "send" | "delay" | "drop" strings, one per attempt.
    /// Falls back to all-"send" if the output is unparseable.
    /// </summary>
    private static IReadOnlyList<string> ParseThrottleDecisions(string throttleOutput, int expectedCount)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(throttleOutput);
            if (doc.RootElement.TryGetProperty("decisions", out var decisions))
            {
                return decisions.EnumerateArray()
                    .Select(d => d.TryGetProperty("decision", out var dec) ? dec.GetString() ?? "send" : "send")
                    .ToList();
            }
        }
        catch { /* fall through to default */ }

        return Enumerable.Repeat("send", expectedCount).ToList();
    }
}
