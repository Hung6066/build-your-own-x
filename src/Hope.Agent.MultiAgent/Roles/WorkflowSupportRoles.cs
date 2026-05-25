using System.Globalization;
using System.Text.Json;
using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Tools;

namespace Hope.Agent.MultiAgent.Roles;

/// <summary>
/// Intra-workflow agent roles — used exclusively by Temporal workflow steps via ClinicalActivities.DispatchAgentAsync.
/// These roles call IAgentTool implementations directly and never start new workflows,
/// eliminating any risk of circular dispatch.
/// </summary>

// ── Specialty Routing ─────────────────────────────────────────────────────────

internal sealed class SpecialtyRoutingAgent(IToolRegistry tools) : IAgentRole
{
    public string Name => "specialty-routing";
    public string Description => "Maps chief complaint / symptoms to the correct clinical specialty department.";
    public IReadOnlyList<string> Intents { get; } = ["specialty_routing", "map_specialty", "route_specialty"];

    public async Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
    {
        var tool = tools.Find("map_specialty");
        if (tool is null) return new AgentRoleResult(Name, false, "map_specialty tool unavailable");

        var urgency = task.Context.GetValueOrDefault("urgency", "normal");
        var ctx = new ToolInvocationContext(task.UserId, task.ConversationId ?? Guid.Empty, task.CorrelationId ?? string.Empty);
        var args = JsonSerializer.Serialize(new { complaint = task.Input, urgency });
        var output = await tool.InvokeAsync(args, ctx, ct);

        // Extract specialty name for downstream steps
        var specialty = "Nội tổng quát";
        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("specialty", out var sp))
                specialty = sp.GetString() ?? specialty;
        }
        catch { /* keep default */ }

        return new AgentRoleResult(Name, true, specialty, new Dictionary<string, string>
        {
            ["specialty"] = specialty,
            ["raw"] = output,
        });
    }
}

// ── HIS Slot Lookup ───────────────────────────────────────────────────────────

internal sealed class HisSlotsAgent(IToolRegistry tools) : IAgentRole
{
    public string Name => "his-slots";
    public string Description => "Fetches available appointment slots from the Hospital Information System for a given specialty and time window.";
    public IReadOnlyList<string> Intents { get; } = ["his_slots", "get_slots", "available_slots"];

    public async Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
    {
        var tool = tools.Find("get_doctor_slots");
        if (tool is null) return new AgentRoleResult(Name, false, "get_doctor_slots tool unavailable");

        var ctx = new ToolInvocationContext(task.UserId, task.ConversationId ?? Guid.Empty, task.CorrelationId ?? string.Empty);
        var specialty = task.Context.GetValueOrDefault("specialty", task.Input);
        var urgency = task.Context.GetValueOrDefault("urgency", "normal");
        var preferredTime = task.Context.GetValueOrDefault("preferred_time", string.Empty);
        var preferredDoctor = task.Context.GetValueOrDefault("preferred_doctor", string.Empty);

        var args = JsonSerializer.Serialize(new
        {
            specialty,
            urgency,
            preferred_time = preferredTime,
            preferred_doctor_id = preferredDoctor,
        });
        var output = await tool.InvokeAsync(args, ctx, ct);

        // Return the full slots JSON as Output so downstream optimization steps can read it.
        // Extract top doctor name for convenience and store in metadata.
        var doctorName = "BS. Trực ban";
        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("slots", out var slots) && slots.GetArrayLength() > 0)
            {
                var first = slots[0];
                if (first.TryGetProperty("doctor_name", out var dn))
                    doctorName = dn.GetString() ?? doctorName;
            }
        }
        catch { /* keep default */ }

        return new AgentRoleResult(Name, true, output, new Dictionary<string, string>
        {
            ["top_doctor"] = doctorName,
        });
    }
}

// ── HIS Booking Commit ────────────────────────────────────────────────────────

internal sealed class HisBookingAgent(IToolRegistry tools) : IAgentRole
{
    public string Name => "his-booking";
    public string Description => "Commits a confirmed appointment booking into the HIS and returns the booking ID.";
    public IReadOnlyList<string> Intents { get; } = ["his_booking", "commit_booking", "confirm_booking"];

    public async Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
    {
        var tool = tools.Find("commit_booking");
        if (tool is null) return new AgentRoleResult(Name, false, "commit_booking tool unavailable");

        var ctx = new ToolInvocationContext(task.UserId, task.ConversationId ?? Guid.Empty, task.CorrelationId ?? string.Empty);
        var args = JsonSerializer.Serialize(new
        {
            patient_id = task.Context.GetValueOrDefault("patient_id", task.UserId.ToString()),
            doctor_id = task.Context.GetValueOrDefault("doctor", "DR-GEN-001"),
            slot_id = task.Context.GetValueOrDefault("slot_id", $"SLOT-{Guid.NewGuid().ToString("N")[..8]}"),
            reason = task.Context.GetValueOrDefault("reason", task.Input),
            booking_id = task.Context.GetValueOrDefault("booking_id", string.Empty),
        });
        var output = await tool.InvokeAsync(args, ctx, ct);
        return new AgentRoleResult(Name, true, output);
    }
}

