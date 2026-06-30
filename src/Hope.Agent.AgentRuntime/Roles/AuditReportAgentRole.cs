using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.Application.Security;
using Hope.Agent.Application.Workflows;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.AgentRuntime.Roles;

/// <summary>
/// Audit Report Agent — triggers a Temporal audit report workflow that aggregates structured logs,
/// runs AI-assisted narrative writing, detects anomalies, and exports a tamper-evident signed report.
/// Reference: Epic audit trail, Amazon Macie, Microsoft Purview, Wazuh SIEM.
/// </summary>
internal sealed class AuditReportAgentRole(
    IWorkflowDispatcher workflows,
    IPhiRedactor phi,
    ILogger<AuditReportAgentRole> log) : IAgentRole
{
    public string Name => "audit-report";
    public string Description => "Generates compliance and operational audit reports with AI narration and tamper-evident hash chaining.";
    public IReadOnlyList<string> Intents =>
    [
        "audit_report", "bao_cao_audit", "compliance_report",
        "bao_cao_van_hanh", "security_report", "kiem_toan",
    ];

    public async Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
    {
        log.LogInformation("[AuditReport] UserId={UserId} Input={Input}", task.UserId, phi.Redact(task.Input));

        task.Context.TryGetValue("report_type", out var reportType);
        task.Context.TryGetValue("period_start", out var rawStart);
        task.Context.TryGetValue("period_end", out var rawEnd);
        task.Context.TryGetValue("export_format", out var exportFormat);

        _ = DateTimeOffset.TryParse(rawStart, out var periodStart);
        _ = DateTimeOffset.TryParse(rawEnd, out var periodEnd);

        if (periodStart == default)
            periodStart = DateTimeOffset.UtcNow.AddDays(-30);
        if (periodEnd == default)
            periodEnd = DateTimeOffset.UtcNow;

        var resolvedType = ResolveReportType(reportType, task.Input);

        var input = new AuditReportInput(
            RequestedBy: task.UserId,
            ReportType: resolvedType,
            PeriodStart: periodStart,
            PeriodEnd: periodEnd,
            ExportFormat: exportFormat ?? "json");

        var workflowId = $"audit-{resolvedType}-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.CreateVersion7():N}";
        var started = await workflows.StartAuditReportAsync(input, workflowId, ct)
            .ConfigureAwait(false);

        log.LogInformation("[AuditReport] Workflow started: {WorkflowId} Type={Type}", started.WorkflowId, resolvedType);

        return new AgentRoleResult(
            Role: Name,
            Success: true,
            Output:
                $"📊 Đang tạo báo cáo **{resolvedType}**.\n" +
                $"• Kỳ báo cáo: {periodStart:dd/MM/yyyy} – {periodEnd:dd/MM/yyyy}\n" +
                $"• Định dạng: {(exportFormat ?? "json").ToUpperInvariant()}\n" +
                $"• Workflow: {started.WorkflowId}\n\n" +
                "Báo cáo sẽ bao gồm: tổng số cuộc hội thoại, tỷ lệ thành công theo workflow, " +
                "sự kiện bảo mật (SSRF/injection attempts), phân tích ICD-10 coding, " +
                "và phần tường thuật AI. Ký số tamper-evident khi xuất.",
            Metadata: new Dictionary<string, string>
            {
                ["workflow_id"] = started.WorkflowId,
                ["report_type"] = resolvedType,
                ["period_start"] = periodStart.ToString("O"),
                ["period_end"] = periodEnd.ToString("O"),
            });
    }

    private static string ResolveReportType(string? provided, string input)
    {
        if (!string.IsNullOrEmpty(provided))
            return provided;

        var lower = input.ToLowerInvariant();
        if (lower.Contains("bảo mật") || lower.Contains("security"))
            return "security";
        if (lower.Contains("lâm sàng") || lower.Contains("clinical"))
            return "clinical";
        if (lower.Contains("tuân thủ") || lower.Contains("compliance"))
            return "compliance";
        return "operational";
    }
}
