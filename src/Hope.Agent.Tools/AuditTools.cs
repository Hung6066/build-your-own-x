using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Tools;
using Hope.Agent.Application.Workflows;

namespace Hope.Agent.Tools;

/// <summary>
/// Audit tools for the AuditReportWorkflow intra-workflow steps.
/// Production systems would pull from structured log stores (OpenSearch, Wazuh, Azure Monitor).
/// </summary>

// ── Log Collection ────────────────────────────────────────────────────────────

public sealed class CollectAuditLogsTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "collect_audit_logs",
        "Aggregates structured audit events from the agent platform (workflow events, auth logs, tool invocations) for a given period and report type.",
        """
        {
          "type": "object",
          "properties": {
            "report_type": {"type": "string", "description": "security | compliance | operational | coding"},
            "period_start": {"type": "string", "format": "date-time"},
            "period_end": {"type": "string", "format": "date-time"},
            "report_id": {"type": "string"}
          },
          "required": ["report_type", "period_start", "period_end"]
        }
        """);

    public Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;
        var reportType = args.GetProperty("report_type").GetString() ?? "operational";
        var start = args.GetProperty("period_start").GetString();
        var end = args.GetProperty("period_end").GetString();

        // Stub: deterministic metrics that production would pull from OpenSearch/Wazuh
        var metrics = reportType switch
        {
            "security" => new Dictionary<string, object>
            {
                ["total_auth_attempts"] = 1240,
                ["failed_auth"] = 23,
                ["ssrf_blocked"] = 4,
                ["prompt_injection_blocked"] = 7,
                ["pii_redacted_events"] = 18,
                ["anomalous_ips"] = new[] { "10.0.0.45", "192.168.1.99" },
                ["off_hours_access_count"] = 12,
            },
            "compliance" => new Dictionary<string, object>
            {
                ["total_patient_records_accessed"] = 892,
                ["phi_export_events"] = 34,
                ["consent_violations"] = 0,
                ["role_escalations"] = 2,
                ["failed_compliance_checks"] = 5,
                ["icd10_coding_sessions"] = 156,
                ["uncoded_discharge_summaries"] = 8,
            },
            _ => new Dictionary<string, object>
            {
                ["total_workflows_started"] = 312,
                ["completed_workflows"] = 298,
                ["failed_workflows"] = 14,
                ["avg_scheduling_time_min"] = 3.4,
                ["appointment_no_shows"] = 27,
                ["medication_reminders_sent"] = 1803,
                ["adherence_rate_pct"] = 78.2,
                ["agent_tasks_processed"] = 4210,
                ["llm_tokens_used"] = 2_840_000,
            },
        };

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            report_id = args.TryGetProperty("report_id", out var r) ? r.GetString() : null,
            report_type = reportType,
            period_start = start,
            period_end = end,
            collected_at = DateTimeOffset.UtcNow.ToString("O"),
            metrics,
            event_count = 4210 + new Random(reportType.GetHashCode()).Next(0, 500),
        }));
    }
}

// ── Anomaly Detection ─────────────────────────────────────────────────────────

public sealed class DetectAuditAnomaliesTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "detect_audit_anomalies",
        "Analyzes collected audit metrics for security and compliance anomalies: unusual access patterns, bulk exports, off-hours activity, repeated failures.",
        """
        {
          "type": "object",
          "properties": {
            "metrics_json": {"type": "string", "description": "JSON output from collect_audit_logs"},
            "sensitivity": {"type": "string", "enum": ["low", "medium", "high"], "default": "medium"}
          },
          "required": ["metrics_json"]
        }
        """);

    public Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;
        var sensitivity = args.TryGetProperty("sensitivity", out var s) ? s.GetString() : "medium";

        var anomalies = new List<object>();

        try
        {
            var metrics = JsonDocument.Parse(
                args.GetProperty("metrics_json").GetString() ?? "{}").RootElement;

            if (metrics.TryGetProperty("metrics", out var m))
            {
                // Off-hours access check
                if (m.TryGetProperty("off_hours_access_count", out var offHours)
                    && offHours.GetInt32() > (sensitivity == "high" ? 5 : 10))
                {
                    anomalies.Add(new
                    {
                        type = "off_hours_access",
                        severity = "medium",
                        detail = $"Phát hiện {offHours.GetInt32()} lần truy cập ngoài giờ làm việc",
                        recommendation = "Rà soát danh sách tài khoản truy cập ngoài giờ, xác nhận với quản lý",
                    });
                }

                // Auth failures
                if (m.TryGetProperty("failed_auth", out var failedAuth)
                    && failedAuth.GetInt32() > 15)
                {
                    anomalies.Add(new
                    {
                        type = "brute_force_risk",
                        severity = "high",
                        detail = $"{failedAuth.GetInt32()} lần xác thực thất bại — nguy cơ tấn công brute-force",
                        recommendation = "Kích hoạt 2FA bắt buộc, kiểm tra fail2ban/IP block logs",
                    });
                }

                // Prompt injection
                if (m.TryGetProperty("prompt_injection_blocked", out var injections)
                    && injections.GetInt32() > 0)
                {
                    anomalies.Add(new
                    {
                        type = "prompt_injection_attempt",
                        severity = injections.GetInt32() > 3 ? "high" : "medium",
                        detail = $"{injections.GetInt32()} lần tấn công prompt injection bị chặn",
                        recommendation = "Rà soát input từ nguồn nào, cập nhật PromptShield patterns",
                    });
                }

                // PHI export
                if (m.TryGetProperty("phi_export_events", out var phiExport)
                    && phiExport.GetInt32() > 20)
                {
                    anomalies.Add(new
                    {
                        type = "bulk_phi_export",
                        severity = "high",
                        detail = $"{phiExport.GetInt32()} sự kiện xuất dữ liệu PHI — vượt ngưỡng bình thường",
                        recommendation = "Xác nhận ủy quyền xuất dữ liệu, kiểm tra Data Loss Prevention logs",
                    });
                }
            }
        }
        catch { /* malformed input — return empty anomalies */ }

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            anomaly_count = anomalies.Count,
            risk_level = anomalies.Any(a => ((dynamic)a).severity == "high") ? "high"
                : anomalies.Count > 0 ? "medium" : "low",
            anomalies,
            analyzed_at = DateTimeOffset.UtcNow.ToString("O"),
        }));
    }
}

