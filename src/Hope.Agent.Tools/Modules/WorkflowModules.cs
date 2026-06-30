using Hope.Agent.Application.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Hope.Agent.Tools.Modules;

// ────────────────────────────────────────────────────────────────────────────────
// Mỗi class dưới đây là một "module tool" cho một workflow cụ thể.
// Quy tắc:
//   - WorkflowName phải khớp với WorkflowName trong MultiAgent/Modules/WorkflowModules.cs
//   - Chỉ đăng ký IAgentTool ở đây; IAgentRole đăng ký ở phía MultiAgent
//   - Để thêm workflow mới: tạo thêm một class sealed mới bên dưới, không cần chỉnh DI
// ────────────────────────────────────────────────────────────────────────────────

/// <summary>Tool dùng chung, không gắn với một workflow cụ thể.</summary>
internal sealed class CoreToolModule : IWorkflowModule
{
    public string WorkflowName => "core";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IAgentTool, PatientLookupTool>();
        services.AddSingleton<IAgentTool, AppointmentScheduleTool>();
        services.AddSingleton<IAgentTool, InsuranceVerifyTool>();
        services.AddSingleton<IAgentTool, ClinicalGuidelineSearchTool>();
    }
}

/// <summary>
/// Tool phục vụ <c>AppointmentSchedulingWorkflow</c>.
/// Các bước: map_specialty → get_doctor_slots → commit_booking.
/// Role tương ứng: SpecialtyRoutingAgent, HisSlotsAgent, HisBookingAgent.
/// </summary>
internal sealed class AppointmentSchedulingToolModule : IWorkflowModule
{
    public string WorkflowName => "appointment-scheduling";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IAgentTool, MapSpecialtyTool>();
        services.AddSingleton<IAgentTool, GetDoctorSlotsTool>();
        services.AddSingleton<IAgentTool, CommitBookingTool>();
    }
}

/// <summary>
/// Tool phục vụ <c>MedicationReminderWorkflow</c>.
/// Bước: get_medication_schedule (đọc lịch trình hiện tại khi khởi tạo nhắc nhở).
/// Role tương ứng: MedicationLookupAgent.
/// </summary>
internal sealed class MedicationReminderToolModule : IWorkflowModule
{
    public string WorkflowName => "medication-reminder";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IAgentTool, GetMedicationScheduleTool>();
        services.AddSingleton<IAgentTool, CreateReminderRecordTool>();
        services.AddSingleton<IAgentTool, UpdateReminderStatusTool>();
    }
}

/// <summary>
/// Tool phục vụ <c>MedicalSummaryAgent</c>.
/// Bước: persist_medical_summary (ghi tóm tắt/SOAP note vào Postgres).
/// </summary>
internal sealed class MedicalSummaryToolModule : IWorkflowModule
{
    public string WorkflowName => "medical-summary";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IAgentTool, PersistMedicalSummaryTool>();
    }
}

/// <summary>
/// Tool phục vụ <c>AuditReportWorkflow</c>.
/// Các bước: collect_audit_logs → detect_audit_anomalies → export_audit_report.
/// Role tương ứng: AuditExecutionAgent (xử lý cả 4 intent trong một role).
/// </summary>
internal sealed class AuditReportToolModule : IWorkflowModule
{
    public string WorkflowName => "audit-report";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IAgentTool, CollectAuditLogsTool>();
        services.AddSingleton<IAgentTool, DetectAuditAnomaliesTool>();
        services.AddSingleton<IAgentTool, ExportAuditReportTool>();
    }
}

/// <summary>
/// Optimization tools — không gắn với một workflow đơn lẻ,
/// nhưng có thể được dùng bởi bất kỳ workflow nào cần tối ưu hóa.
///
/// Algorithms:
///   - OptimizeBatchAppointmentsTool : Min-Cost Max-Flow (slot allocation)
///   - RankTriagePatientsTool         : Weighted multi-criteria EDF scoring
///   - ThrottleNotificationsTool      : Token-bucket rate limiting
/// </summary>
internal sealed class OptimizationToolModule : IWorkflowModule
{
    public string WorkflowName => "optimization";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IAgentTool, OptimizeBatchAppointmentsTool>();
        services.AddSingleton<IAgentTool, RankTriagePatientsTool>();
        services.AddSingleton<IAgentTool, ThrottleNotificationsTool>();
    }
}
