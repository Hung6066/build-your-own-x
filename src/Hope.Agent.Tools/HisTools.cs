using System.Text.Json;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Tools;

namespace Hope.Agent.Tools;

/// <summary>
/// HIS (Hospital Information System) tools for appointment scheduling workflows.
/// Production systems would replace these stubs with real HIS/EMR API calls
/// (e.g. Epic FHIR R4, Google Cloud Healthcare API, Vinmec HIS).
/// </summary>

// ── Specialty Routing ─────────────────────────────────────────────────────────

public sealed class MapSpecialtyTool : IAgentTool
{
    private static readonly Dictionary<string, string> SymptomMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["đau ngực"] = "Tim mạch",
        ["chest pain"] = "Tim mạch",
        ["đau bụng"] = "Tiêu hóa",
        ["abdominal pain"] = "Tiêu hóa",
        ["đau đầu"] = "Thần kinh",
        ["headache"] = "Thần kinh",
        ["ho"] = "Hô hấp",
        ["cough"] = "Hô hấp",
        ["khó thở"] = "Hô hấp",
        ["shortness of breath"] = "Hô hấp",
        ["da liễu"] = "Da liễu",
        ["rash"] = "Da liễu",
        ["mắt"] = "Nhãn khoa",
        ["eye"] = "Nhãn khoa",
        ["sản"] = "Sản phụ khoa",
        ["pregnancy"] = "Sản phụ khoa",
        ["nhi"] = "Nhi",
        ["child"] = "Nhi",
        ["xương"] = "Cơ xương khớp",
        ["bone"] = "Cơ xương khớp",
        ["tiểu đường"] = "Nội tiết",
        ["diabetes"] = "Nội tiết",
        ["sốt"] = "Nội tổng quát",
        ["fever"] = "Nội tổng quát",
    };

    public ToolDefinition Definition { get; } = new(
        "map_specialty",
        "Maps a patient's chief complaint or symptom description to the appropriate clinical specialty department.",
        """
        {
          "type": "object",
          "properties": {
            "complaint": {"type": "string", "description": "Chief complaint or symptom in Vietnamese or English"},
            "urgency": {"type": "string", "enum": ["normal", "soon", "urgent"], "default": "normal"}
          },
          "required": ["complaint"]
        }
        """);

    public Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;
        var complaint = args.GetProperty("complaint").GetString() ?? string.Empty;
        var urgency = args.TryGetProperty("urgency", out var u) ? u.GetString() : "normal";

        var specialty = SymptomMap.FirstOrDefault(kv =>
            complaint.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)).Value
            ?? "Nội tổng quát";

        // Urgent cases route to emergency department
        if (urgency == "urgent")
            specialty = complaint.Contains("ngực") || complaint.Contains("chest")
                ? "Cấp cứu Tim mạch"
                : "Cấp cứu";

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            complaint,
            specialty,
            urgency,
            triage_note = urgency == "urgent"
                ? "Ưu tiên cao — yêu cầu khám ngay trong 2 giờ"
                : "Lịch khám thông thường trong 24–48 giờ",
        }));
    }
}

// ── Available Slots Lookup ────────────────────────────────────────────────────