// ── Report Export & Signing ───────────────────────────────────────────────────

public sealed class ExportAuditReportTool(IAuditReportStore reportStore) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "export_audit_report",
        "Serializes the audit report to the specified format (json/pdf/csv), computes a SHA-256 integrity hash, and persists to the report store.",
        """
        {
          "type": "object",
          "properties": {
            "report_id": {"type": "string"},
            "narrative": {"type": "string"},
            "anomalies_json": {"type": "string"},
            "metrics_json": {"type": "string"},
            "format": {"type": "string", "enum": ["json", "pdf", "csv"], "default": "json"}
          },
          "required": ["report_id", "narrative"]
        }
        """);

    public async Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;
        var reportId = args.GetProperty("report_id").GetString() ?? "UNKNOWN";
        var narrative = args.GetProperty("narrative").GetString() ?? string.Empty;
        var format = args.TryGetProperty("format", out var f) ? f.GetString() ?? "json" : "json";
        var reportType = args.TryGetProperty("report_type", out var rt) ? rt.GetString() ?? "operational" : "operational";
        var periodStart = args.TryGetProperty("period_start", out var ps)
            && DateTimeOffset.TryParse(ps.GetString(), out var parsedStart)
                ? parsedStart
                : (DateTimeOffset?)null;
        var periodEnd = args.TryGetProperty("period_end", out var pe)
            && DateTimeOffset.TryParse(pe.GetString(), out var parsedEnd)
                ? parsedEnd
                : (DateTimeOffset?)null;
        var anomaliesJson = args.TryGetProperty("anomalies_json", out var a) ? a.GetString() : null;
        var metricsJson = args.TryGetProperty("metrics_json", out var m) ? m.GetString() : null;
        var exportedAt = DateTimeOffset.UtcNow;
        var exportPath = $"/reports/{reportId}.{format}";

        var content = JsonSerializer.Serialize(new
        {
            report_id = reportId,
            narrative,
            anomalies = anomaliesJson,
            metrics = metricsJson,
            exported_at = exportedAt.ToString("O"),
            exported_by = context.UserId.ToString(),
        });

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        var byteSize = Encoding.UTF8.GetByteCount(content);

        await reportStore.SaveAsync(new AuditReportWrite(
            ReportId: reportId,
            RequestedBy: context.UserId,
            ReportType: reportType,
            PeriodStart: periodStart,
            PeriodEnd: periodEnd,
            Narrative: narrative,
            MetricsJson: NormalizeJsonOrNull(metricsJson),
            AnomaliesJson: NormalizeJsonOrNull(anomaliesJson),
            Format: format,
            ExportPath: exportPath,
            IntegrityHash: hash,
            ByteSize: byteSize,
            SigningAlgorithm: "SHA-256",
            ExportedAt: exportedAt,
            Status: "exported",
            CorrelationId: context.CorrelationId), ct).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            report_id = reportId,
            format,
            export_path = exportPath,
            integrity_hash = hash,
            byte_size = byteSize,
            exported_at = exportedAt.ToString("O"),
            signing_algorithm = "SHA-256",
        });
    }

    private static string? NormalizeJsonOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetRawText();
        }
        catch
        {
            return JsonSerializer.Serialize(new { raw = json });
        }
    }
}