// ── Medication Lookup ─────────────────────────────────────────────────────────

internal sealed class MedicationLookupAgent(IToolRegistry tools) : IAgentRole
{
    public string Name => "medication-lookup";
    public string Description => "Retrieves a patient's active medication prescriptions from the HIS pharmacy module.";
    public IReadOnlyList<string> Intents { get; } = ["medication_lookup", "get_medication", "medication_schedule"];

    public async Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
    {
        var tool = tools.Find("get_medication_schedule");
        if (tool is null) return new AgentRoleResult(Name, false, "get_medication_schedule tool unavailable");

        var ctx = new ToolInvocationContext(task.UserId, task.ConversationId ?? Guid.Empty, task.CorrelationId ?? string.Empty);
        var patientId = task.Context.GetValueOrDefault("patient_id", task.UserId.ToString());
        var args = JsonSerializer.Serialize(new { patient_id = patientId, include_past = false });
        var output = await tool.InvokeAsync(args, ctx, ct);
        return new AgentRoleResult(Name, true, output);
    }
}

// ── Audit Execution (4-in-1 intra-workflow role) ──────────────────────────────

internal sealed class AuditExecutionAgent(IToolRegistry tools, ILLMRouter llm) : IAgentRole
{
    public string Name => "audit-execution";
    public string Description => "Handles all intra-workflow audit steps: log collection, anomaly detection, narrative writing, and tamper-evident export.";
    public IReadOnlyList<string> Intents { get; } =
        ["audit_collect", "audit_analyze", "audit_narrate", "audit_export"];

    public Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
        => task.Intent switch
        {
            "audit_collect" => CollectLogsAsync(task, ct),
            "audit_analyze" => AnalyzeAnomaliesAsync(task, ct),
            "audit_narrate" => GenerateNarrativeAsync(task, ct),
            "audit_export" => ExportReportAsync(task, ct),
            _ => Task.FromResult(new AgentRoleResult(Name, false, $"Unknown audit intent: {task.Intent}")),
        };

    private async Task<AgentRoleResult> CollectLogsAsync(AgentTask task, CancellationToken ct)
    {
        var tool = tools.Find("collect_audit_logs");
        if (tool is null) return new AgentRoleResult(Name, false, "collect_audit_logs tool unavailable");

        var ctx = new ToolInvocationContext(task.UserId, task.ConversationId ?? Guid.Empty, task.CorrelationId ?? string.Empty);
        var args = JsonSerializer.Serialize(new
        {
            report_type = task.Context.GetValueOrDefault("report_type", "operational"),
            period_start = task.Context.GetValueOrDefault("period_start", DateTimeOffset.UtcNow.AddDays(-30).ToString("O")),
            period_end = task.Context.GetValueOrDefault("period_end", DateTimeOffset.UtcNow.ToString("O")),
            report_id = task.Context.GetValueOrDefault("report_id", string.Empty),
        });
        var output = await tool.InvokeAsync(args, ctx, ct);
        return new AgentRoleResult(Name, true, output, new Dictionary<string, string> { ["step"] = "collect" });
    }

    private async Task<AgentRoleResult> AnalyzeAnomaliesAsync(AgentTask task, CancellationToken ct)
    {
        var tool = tools.Find("detect_audit_anomalies");
        if (tool is null) return new AgentRoleResult(Name, false, "detect_audit_anomalies tool unavailable");

        var ctx = new ToolInvocationContext(task.UserId, task.ConversationId ?? Guid.Empty, task.CorrelationId ?? string.Empty);
        var rawData = task.Context.GetValueOrDefault("raw_data", task.Input);
        var args = JsonSerializer.Serialize(new { metrics_json = rawData, sensitivity = "medium" });
        var output = await tool.InvokeAsync(args, ctx, ct);
        return new AgentRoleResult(Name, true, output, new Dictionary<string, string> { ["step"] = "analyze" });
    }