public sealed class GetDoctorSlotsTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "get_doctor_slots",
        "Queries the HIS for available appointment slots in a given specialty within the next 48 hours.",
        """
        {
          "type": "object",
          "properties": {
            "specialty": {"type": "string"},
            "urgency": {"type": "string", "default": "normal"},
            "preferred_time": {"type": "string", "description": "ISO 8601 datetime hint, optional"},
            "preferred_doctor_id": {"type": "string", "description": "Doctor ID if patient has a preference"}
          },
          "required": ["specialty"]
        }
        """);

    public Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;
        var specialty = args.GetProperty("specialty").GetString() ?? "Nội tổng quát";
        var urgency = args.TryGetProperty("urgency", out var u) ? u.GetString() : "normal";
        var preferredDoctorId = args.TryGetProperty("preferred_doctor_id", out var d) ? d.GetString() : null;

        // Deterministic stubs simulating real HIS slot availability
        var baseTime = urgency == "urgent"
            ? DateTimeOffset.UtcNow.AddHours(1)
            : DateTimeOffset.UtcNow.AddDays(1).Date.AddHours(8);

        var slots = new[]
        {
            new
            {
                slot_id = $"SLOT-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                doctor_id = preferredDoctorId ?? $"DR-{specialty[..Math.Min(3, specialty.Length)].ToUpperInvariant()}-001",
                doctor_name = $"BS. Trần Thị Lan — {specialty}",
                specialty,
                time = baseTime.AddHours(0).ToString("O"),
                room = "P.201",
                available = true,
            },
            new
            {
                slot_id = $"SLOT-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                doctor_id = $"DR-{specialty[..Math.Min(3, specialty.Length)].ToUpperInvariant()}-002",
                doctor_name = $"BS. Nguyễn Văn Minh — {specialty}",
                specialty,
                time = baseTime.AddHours(2).ToString("O"),
                room = "P.205",
                available = true,
            },
        };

        return Task.FromResult(JsonSerializer.Serialize(new { specialty, urgency, slots }));
    }
}

// ── HIS Booking Commit ────────────────────────────────────────────────────────

public sealed class CommitBookingTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "commit_booking",
        "Writes a confirmed appointment booking into the HIS. Returns the booking confirmation record.",
        """
        {
          "type": "object",
          "properties": {
            "patient_id": {"type": "string"},
            "doctor_id": {"type": "string"},
            "slot_id": {"type": "string"},
            "reason": {"type": "string"},
            "booking_id": {"type": "string", "description": "Pre-generated booking ID for idempotency"}
          },
          "required": ["patient_id", "doctor_id", "slot_id"]
        }
        """);

    public Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;
        var bookingId = args.TryGetProperty("booking_id", out var b) && b.GetString() is { } bid
            ? bid
            : $"BK-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            booking_id = bookingId,
            patient_id = args.GetProperty("patient_id").GetString(),
            doctor_id = args.GetProperty("doctor_id").GetString(),
            slot_id = args.GetProperty("slot_id").GetString(),
            status = "confirmed",
            confirmed_at = DateTimeOffset.UtcNow.ToString("O"),
            hl7_message_id = $"MSG-{Guid.NewGuid():N}",
        }));
    }
}

// ── Medication Schedule ───────────────────────────────────────────────────────

public sealed class GetMedicationScheduleTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "get_medication_schedule",
        "Retrieves a patient's active medication prescriptions from the HIS pharmacy module.",
        """
        {
          "type": "object",
          "properties": {
            "patient_id": {"type": "string"},
            "include_past": {"type": "boolean", "default": false}
          },
          "required": ["patient_id"]
        }
        """);

    public Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;
        var patientId = args.GetProperty("patient_id").GetString();
        var includePast = args.TryGetProperty("include_past", out var ip) && ip.GetBoolean();

        var meds = new[]
        {
            new
            {
                prescription_id = $"RX-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                medication_name = "Metformin",
                dosage = "500mg",
                frequency = "twice_daily",
                start_date = DateTimeOffset.UtcNow.AddDays(-30).ToString("yyyy-MM-dd"),
                end_date = DateTimeOffset.UtcNow.AddDays(60).ToString("yyyy-MM-dd"),
                status = "active",
                adherence_rate = 0.75,
            },
            new
            {
                prescription_id = $"RX-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                medication_name = "Amlodipine",
                dosage = "5mg",
                frequency = "once_daily",
                start_date = DateTimeOffset.UtcNow.AddDays(-90).ToString("yyyy-MM-dd"),
                end_date = DateTimeOffset.UtcNow.AddDays(90).ToString("yyyy-MM-dd"),
                status = "active",
                adherence_rate = 0.90,
            },
        };

        var result = includePast ? meds : meds.Where(m => m.status == "active").ToArray();
        return Task.FromResult(JsonSerializer.Serialize(new { patient_id = patientId, prescriptions = result }));
    }
}
