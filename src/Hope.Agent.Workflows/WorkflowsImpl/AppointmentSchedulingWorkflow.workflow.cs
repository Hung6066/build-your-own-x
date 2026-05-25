using Hope.Agent.Application.Workflows;
using Hope.Agent.Workflows.Activities;
using Microsoft.Extensions.Logging;
using Temporalio.Common;
using Temporalio.Workflows;

namespace Hope.Agent.Workflows.WorkflowsImpl;

/// <summary>
/// Durable appointment scheduling workflow.
/// Steps: specialty routing → slot lookup (parallel with insurance check) →
///        present options → confirm booking → send confirmation notification.
/// Reference: Epic AI Scheduling, Google MedLM + Deloitte provider search, Amazon Connect.
/// </summary>
[Workflow]
public class AppointmentSchedulingWorkflow
{
    private string status = "initializing";
    private readonly List<string> stepLog = [];

    [WorkflowRun]
    public async Task<AppointmentSchedulingResult> RunAsync(AppointmentSchedulingInput input)
    {
        var actOpts = new ActivityOptions
        {
            StartToCloseTimeout = TimeSpan.FromMinutes(2),
            RetryPolicy = new RetryPolicy
            {
                InitialInterval = TimeSpan.FromSeconds(2),
                BackoffCoefficient = 2.0F,
                MaximumInterval = TimeSpan.FromMinutes(1),
                MaximumAttempts = 5,
            },
        };

        Workflow.Logger.LogInformation("Scheduling workflow started for patient {Patient}", input.PatientId);

        // ── Step 1: Specialty routing ────────────────────────────────────────
        status = "routing-specialty";
        stepLog.Add(status);

        var routingCtx = new Dictionary<string, string>
        {
            ["patient_id"] = input.PatientId.ToString(),
            ["urgency"] = input.Urgency,
            ["preferred_doctor"] = input.PreferredDoctorId ?? string.Empty,
        };
        var routingDispatch = new AgentDispatchInput(
            input.UserId, "specialty_routing",
            $"Xác định chuyên khoa cho: {input.ChiefComplaint}",
            routingCtx, null, null,
            input.Urgency == "urgent" ? 1 : 5);

        var specialtyResult = await Workflow.ExecuteActivityAsync(
            (ClinicalActivities a) => a.DispatchAgentAsync(routingDispatch), actOpts);
        stepLog.Add($"specialty:{specialtyResult.Role}");

        // ── Step 2: Slot lookup + insurance check (parallel) ────────────────
        status = "fetching-slots-and-insurance";
        stepLog.Add(status);

        var slotCtx = new Dictionary<string, string>
        {
            ["patient_id"] = input.PatientId.ToString(),
            ["specialty"] = specialtyResult.Output,
            ["urgency"] = input.Urgency,
            ["preferred_time"] = input.PreferredTime ?? string.Empty,
        };
        var slotDispatch = new AgentDispatchInput(input.UserId, "his_slots",
            $"Tìm slot khám {specialtyResult.Output} trong 48h", slotCtx, null, null, 3);
        var slotTask = Workflow.ExecuteActivityAsync(
            (ClinicalActivities a) => a.DispatchAgentAsync(slotDispatch),
            actOpts);

        var insuranceCtx = new Dictionary<string, string>
        {
            ["patient_id"] = input.PatientId.ToString(),
            ["insurance_card"] = input.InsuranceCardNumber ?? string.Empty,
            ["specialty"] = specialtyResult.Output,
        };
        var insuranceDispatch = new AgentDispatchInput(input.UserId, "insurance",
            $"Kiểm tra bảo hiểm cho khám {specialtyResult.Output}", insuranceCtx, null, null, 3);
        var insuranceTask = Workflow.ExecuteActivityAsync(
            (ClinicalActivities a) => a.DispatchAgentAsync(insuranceDispatch),
            actOpts);

        await Task.WhenAll(slotTask, insuranceTask);

        var slots = slotTask.Result;
        var insurance = insuranceTask.Result;
        stepLog.Add($"slots:{slots.Role}");
        stepLog.Add($"insurance:{insurance.Role}");

        // ── Step 3: Optimize slot assignment via Min-Cost Max-Flow ────────────
        status = "optimizing-slot";
        stepLog.Add(status);

        var optimizeCtx = new Dictionary<string, string>
        {
            ["patient_id"] = input.PatientId.ToString(),
            ["specialty"] = specialtyResult.Output,
            ["urgency"] = input.Urgency,
            ["slots_json"] = slots.Output,          // full HIS slots JSON
            ["preferred_time"] = input.PreferredTime ?? string.Empty,
        };
        var optimizeDispatch = new AgentDispatchInput(
            input.UserId, "optimize_slots",
            $"Tối ưu phân bổ slot khám {specialtyResult.Output}",
            optimizeCtx, null, null, 2);
        var optimizeResult = await Workflow.ExecuteActivityAsync(
            (ClinicalActivities a) => a.DispatchAgentAsync(optimizeDispatch), actOpts);
        stepLog.Add($"optimize:{optimizeResult.Role}");

        var selectedDoctorName = ExtractBestDoctor(optimizeResult.Output, slots.Output, input.Urgency);
        var selectedTime = ExtractBestSlotTime(optimizeResult.Output, slots.Output, input.Urgency);
        var selectedSlotId = ExtractBestSlotId(optimizeResult.Output);
        var selectedDoctorId = ExtractBestDoctorId(optimizeResult.Output);
        stepLog.Add($"selected:{selectedDoctorName}@{selectedTime:HH:mm}");

        // ── Step 4: Confirm booking via HIS ─────────────────────────────────
        status = "booking";
        stepLog.Add(status);

        var bookingId = $"BK-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        var bookingCtx = new Dictionary<string, string>
        {
            ["patient_id"] = input.PatientId.ToString(),
            ["doctor"] = selectedDoctorId,         // doctor_id for HIS commit
            ["slot_id"] = selectedSlotId,
            ["time"] = selectedTime.ToString("O"),
            ["booking_id"] = bookingId,
            ["reason"] = input.ChiefComplaint,
        };
        var bookingDispatch = new AgentDispatchInput(input.UserId, "his_booking",
            $"Đặt lịch {bookingId}: {selectedDoctorName} lúc {selectedTime:HH:mm dd/MM}",
            bookingCtx, null, null, 2);
        await Workflow.ExecuteActivityAsync(
            (ClinicalActivities a) => a.DispatchAgentAsync(bookingDispatch),
            actOpts);
        stepLog.Add($"booked:{bookingId}");

        // ── Step 5: Send confirmation notification ───────────────────────────
        status = "notifying";
        stepLog.Add(status);

        var confirmBody =
            $"✅ Lịch hẹn của bạn đã được xác nhận!\n" +
            $"• Bác sĩ: {selectedDoctorName}\n" +
            $"• Thời gian: {selectedTime:HH:mm dd/MM/yyyy}\n" +
            $"• Mã đặt lịch: {bookingId}\n" +
            $"• Bảo hiểm: {SummarizeInsurance(insurance.Output)}\n\n" +
            "Vui lòng mang theo: CMND, thẻ BHYT, kết quả xét nghiệm gần nhất.\n" +
            "Nhắc nhở sẽ được gửi trước 24 giờ và 2 giờ.";

        var confirmMeta = new Dictionary<string, string> { ["booking_id"] = bookingId };
        var confirmNotify = new NotificationActivityInput(
            "zalo", "appointment.confirmed", "Xác nhận lịch hẹn",
            confirmBody, input.UserId, confirmMeta);
        await Workflow.ExecuteActivityAsync(
            (ClinicalActivities a) => a.NotifyAsync(confirmNotify),
            actOpts);

        status = "completed";
        stepLog.Add(status);

        return new AppointmentSchedulingResult(
            BookingId: bookingId,
            DoctorName: selectedDoctorName,
            Specialty: specialtyResult.Output,
            AppointmentTime: selectedTime,
            InsuranceSummary: SummarizeInsurance(insurance.Output),
            StepLog: stepLog.AsReadOnly());
    }