    private async Task<AgentRoleResult> GenerateNarrativeAsync(AgentTask task, CancellationToken ct)
    {
        var metricsData = task.Context.GetValueOrDefault("metrics_data", string.Empty);
        var anomalyData = task.Context.GetValueOrDefault("anomaly_data", string.Empty);
        var period = task.Context.GetValueOrDefault("period", "kỳ báo cáo");
        var reportType = task.Context.GetValueOrDefault("report_type", "operational");

        var chat = llm.SelectChat();
        var resp = await chat.CompleteAsync(new ChatRequest(
            [
                new ChatMessage("system",
                    "Bạn là chuyên gia phân tích vận hành bệnh viện của Hope.Agent. " +
                    "Viết tường thuật báo cáo bằng tiếng Việt, súc tích, chuyên nghiệp. " +
                    "Chỉ dùng dữ liệu được cung cấp. Kết thúc bằng phần 'Khuyến nghị'."),
                new ChatMessage("user",
                    $"Loại báo cáo: {reportType}\n" +
                    $"Kỳ: {period}\n\n" +
                    $"Dữ liệu chỉ số:\n{metricsData}\n\n" +
                    $"Dữ liệu bất thường:\n{anomalyData}\n\n" +
                    "Viết tường thuật đầy đủ."),
            ],
            Temperature: 0.3f), ct);

        return new AgentRoleResult(Name, true, resp.Content, new Dictionary<string, string> { ["step"] = "narrate" });
    }

    private async Task<AgentRoleResult> ExportReportAsync(AgentTask task, CancellationToken ct)
    {
        var tool = tools.Find("export_audit_report");
        if (tool is null) return new AgentRoleResult(Name, false, "export_audit_report tool unavailable");

        var ctx = new ToolInvocationContext(task.UserId, task.ConversationId ?? Guid.Empty, task.CorrelationId ?? string.Empty);
        var args = JsonSerializer.Serialize(new
        {
            report_id = task.Context.GetValueOrDefault("report_id", "UNKNOWN"),
            narrative = task.Context.GetValueOrDefault("narrative", task.Input),
            anomalies_json = task.Context.GetValueOrDefault("anomalies", string.Empty),
            metrics_json = task.Context.GetValueOrDefault("narrative", string.Empty),
            format = task.Context.GetValueOrDefault("format", "json"),
        });
        var output = await tool.InvokeAsync(args, ctx, ct);
        return new AgentRoleResult(Name, true, output, new Dictionary<string, string> { ["step"] = "export" });
    }
}

// ── Optimization (3-in-1 intra-workflow role) ─────────────────────────────────

/// <summary>
/// Handles all intra-workflow optimization intents:
///   - optimize_slots  : Min-Cost Max-Flow slot assignment (wraps OptimizeBatchAppointmentsTool)
///   - rank_triage     : Weighted EDF patient ranking     (wraps RankTriagePatientsTool)
///   - throttle_notify : Token-bucket rate limiting        (wraps ThrottleNotificationsTool)
///
/// Never starts a new workflow — only calls IAgentTool implementations.
/// </summary>
internal sealed class OptimizationAgent(IToolRegistry tools) : IAgentRole
{
    public string Name => "optimization";
    public string Description => "Applies optimization algorithms (MCMF, EDF, token-bucket) to workflow scheduling, triage, and notification steps.";
    public IReadOnlyList<string> Intents { get; } = ["optimize_slots", "rank_triage", "throttle_notify"];

    public Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
        => task.Intent switch
        {
            "optimize_slots" => OptimizeSlotsAsync(task, ct),
            "rank_triage" => RankTriageAsync(task, ct),
            "throttle_notify" => ThrottleNotifyAsync(task, ct),
            _ => Task.FromResult(new AgentRoleResult(Name, false, $"Unknown optimization intent: {task.Intent}")),
        };

    // ── optimize_slots ────────────────────────────────────────────────────────
    // Takes slots_json (from HisSlotsAgent) + patient context → runs MCMF →
    // returns first assignment JSON: { patient_id, slot_id, doctor_id, specialty, time_iso, cost }

