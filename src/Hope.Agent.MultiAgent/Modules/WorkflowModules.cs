using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.Application.Tools;
using Hope.Agent.MultiAgent.Roles;
using Microsoft.Extensions.DependencyInjection;

namespace Hope.Agent.MultiAgent.Modules;

// ────────────────────────────────────────────────────────────────────────────────
// Mỗi class dưới đây là một "module role" cho một workflow cụ thể.
// Quy tắc:
//   - WorkflowName phải khớp với WorkflowName trong Tools/Modules/WorkflowModules.cs
//   - Chỉ đăng ký IAgentRole ở đây; IAgentTool đăng ký ở phía Tools
//   - Để thêm workflow mới: tạo thêm một class sealed mới bên dưới, không cần chỉnh DI
// ────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Role hội thoại tổng quát — có thể khởi tạo workflow mới qua IWorkflowDispatcher.
/// Không gắn với bước nội tại của một workflow cụ thể.
/// </summary>
internal sealed class GeneralRoleModule : IWorkflowModule
{
    public string WorkflowName => "general";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddScoped<IAgentRole, SchedulingAgent>();
        services.AddScoped<IAgentRole, ClinicalAgent>();
        services.AddScoped<IAgentRole, BillingAgent>();
        services.AddScoped<IAgentRole, ComplianceAgent>();
        services.AddScoped<IAgentRole, EmergencyAgent>();
        services.AddScoped<IAgentRole, NotificationAgent>();
    }
}

/// <summary>
/// Role phục vụ bước nội tại <c>AppointmentSchedulingWorkflow</c>.
/// Mapping: specialty_routing → SpecialtyRoutingAgent (map_specialty)
///          his_slots        → HisSlotsAgent          (get_doctor_slots)
///          his_booking      → HisBookingAgent         (commit_booking)
/// Tool tương ứng: AppointmentSchedulingToolModule trong Hope.Agent.Tools.
/// </summary>
internal sealed class AppointmentSchedulingRoleModule : IWorkflowModule
{
    public string WorkflowName => "appointment-scheduling";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddScoped<IAgentRole, SpecialtyRoutingAgent>();
        services.AddScoped<IAgentRole, HisSlotsAgent>();
        services.AddScoped<IAgentRole, HisBookingAgent>();
    }
}

/// <summary>
/// Role phục vụ bước nội tại <c>MedicationReminderWorkflow</c>.
/// Mapping: medication_lookup → MedicationLookupAgent (get_medication_schedule)
/// Tool tương ứng: MedicationReminderToolModule trong Hope.Agent.Tools.
/// </summary>
internal sealed class MedicationReminderRoleModule : IWorkflowModule
{
    public string WorkflowName => "medication-reminder";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddScoped<IAgentRole, MedicationLookupAgent>();
        services.AddScoped<IAgentRole, ReminderPersistenceAgent>();
    }
}

/// <summary>
/// Role phục vụ bước nội tại <c>AuditReportWorkflow</c>.
/// Mapping: audit_collect  → AuditExecutionAgent (collect_audit_logs)
///          audit_analyze  → AuditExecutionAgent (detect_audit_anomalies)
///          audit_narrate  → AuditExecutionAgent (ILLMRouter — sinh văn bản tiếng Việt)
///          audit_export   → AuditExecutionAgent (export_audit_report)
/// Tool tương ứng: AuditReportToolModule trong Hope.Agent.Tools.
/// </summary>
internal sealed class AuditReportRoleModule : IWorkflowModule
{
    public string WorkflowName => "audit-report";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddScoped<IAgentRole, AuditExecutionAgent>();
    }
}

/// <summary>
/// Role phục vụ các bước tối ưu hóa trong bất kỳ workflow nào.
/// Mapping: optimize_slots  → OptimizationAgent (optimize_batch_appointments — Min-Cost Max-Flow)
///          rank_triage     → OptimizationAgent (rank_triage_patients — Weighted EDF)
///          throttle_notify → OptimizationAgent (throttle_notifications — Token Bucket)
/// Tool tương ứng: OptimizationToolModule trong Hope.Agent.Tools.
/// </summary>
internal sealed class OptimizationRoleModule : IWorkflowModule
{
    public string WorkflowName => "optimization";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddScoped<IAgentRole, OptimizationAgent>();
    }
}