    [WorkflowQuery]
    public string GetStatus() => status;

    [WorkflowQuery]
    public IReadOnlyList<string> GetStepLog() => stepLog.AsReadOnly();

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts doctor_name from the MCMF assignment JSON.
    /// Falls back to parsing the raw HIS slots JSON, then a safe default.
    /// </summary>
    private static string ExtractBestDoctor(string assignmentJson, string slotsJson, string urgency)
    {
        _ = urgency;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(assignmentJson);
            if (doc.RootElement.TryGetProperty("doctor_id", out var did))
                return did.GetString() ?? "BS. Trực ban";
        }
        catch { /* fall through */ }

        // Fallback: parse HIS slots JSON
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(slotsJson);
            if (doc.RootElement.TryGetProperty("slots", out var s) && s.GetArrayLength() > 0
                && s[0].TryGetProperty("doctor_name", out var dn))
                return dn.GetString() ?? "BS. Trực ban";
        }
        catch { /* keep default */ }

        return "BS. Trực ban";
    }

    private static DateTimeOffset ExtractBestSlotTime(string assignmentJson, string slotsJson, string urgency)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(assignmentJson);
            if (doc.RootElement.TryGetProperty("time_iso", out var t)
                && DateTimeOffset.TryParse(t.GetString(), out var dt))
                return dt;
        }
        catch { /* fall through */ }

        // Fallback
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(slotsJson);
            if (doc.RootElement.TryGetProperty("slots", out var s) && s.GetArrayLength() > 0
                && s[0].TryGetProperty("time", out var t)
                && DateTimeOffset.TryParse(t.GetString(), out var dt))
                return dt;
        }
        catch { /* keep default */ }

        return urgency == "urgent"
            ? DateTimeOffset.UtcNow.AddHours(2)
            : DateTimeOffset.UtcNow.AddDays(1).Date.AddHours(9);
    }

    private static string ExtractBestSlotId(string assignmentJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(assignmentJson);
            if (doc.RootElement.TryGetProperty("slot_id", out var sid))
                return sid.GetString() ?? $"SLOT-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        }
        catch { /* keep default */ }
        return $"SLOT-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
    }

    private static string ExtractBestDoctorId(string assignmentJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(assignmentJson);
            if (doc.RootElement.TryGetProperty("doctor_id", out var did))
                return did.GetString() ?? "DR-GEN-001";
        }
        catch { /* keep default */ }
        return "DR-GEN-001";
    }

    private static string SummarizeInsurance(string insuranceOutput)
    {
        if (insuranceOutput.Contains("80%")) return "BHYT 80%";
        if (insuranceOutput.Contains("100%")) return "BHYT 100%";
        if (insuranceOutput.Contains("denied") || insuranceOutput.Contains("hết hạn")) return "Không có BHYT";
        return "Đang xác minh";
    }
}