    private async Task<AgentRoleResult> OptimizeSlotsAsync(AgentTask task, CancellationToken ct)
    {
        var tool = tools.Find("optimize_batch_appointments");
        if (tool is null) return new AgentRoleResult(Name, false, "optimize_batch_appointments tool unavailable");

        var toolCtx = new ToolInvocationContext(task.UserId, task.ConversationId ?? Guid.Empty, task.CorrelationId ?? string.Empty);
        var patientId = task.Context.GetValueOrDefault("patient_id", task.UserId.ToString());
        var specialty = task.Context.GetValueOrDefault("specialty", string.Empty);
        var urgency = task.Context.GetValueOrDefault("urgency", "normal");
        var preferredTime = task.Context.GetValueOrDefault("preferred_time", string.Empty);
        var slotsJson = task.Context.GetValueOrDefault("slots_json", "{}");

        // Transform HIS slot format → MCMF slot format
        // HIS: { slots: [{ slot_id, doctor_id, doctor_name, time, room, available }] }
        // MCMF expects: [{ slot_id, doctor_id, specialty, time_iso }]
        var mcmfSlots = new List<object>();
        try
        {
            using var doc = JsonDocument.Parse(slotsJson);
            var slotArr = doc.RootElement.TryGetProperty("slots", out var sa) ? sa : doc.RootElement;
            foreach (var s in slotArr.EnumerateArray())
            {
                mcmfSlots.Add(new
                {
                    slot_id = s.TryGetProperty("slot_id", out var sid) ? sid.GetString() : $"SLOT-{Guid.NewGuid().ToString("N")[..8]}",
                    doctor_id = s.TryGetProperty("doctor_id", out var did) ? did.GetString() : "DR-GEN-001",
                    specialty = s.TryGetProperty("specialty", out var sp) ? sp.GetString() : specialty,
                    time_iso = s.TryGetProperty("time", out var t) ? t.GetString()
                               : DateTimeOffset.UtcNow.AddHours(2).ToString("O"),
                });
            }
        }
        catch { /* malformed JSON — mcmfSlots stays empty */ }

        if (mcmfSlots.Count == 0)
            return new AgentRoleResult(Name, false, "No available slots to optimize");

        var req = new
        {
            requests = new[]
            {
                new { patient_id = patientId, specialty, urgency, preferred_time_iso = preferredTime },
            },
            slots = mcmfSlots,
        };

        var output = await tool.InvokeAsync(JsonSerializer.Serialize(req), toolCtx, ct);

        // Extract the first assignment so the workflow gets a clean top-level object
        try
        {
            using var resultDoc = JsonDocument.Parse(output);
            if (resultDoc.RootElement.TryGetProperty("assignments", out var assignments)
                && assignments.GetArrayLength() > 0)
            {
                return new AgentRoleResult(Name, true, assignments[0].GetRawText(),
                    new Dictionary<string, string> { ["full_result"] = output });
            }
        }
        catch { /* return raw output on parse failure */ }

        return new AgentRoleResult(Name, true, output);
    }

    // ── rank_triage ───────────────────────────────────────────────────────────
    // Maps integer severity level (1-5) to string severity, calls rank_triage_patients.
    // Returns the first ranked patient JSON with priority_score + breakdown.

    private async Task<AgentRoleResult> RankTriageAsync(AgentTask task, CancellationToken ct)
    {
        var tool = tools.Find("rank_triage_patients");
        if (tool is null) return new AgentRoleResult(Name, false, "rank_triage_patients tool unavailable");

        var toolCtx = new ToolInvocationContext(task.UserId, task.ConversationId ?? Guid.Empty, task.CorrelationId ?? string.Empty);
        var patientId = task.Context.GetValueOrDefault("patient_id", task.UserId.ToString());

        var severityRaw = task.Context.GetValueOrDefault("severity_level", "3");
        var severityLevel = int.TryParse(severityRaw, out var lvl) ? lvl : 3;
        var severity = severityLevel switch { >= 5 => "critical", 4 => "severe", 3 => "moderate", _ => "mild" };

        var riskFlags = task.Context.GetValueOrDefault("risk_flags", string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var resourceLoad = severityLevel switch { >= 5 => 0.9, 4 => 0.7, 3 => 0.4, _ => 0.2 };

        var req = new
        {
            patients = new[]
            {
                new
                {
                    patient_id = patientId,
                    severity,
                    wait_minutes = 0,
                    risk_flags = riskFlags,
                    resource_load = resourceLoad,
                },
            },
        };

        var output = await tool.InvokeAsync(JsonSerializer.Serialize(req), toolCtx, ct);

        // Surface the first ranked patient for easy downstream reading
        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("ranked_patients", out var ranked)
                && ranked.GetArrayLength() > 0)
            {
                return new AgentRoleResult(Name, true, ranked[0].GetRawText(),
                    new Dictionary<string, string> { ["full_result"] = output });
            }
        }
        catch { /* return raw output */ }

        return new AgentRoleResult(Name, true, output);
    }

    // ── throttle_notify ───────────────────────────────────────────────────────
    // Reads notifications_json from context (JSON array), wraps it in the tool input,
    // returns the decisions array JSON.

    private async Task<AgentRoleResult> ThrottleNotifyAsync(AgentTask task, CancellationToken ct)
    {
        var tool = tools.Find("throttle_notifications");
        if (tool is null) return new AgentRoleResult(Name, false, "throttle_notifications tool unavailable");

        var toolCtx = new ToolInvocationContext(task.UserId, task.ConversationId ?? Guid.Empty, task.CorrelationId ?? string.Empty);
        var notificationsJson = task.Context.GetValueOrDefault("notifications_json", "[]");

        string wrappedArgs;
        try
        {
            using var arr = JsonDocument.Parse(notificationsJson);
            wrappedArgs = JsonSerializer.Serialize(new { notifications = arr.RootElement });
        }
        catch
        {
            return new AgentRoleResult(Name, false, "notifications_json is not valid JSON");
        }

        var output = await tool.InvokeAsync(wrappedArgs, toolCtx, ct);
        return new AgentRoleResult(Name, true, output);
    }
}
