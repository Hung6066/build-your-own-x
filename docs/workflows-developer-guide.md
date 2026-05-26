# Hope.Agent — Temporal Workflows: Tài Liệu Triển Khai, Luồng Xử Lý & Hướng Dẫn Lập Trình Viên

> **Đối tượng:** Kỹ sư backend mới onboard codebase Hope.Agent.  
> **Phạm vi:** 3 Temporal workflow mới, kiến trúc tool/agent hỗ trợ từng bước, REST API, và ràng buộc tránh circular dispatch.

---

## Mục Lục

1. [Tổng quan kiến trúc](#1-tổng-quan-kiến-trúc)
2. [Bản đồ layer](#2-bản-đồ-layer)
   - 2.1 [Pattern IWorkflowModule & auto-discovery](#21-pattern-iworkflowmodule--auto-discovery)
3. [Cơ chế thực thi một bước workflow](#3-cơ-chế-thực-thi-một-bước-workflow)
4. [Workflow: AppointmentSchedulingWorkflow](#4-workflow-appointmentschedulingworkflow)
5. [Workflow: MedicationReminderWorkflow](#5-workflow-medicationreminderworkflow)
6. [Workflow: AuditReportWorkflow](#6-workflow-auditreportworkflow)
7. [Danh mục Agent Role](#7-danh-mục-agent-role)
8. [Danh mục Tool](#8-danh-mục-tool)
9. [REST API Reference](#9-rest-api-reference)
10. [Hướng dẫn đăng ký DI](#10-hướng-dẫn-đăng-ký-di)
11. [Thêm workflow mới (end-to-end)](#11-thêm-workflow-mới-end-to-end)
12. [Quy tắc bắt buộc & ràng buộc thiết kế](#12-quy-tắc-bắt-buộc--ràng-buộc-thiết-kế)
13. [Vòng Lặp Suy Luận Agentic](#13-vòng-lặp-suy-luận-agentic)
    - 13.1–13.8 ReAct · Reflection · Adaptive Routing · Feedback Loop
    - [13.9 Tree of Thoughts](#139-tree-of-thoughts)
    - [13.10 MCMF Adaptive Costs](#1310-mcmf-adaptive-costs)
    - [13.11 Cross-Workflow Patient Memory](#1311-cross-workflow-patient-memory)
    - [13.12 Multi-hop Agent Handoffs](#1312-multi-hop-agent-handoffs)
14. [Thêm workflow mới (end-to-end)](#11-thêm-workflow-mới-end-to-end)
15. [Quy tắc bắt buộc & ràng buộc thiết kế](#12-quy-tắc-bắt-buộc--ràng-buộc-thiết-kế)

---

## 1. Tổng Quan Kiến Trúc

```
HTTP Request
    │
    ▼
WorkflowEndpoints (Minimal API)
    │  POST /v1/workflows/{type}
    ▼
IWorkflowDispatcher  ──────────────────────────────────── TemporalWorkflowDispatcher
    │                                                           │
    │                                                    Temporal.io Server
    │                                                           │
    │                                              [Workflow] class chạy
    │                                              (chỉ chứa logic tất định)
    │                                                           │
    │                                         Workflow.ExecuteActivityAsync(...)
    │                                                           │
    │                                       ClinicalActivities  (IO không tất định)
    │                                                           │
    │                                         orchestrator.DispatchAsync(AgentTask)
    │                                                           │
    │                                              ChiefMedicalAgent
    │                                                 │
    │                             ┌───────────────────┼─────────────────────┐
    │                             ▼                   ▼                     ▼
    │                    SpecialtyRoutingAgent   HisSlotsAgent        AuditExecutionAgent
    │                             │                   │                     │
    │                      map_specialty    get_doctor_slots    collect_audit_logs
    │                         (IAgentTool)      (IAgentTool)         (IAgentTool)
    │
    ▼
WorkflowStartResult  →  HTTP 202 Accepted
```

**Nguyên tắc thiết kế cốt lõi:** Workflow chỉ chứa logic điều phối tất định. Toàn bộ I/O (gọi LLM, HIS API, gửi thông báo) được đặt trong `ClinicalActivities` — một `[Activity]` được Temporal tự động retry, bảo vệ timeout và ghi nhật ký bền vững.

---

## 2. Bản Đồ Layer

| Layer                     | Project                  | Trách nhiệm                                                                   |
| ------------------------- | ------------------------ | ----------------------------------------------------------------------------- |
| **API**                   | `Hope.Agent.Api`         | Minimal API endpoint; ánh xạ HTTP → gọi `IWorkflowDispatcher`                 |
| **Application contracts** | `Hope.Agent.Application` | `IWorkflowDispatcher`, `IAgentRole`, `IAgentTool`, tất cả record input/output |
| **Workflow engine**       | `Hope.Agent.Workflows`   | Class `[Workflow]`, `ClinicalActivities`, `TemporalWorkflowDispatcher`        |
| **Agent roles**           | `Hope.Agent.MultiAgent`  | Orchestrator `ChiefMedicalAgent` + toàn bộ `IAgentRole`                       |
| **Tools**                 | `Hope.Agent.Tools`       | Các `IAgentTool` — HIS stub, audit tool                                       |
| **LLM**                   | `Hope.Agent.LLMGateway`  | `ILLMRouter` → `IChatCompletionProvider` → OpenAI / Azure OpenAI              |

### Hai nhóm đăng ký IAgentRole

Có **hai nhóm riêng biệt** `IAgentRole` được đăng ký trong DI:

| Nhóm                                       | Project                   | Mục đích                                             | Khởi tạo workflow?                    |
| ------------------------------------------ | ------------------------- | ---------------------------------------------------- | ------------------------------------- |
| `MultiAgent/Roles/Roles.cs`                | `Hope.Agent.MultiAgent`   | Điều phối tổng quát (clinical, billing, scheduling…) | Một số có (qua `IWorkflowDispatcher`) |
| `MultiAgent/Roles/WorkflowSupportRoles.cs` | `Hope.Agent.MultiAgent`   | Chỉ phục vụ bước nội tại workflow                    | **Không bao giờ** — chỉ gọi tool      |
| `AgentRuntime/Roles/*.cs`                  | `Hope.Agent.AgentRuntime` | Agent hội thoại phía người dùng                      | Có — khởi tạo workflow                |

`ChiefMedicalAgent` xây dựng `Dictionary<string, IAgentRole>` theo `Name`. Vì `AgentRuntime` roles được đăng ký **sau** `MultiAgent` roles trong `Program.cs`, nếu hai role trùng `Name` thì phiên bản `AgentRuntime` sẽ thắng. **WorkflowSupportRoles dùng name riêng biệt** (`specialty-routing`, `his-slots`…) để tránh va chạm.

---

### 2.1 Pattern IWorkflowModule & Auto-Discovery

Thay vì đăng ký tool/role riêng lẻ trong DI file, mỗi workflow sử dụng **hai module class** — một bên `Hope.Agent.Tools`, một bên `Hope.Agent.MultiAgent` — để nhóm toàn bộ dependencies theo đúng workflow sở hữu.

#### Cấu trúc file module

```
Hope.Agent.Tools/
└── Modules/
    └── WorkflowModules.cs
          ├── CoreToolModule                  ("core")
          ├── AppointmentSchedulingToolModule ("appointment-scheduling")
          ├── MedicationReminderToolModule    ("medication-reminder")
          ├── AuditReportToolModule           ("audit-report")
          └── OptimizationToolModule          ("optimization")

Hope.Agent.MultiAgent/
└── Modules/
    └── WorkflowModules.cs
          ├── GeneralRoleModule               ("general")
          ├── AppointmentSchedulingRoleModule ("appointment-scheduling")
          ├── MedicationReminderRoleModule    ("medication-reminder")
          ├── AuditReportRoleModule           ("audit-report")
          └── OptimizationRoleModule          ("optimization")
```

**Quy ước:** Cùng `WorkflowName` ở hai file = cùng một workflow. Đây là điểm tra cứu duy nhất khi muốn biết "workflow X dùng tool/role nào".

#### IWorkflowModule interface

Định nghĩa trong `src/Hope.Agent.Application/Tools/IAgentTool.cs`:

```csharp
public interface IWorkflowModule
{
    /// <summary>Tên định danh workflow, vd. "appointment-scheduling".</summary>
    string WorkflowName { get; }

    void RegisterServices(IServiceCollection services);
}
```

#### Ví dụ module class

```csharp
// Hope.Agent.Tools/Modules/WorkflowModules.cs
internal sealed class AppointmentSchedulingToolModule : IWorkflowModule
{
    public string WorkflowName => "appointment-scheduling";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddScoped<IAgentTool, MapSpecialtyTool>();
        services.AddScoped<IAgentTool, GetDoctorSlotsTool>();
        services.AddScoped<IAgentTool, CommitBookingTool>();
    }
}

// Hope.Agent.MultiAgent/Modules/WorkflowModules.cs
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
```

#### Cơ chế auto-discovery

`DependencyInjection.cs` của mỗi project **không liệt kê tĩnh** từng class nữa. Thay vào đó nó quét assembly tìm toàn bộ `IWorkflowModule` và tự gọi:

```csharp
// Trong Tools/DependencyInjection.cs và MultiAgent/DependencyInjection.cs
private static void ApplyWorkflowModules(IServiceCollection services)
{
    var moduleType = typeof(IWorkflowModule);
    foreach (var type in typeof(DependencyInjection).Assembly.GetTypes()
        .Where(t => moduleType.IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface))
    {
        var module = (IWorkflowModule)Activator.CreateInstance(type)!;
        module.RegisterServices(services);
    }
}
```

**Kết quả thực tế:** Thêm workflow mới **không cần chạm vào DI file** — chỉ cần tạo class mới trong `WorkflowModules.cs` của mỗi project.

#### Bảng ánh xạ đầy đủ (workflow ↔ tool ↔ role)

| `WorkflowName`           | Tool module                                                                                          | Role module                                                                                                  | Workflow class                  |
| ------------------------ | ---------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------ | ------------------------------- |
| `core`                   | `PatientLookupTool`, `AppointmentScheduleTool`, `InsuranceVerifyTool`, `ClinicalGuidelineSearchTool` | `SchedulingAgent`, `ClinicalAgent`, `BillingAgent`, `ComplianceAgent`, `EmergencyAgent`, `NotificationAgent` | _(dùng chung)_                  |
| `appointment-scheduling` | `MapSpecialtyTool`, `GetDoctorSlotsTool`, `CommitBookingTool`                                        | `SpecialtyRoutingAgent`, `HisSlotsAgent`, `HisBookingAgent`                                                  | `AppointmentSchedulingWorkflow` |
| `medication-reminder`    | `GetMedicationScheduleTool`                                                                          | `MedicationLookupAgent`                                                                                      | `MedicationReminderWorkflow`    |
| `audit-report`           | `CollectAuditLogsTool`, `DetectAuditAnomaliesTool`, `ExportAuditReportTool`                          | `AuditExecutionAgent`                                                                                        | `AuditReportWorkflow`           |
| `optimization`           | `OptimizeBatchAppointmentsTool`, `RankTriagePatientsTool`, `ThrottleNotificationsTool`               | `OptimizationAgent`                                                                                          | _(cross-workflow)_              |

---

## 3. Cơ Chế Thực Thi Một Bước Workflow

Mỗi bước workflow đều đi theo chuỗi sau:

```
Workflow (tất định)
  └─ Workflow.ExecuteActivityAsync(
         (ClinicalActivities a) => a.DispatchAgentAsync(input))
                │
                ▼  (trong [Activity] — Temporal tự retry khi thất bại)
         ClinicalActivities.DispatchAgentAsync
           tạo AgentTask { Intent, Input, Context, UserId, ... }
                │
                ▼
         IMultiAgentOrchestrator.DispatchAsync   (ChiefMedicalAgent)
           1. Tìm role có role.Intents.Contains(task.Intent)  — fast path
           2. Fallback: routing qua LLM
                │
                ▼
         IAgentRole.HandleAsync
           gọi IAgentTool.InvokeAsync  (hoặc ILLMRouter cho bước narrate/clinical)
                │
                ▼
         AgentRoleResult(Role, Success, Output, Metadata)
                │
                ▼
         AgentDispatchResult  trả về cho workflow
```

Workflow đọc `result.Output` (chuỗi JSON) và truyền các trường liên quan xuống bước tiếp theo qua dictionary `Context`.

---

## 4. Workflow: AppointmentSchedulingWorkflow

**File:** `src/Hope.Agent.Workflows/WorkflowsImpl/AppointmentSchedulingWorkflow.workflow.cs`

### Input / Output

```csharp
AppointmentSchedulingInput(
    Guid PatientId,
    Guid UserId,
    string ChiefComplaint,
    string Urgency = "normal",        // "normal" | "soon" | "urgent"
    string? PreferredDoctorId = null,
    string? PreferredTime = null,
    string? InsuranceCardNumber = null)

AppointmentSchedulingResult(
    string BookingId,
    string DoctorName,
    string Specialty,
    DateTimeOffset AppointmentTime,
    string InsuranceSummary,
    IReadOnlyList<string> StepLog)
```

### Luồng từng bước

```
Bước 1  routing-specialty
        Intent: "specialty_routing"  →  SpecialtyRoutingAgent
        Tool:   map_specialty(complaint, urgency)
        Output: tên chuyên khoa (string)  vd. "Tim mạch"

Bước 2  fetching-slots-and-insurance  [SONG SONG]
        ┌─ Intent: "his_slots"   →  HisSlotsAgent
        │  Tool:   get_doctor_slots(specialty, urgency, preferred_time)
        │  Output: full HIS slots JSON  { specialty, urgency, slots: [...] }
        │
        └─ Intent: "insurance"  →  BillingAgent
           Tool:   insurance_verify(patient_id, insurance_card, specialty)
           Output: JSON tóm tắt bảo hiểm

Bước 3  optimizing-slot  ← MỚI (Min-Cost Max-Flow)
        Intent: "optimize_slots"  →  OptimizationAgent
        Tool:   optimize_batch_appointments(requests=[1 patient], slots=[from HIS])
        Thuật toán: Successive Shortest Paths + SPFA
        Cost = specialty_mismatch(100) + wait_deviation(1/15min) - urgency_discount
        Output: assignment JSON  { patient_id, slot_id, doctor_id, specialty, time_iso, cost }

Bước 4  booking
        Intent: "his_booking"  →  HisBookingAgent
        Tool:   commit_booking(patient_id, doctor_id, slot_id, reason, booking_id)
        Context nhận slot_id và doctor_id trực tiếp từ kết quả MCMF ở Bước 3.
        Output: JSON xác nhận lịch hẹn  (bao gồm HL7 message ID)

Bước 5  notifying
        ClinicalActivities.NotifyAsync  (không dispatch agent)
        Kênh: input.PreferredChannel (mặc định "zalo")
        Gửi thông báo xác nhận kèm booking ID và thời gian khám.
```

### Query method của workflow

```csharp
[WorkflowQuery] string GetStatus()          // "routing-specialty" | "booking" | ...
[WorkflowQuery] IReadOnlyList<string> GetStepLog()
```

### Timeout activity

- `StartToCloseTimeout`: 2 phút mỗi bước
- `RetryPolicy`: tối đa 5 lần, initial 2s, ×2 backoff, tối đa 1 phút

---

## 5. Workflow: MedicationReminderWorkflow

**File:** `src/Hope.Agent.Workflows/WorkflowsImpl/MedicationReminderWorkflow.workflow.cs`

### Input

```csharp
MedicationReminderInput(
    Guid PatientId,
    Guid UserId,
    string MedicationName,
    string Dosage,
    string Frequency,               // "twice_daily" | "once_daily" ...
    DateTimeOffset StartAt,
    int DurationDays,
    string PreferredChannel = "zalo",
    int AdherenceRiskScore = 30)    // 0–100; ảnh hưởng số lần nhắc mỗi liều
```

### Luồng vòng lặp chạy dài

```
while (UtcNow < StartAt + DurationDays)
    │
    ├─ Workflow.DelayAsync(đến giờ liều tiếp theo)    ← Temporal durable timer
    │
    ├─ Throttle check (Token-Bucket)  ← MỚI
    │     Intent: "throttle_notify"  →  OptimizationAgent
    │     Tool:   throttle_notifications([attempt_1..N], channel, urgency)
    │     → Quyết định: send | delay | drop cho từng attempt
    │
    ├─ Lặp qua từng lần nhắc đã được phê duyệt (không drop):
    │     ClinicalActivities.NotifyAsync(channel, title, body)
    │     Attempt bị drop: bỏ qua và log
    │
    ├─ Workflow.WaitConditionAsync(
    │     () => latestConfirmation != null,
    │     timeout: khoảng cách giữa 2 liều)
    │
    ├─ Đã xác nhận?  confirmedCount++  thêm streakNote vào nhắc tiếp theo
    └─ Bỏ lỡ?        missedCount++
                     missedCount >= 3  →  thông báo nhóm chăm sóc (EscalateToCareTeam)
                     missedCount >= 3 VÀ DurationDays > 30  →  thông báo thêm giám sát viên
```

### Tần suất nhắc theo điểm tuân thủ điều trị

| `AdherenceRiskScore` | Số lần nhắc mỗi liều |
| -------------------- | -------------------- |
| > 60                 | 3 lần                |
| 31–60                | 2 lần                |
| 0–30                 | 1 lần                |

### Signal: xác nhận đã uống thuốc

```
POST /v1/workflows/reminders/{workflowId}/confirm
Body: { "confirmed": true, "note": "tuỳ chọn" }

→  dispatcher.SignalReminderConfirmationAsync
→  workflow.ConfirmDoseAsync(ReminderConfirmation)
   đặt latestConfirmation  →  WaitConditionAsync trả về true
```

### Query method của workflow

```csharp
[WorkflowQuery] string GetStatus()
[WorkflowQuery] int GetMissedCount()
[WorkflowQuery] int GetConfirmedCount()
```

---

## 6. Workflow: AuditReportWorkflow

**File:** `src/Hope.Agent.Workflows/WorkflowsImpl/AuditReportWorkflow.workflow.cs`

### Input / Output

```csharp
AuditReportInput(
    Guid RequestedBy,
    string ReportType,          // "security" | "compliance" | "operational"
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    string ExportFormat = "json")  // "json" | "pdf" | "csv"

AuditReportResult(
    string ReportId,
    string ReportType,
    string NarrativeSummary,
    string ExportPath,
    string IntegrityHash,       // SHA-256 của toàn bộ JSON báo cáo
    IReadOnlyList<string> StepLog)
```

### Luồng từng bước

```
ReportId được tạo:  "AUDIT-{TYPE}-{yyyyMMdd}-{8hex}"

Bước 1  collecting-logs
        Intent: "audit_collect"  →  AuditExecutionAgent.CollectLogsAsync
        Tool:   collect_audit_logs(report_type, period_start, period_end, report_id)
        Output: JSON object chỉ số (số sự kiện, failed auth, PHI export, v.v.)

Bước 2  detecting-anomalies
        Intent: "audit_analyze"  →  AuditExecutionAgent.AnalyzeAnomaliesAsync
        Tool:   detect_audit_anomalies(metrics_json, sensitivity="medium")
        Output: mảng JSON bất thường  { type, severity, detail, recommendation }

Bước 3  generating-narrative
        Intent: "audit_narrate"  →  AuditExecutionAgent.GenerateNarrativeAsync
        LLM:    ILLMRouter.SelectChat().CompleteAsync(...)
                system: chuyên gia phân tích bệnh viện, viết tiếng Việt
                user:   metrics JSON + anomalies JSON + kỳ báo cáo
        Output: văn bản tường thuật tiếng Việt kèm khuyến nghị

Bước 4  exporting-report
        Intent: "audit_export"  →  AuditExecutionAgent.ExportReportAsync
        Tool:   export_audit_report(report_id, narrative, anomalies_json, format)
        Output: { export_path, integrity_hash, byte_size, exported_at }

Bước 5  notifying
        ClinicalActivities.NotifyAsync  →  người yêu cầu nhận link tải + hash kiểm tra toàn vẹn
```

### Bảng điều phối của AuditExecutionAgent

`AuditExecutionAgent` xử lý cả 4 intent trong một class duy nhất qua switch expression:

```csharp
public IReadOnlyList<string> Intents => ["audit_collect", "audit_analyze", "audit_narrate", "audit_export"];

public Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
    => task.Intent switch {
        "audit_collect"  => CollectLogsAsync(task, ct),
        "audit_analyze"  => AnalyzeAnomaliesAsync(task, ct),
        "audit_narrate"  => GenerateNarrativeAsync(task, ct),
        "audit_export"   => ExportReportAsync(task, ct),
        _                => Task.FromResult(new AgentRoleResult(Name, false, "intent không hợp lệ")),
    };
```

### Truyền dữ liệu giữa các bước

Output của mỗi bước trở thành context của bước tiếp theo:

```
collectResult.Output  ──▶  anomalyCtx["raw_data"]
collectResult.Output  ──▶  narrativeCtx["metrics_data"]
anomalyResult.Output  ──▶  narrativeCtx["anomaly_data"]
narrativeResult.Output ──▶ exportCtx["narrative"]
anomalyResult.Output  ──▶  exportCtx["anomalies"]
```

### Timeout activity

- `StartToCloseTimeout`: 5 phút (bước sinh tường thuật LLM có thể tốn thời gian)
- `RetryPolicy`: tối đa 4 lần, initial 3s, ×2 backoff, tối đa 2 phút

---

## 7. Danh Mục Agent Role

### WorkflowSupportRoles — chỉ dùng nội tại workflow

> Nguồn: `src/Hope.Agent.MultiAgent/Roles/WorkflowSupportRoles.cs`

| Class                   | `Name`              | `Intents`                                                         | Tool được gọi                |
| ----------------------- | ------------------- | ----------------------------------------------------------------- | ---------------------------- |
| `SpecialtyRoutingAgent` | `specialty-routing` | `specialty_routing`, `map_specialty`, `route_specialty`           | `map_specialty`              |
| `HisSlotsAgent`         | `his-slots`         | `his_slots`, `get_slots`, `available_slots`                       | `get_doctor_slots`           |
| `HisBookingAgent`       | `his-booking`       | `his_booking`, `commit_booking`, `confirm_booking`                | `commit_booking`             |
| `MedicationLookupAgent` | `medication-lookup` | `medication_lookup`, `get_medication`, `medication_schedule`      | `get_medication_schedule`    |
| `AuditExecutionAgent`   | `audit-execution`   | `audit_collect`, `audit_analyze`, `audit_narrate`, `audit_export` | xem bảng trên + `ILLMRouter` |
| `OptimizationAgent`     | `optimization`      | `optimize_slots`, `rank_triage`, `throttle_notify`                | xem bảng Optimization Tools  |

**Quy tắc bắt buộc:** Không role nào trong nhóm này được gọi `IWorkflowDispatcher`. Chỉ được gọi `IAgentTool` hoặc `ILLMRouter`.

### Các role MultiAgent tổng quát — có thể khởi tạo workflow

| Class               | `Name`         | Intent chính                                       | Handoff được phát sinh                                             |
| ------------------- | -------------- | -------------------------------------------------- | ------------------------------------------------------------------ |
| `SchedulingAgent`   | `scheduling`   | `schedule`, `appointment`, `reschedule`, `booking` | → `optimization` khi không có slot khả dụng                        |
| `ClinicalAgent`     | `clinical`     | `diagnosis`, `triage`, `clinical_note`             | → `emergency` khi phát hiện từ khóa cấp cứu trong output           |
| `BillingAgent`      | `billing`      | `billing`, `insurance`, `coverage`, `claim`        | _(không phát sinh handoff)_                                        |
| `ComplianceAgent`   | `compliance`   | `audit`, `compliance`, `policy`                    | → `clinical` khi phát hiện PHI violation, yêu cầu phản hồi an toàn |
| `EmergencyAgent`    | `emergency`    | `emergency`, `critical`, `code_blue`               | → `notification` khi urgency level ≥ 4                             |
| `NotificationAgent` | `notification` | `notify`, `alert`, `reminder`                      | _(không phát sinh handoff)_                                        |

---

## 8. Danh Mục Tool

### HIS Tools — `src/Hope.Agent.Tools/HisTools.cs`

#### `map_specialty`

Chuyển đổi mô tả triệu chứng hoặc lý do khám thành tên chuyên khoa.

```json
// Schema đầu vào
{ "complaint": "string", "urgency": "normal|soon|urgent" }

// Ví dụ đầu ra
{ "complaint": "đau ngực", "specialty": "Tim mạch", "urgency": "urgent",
  "triage_note": "Ưu tiên cao — yêu cầu khám ngay trong 2 giờ" }
```

Dictionary `SymptomMap` nội tuyến hỗ trợ cả tiếng Việt lẫn tiếng Anh. Fallback về `"Nội tổng quát"`. Trường hợp urgent + đau ngực route tới `"Cấp cứu Tim mạch"`.

---

#### `get_doctor_slots`

Trả về các slot khám còn trống theo chuyên khoa từ HIS.

```json
// Schema đầu vào
{ "specialty": "string", "urgency": "string",
  "preferred_time": "ISO8601?", "preferred_doctor_id": "string?" }

// Ví dụ đầu ra
{ "specialty": "Tim mạch", "urgency": "normal",
  "slots": [
    { "slot_id": "SLOT-A1B2C3D4", "doctor_id": "DR-TIM-001",
      "doctor_name": "BS. Trần Thị Lan — Tim mạch",
      "time": "2026-05-26T08:00:00+00:00", "room": "P.201", "available": true },
    { "slot_id": "SLOT-E5F6G7H8",
      "doctor_name": "BS. Nguyễn Văn Minh — Tim mạch",
      "time": "2026-05-26T10:00:00+00:00", "room": "P.205", "available": true }
  ] }
```

Urgent: slot đầu tiên là 1 giờ kể từ hiện tại. Thường: slot đầu tiên là 08:00 ngày làm việc tiếp theo.

---

#### `commit_booking`

Ghi nhận lịch hẹn vào hệ thống HIS.

```json
// Schema đầu vào
{ "patient_id": "string", "doctor_id": "string", "slot_id": "string",
  "reason": "string?", "booking_id": "string?" }

// Ví dụ đầu ra
{ "booking_id": "BK-20260525-A1B2C3",
  "patient_id": "...", "doctor_id": "DR-TIM-001", "slot_id": "SLOT-...",
  "status": "confirmed", "confirmed_at": "...", "hl7_message_id": "MSG-..." }
```

`booking_id` đảm bảo tính idempotent — truyền ID đã tạo sẵn để tránh đặt trùng lịch khi Temporal retry bước này.

---

#### `get_medication_schedule`

Trả về danh sách đơn thuốc đang hoạt động của bệnh nhân từ module dược HIS.

```json
// Schema đầu vào
{ "patient_id": "string", "include_past": false }

// Ví dụ đầu ra
{ "patient_id": "...",
  "prescriptions": [
    { "prescription_id": "RX-...", "medication_name": "Metformin",
      "dosage": "500mg", "frequency": "twice_daily",
      "start_date": "2026-04-25", "end_date": "2026-07-24",
      "status": "active", "adherence_rate": 0.75 }
  ] }
```

---

### Audit Tools — `src/Hope.Agent.Tools/AuditTools.cs`

#### `collect_audit_logs`

Tổng hợp sự kiện audit theo loại báo cáo từ kho log cấu trúc.

```json
// Schema đầu vào
{
  "report_type": "security|compliance|operational|coding",
  "period_start": "ISO8601",
  "period_end": "ISO8601",
  "report_id": "string?"
}

// Đầu ra bao gồm (theo report_type):
//   security:    total_auth_attempts, failed_auth, ssrf_blocked,
//                prompt_injection_blocked, pii_redacted_events
//   compliance:  total_patient_records_accessed, phi_export_events, consent_violations
//   operational: total_workflows_started, failed_workflows, adherence_rate_pct,
//                agent_tasks_processed, llm_tokens_used
```

---

#### `detect_audit_anomalies`

Phát hiện bất thường dựa trên ngưỡng từ dữ liệu chỉ số đã thu thập.

```json
// Schema đầu vào
{ "metrics_json": "string (JSON từ collect_audit_logs)",
  "sensitivity": "low|medium|high" }

// Ví dụ đầu ra
{ "anomaly_count": 2, "risk_level": "high",
  "anomalies": [
    { "type": "brute_force_risk", "severity": "high",
      "detail": "23 lần xác thực thất bại — nguy cơ tấn công brute-force",
      "recommendation": "Kích hoạt 2FA bắt buộc, kiểm tra fail2ban/IP block logs" },
    { "type": "bulk_phi_export", "severity": "high",
      "detail": "34 sự kiện xuất dữ liệu PHI — vượt ngưỡng bình thường",
      "recommendation": "Xác nhận ủy quyền, kiểm tra Data Loss Prevention logs" }
  ] }
```

Các kiểm tra thực hiện: truy cập ngoài giờ làm việc, số lần auth thất bại, prompt injection bị chặn, xuất PHI khối lượng lớn.

---

#### `export_audit_report`

Serialize báo cáo và ký số bằng SHA-256.

```json
// Schema đầu vào
{ "report_id": "string", "narrative": "string",
  "anomalies_json": "string?", "metrics_json": "string?",
  "format": "json|pdf|csv" }

// Ví dụ đầu ra
{ "report_id": "AUDIT-SECURITY-20260525-A1B2C3D4",
  "format": "json",
  "export_path": "/reports/AUDIT-SECURITY-20260525-A1B2C3D4.json",
  "integrity_hash": "a3f8c2...",
  "byte_size": 14200,
  "exported_at": "...",
  "signing_algorithm": "SHA-256" }
```

Hash được tính trên toàn bộ nội dung: `report_id + narrative + anomalies + metrics + exported_at + exported_by`.

---

### Optimization Tools — `src/Hope.Agent.Tools/OptimizationTools.cs`

Module: `OptimizationToolModule` (`WorkflowName = "optimization"`)
Agent: `OptimizationAgent` (`Name = "optimization"`)

Tất cả optimization tool đều dùng chung qua **một role duy nhất** — `OptimizationAgent` — phân nhánh theo `task.Intent`.

---

#### `optimize_batch_appointments`

Phân bổ N bệnh nhân vào M slot khả dụng bằng **Min-Cost Max-Flow** (Successive Shortest Paths + SPFA).

```json
// Schema đầu vào
{
  "requests": [
    { "patient_id": "P001", "specialty": "Tim mạch",
      "urgency": "high", "preferred_time_iso": "2026-05-26T09:00:00Z" }
  ],
  "slots": [
    { "slot_id": "S1", "doctor_id": "DR001", "specialty": "Tim mạch",
      "time_iso": "2026-05-26T09:00:00Z" }
  ]
}

// Đầu ra
{
  "assignments": [
    { "patient_id": "P001", "slot_id": "S1", "doctor_id": "DR001",
      "specialty": "Tim mạch", "time_iso": "...", "cost": 5 }
  ],
  "total_min_cost": 5,
  "unassigned_patients": [],
  "algorithm": "min-cost-max-flow",
  "solver": "successive-shortest-paths-spfa"
}
```

**Hàm cost cạnh** `patient_i → slot_j`:

- `+100` nếu specialty khác nhau
- `+1` cho mỗi 15 phút chênh lệch so với `preferred_time_iso`
- `-20 / -10` cho urgency `critical / high`
- **`-0..15` (adaptive bonus)**: bác sĩ có lịch sử booking thành công cao được ưu tiên — `successRate ∈ [0.5, 1.0]` → bonus `∈ [0, 15]`. Xem §13.10.

**Tích hợp workflow:** `AppointmentSchedulingWorkflow` Bước 3 — thay thế logic greedy cũ. `OptimizationAgent` nhận `slots_json` từ context (output của `HisSlotsAgent`), transform sang MCMF format, trả về assignment tốt nhất.

---

#### `rank_triage_patients`

Xếp hạng danh sách bệnh nhân theo **weighted multi-criteria priority score** (EDF-inspired).

$$\text{score} = w_s \cdot \text{severity} + w_w \cdot \frac{100}{\text{wait}+1} + w_r \cdot \text{risk\_bonus} - w_l \cdot \text{resource\_load} \times 20$$

```json
// Đầu ra
{
  "ranked_patients": [
    {
      "rank": 1,
      "patient_id": "...",
      "severity": "critical",
      "priority_score": 192.5,
      "breakdown": {
        "severity_contribution": 100,
        "wait_contribution": 100,
        "risk_contribution": 30,
        "resource_penalty": 18
      },
      "active_risk_flags": ["chest_pain", "oxygen_below_90"]
    }
  ],
  "algorithm": "weighted-multi-criteria-edf"
}
```

**Tích hợp workflow:** `EmergencyTriageWorkflow` — sau bước assess triage (intent `"rank_triage"`), `PriorityScore` được ghi vào `EmergencyTriageResult` và log bước `priority-score:{score}`. Notification severity >= 4 cũng đính kèm score.

---

#### `throttle_notifications`

Áp dụng **token-bucket rate limiting** cho batch notification. Trả về quyết định `send | delay | drop` cho từng notification.

| Channel  | Default capacity | Default refill rate |
| -------- | ---------------- | ------------------- |
| `sms`    | 5                | 2/phút              |
| `email`  | 20               | 10/phút             |
| `push`   | 10               | 5/phút              |
| `in_app` | 50               | 20/phút             |

**Quy tắc quyết định:**

- `urgency = critical` → luôn `send` (bypass bucket)
- Còn token → `send`, trừ token
- Hết token + `urgency = high` → `delay`
- Hết token + urgency thấp → `drop`

**Tích hợp workflow:** `MedicationReminderWorkflow` — trước mỗi vòng nhắc dose, throttle check quyết định attempt nào được gửi. Attempt bị `drop` bị bỏ qua và log. Attempt `critical` (bệnh nhân nguy hiểm cao) không bao giờ bị drop.

---

## 9. REST API Reference

Tất cả endpoint yêu cầu JWT bearer auth. Base path: `/v1/workflows`.

### Khởi tạo lịch khám

```http
POST /v1/workflows/scheduling
Authorization: Bearer <token>
Content-Type: application/json

{
  "patientId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "chiefComplaint": "đau ngực dữ dội",
  "urgency": "urgent",
  "preferredDoctorId": null,
  "preferredTime": "2026-05-26T09:00:00Z",
  "insuranceCardNumber": "BH-1234567",
  "workflowId": null
}
```

```http
202 Accepted
Location: /v1/workflows/scheduling-3fa85f64...-...

{
  "workflowId": "scheduling-3fa85f64...-...",
  "runId": "01JE...",
  "startedAt": "2026-05-25T10:00:00Z"
}
```

---

### Khởi tạo nhắc uống thuốc

```http
POST /v1/workflows/reminders
Content-Type: application/json

{
  "patientId": "...",
  "medicationName": "Metformin",
  "dosage": "500mg",
  "frequency": "twice_daily",
  "startAt": "2026-05-25T08:00:00Z",
  "durationDays": 90,
  "preferredChannel": "zalo",
  "adherenceRiskScore": 65
}
```

```http
202 Accepted
```

---

### Xác nhận đã uống thuốc

```http
POST /v1/workflows/reminders/{workflowId}/confirm
Content-Type: application/json

{ "confirmed": true, "note": "Uống sau bữa sáng" }
```

```http
204 No Content
```

---

### Khởi tạo báo cáo audit

```http
POST /v1/workflows/audit
Content-Type: application/json

{
  "reportType": "security",
  "periodStart": "2026-04-01T00:00:00Z",
  "periodEnd": "2026-04-30T23:59:59Z",
  "exportFormat": "json"
}
```

```http
202 Accepted
```

---

### Kiểm tra trạng thái workflow

```http
GET /v1/workflows/{workflowId}
```

```http
200 OK

{
  "workflowId": "...",
  "runId": "...",
  "status": "Running",
  "startedAt": "...",
  "closedAt": null,
  "result": null,
  "failureReason": null
}
```

---

### Hủy workflow bất kỳ

```http
POST /v1/workflows/{workflowId}/cancel
Content-Type: application/json

{ "reason": "Bệnh nhân yêu cầu hủy" }
```

```http
204 No Content
```

---

## 10. Hướng Dẫn Đăng Ký DI

Với pattern `IWorkflowModule`, **DI file không còn là nơi liệt kê tool/role**. Mỗi file DI chỉ còn gọi `ApplyWorkflowModules` để tự động tìm và đăng ký toàn bộ module trong assembly.

### Đăng ký tool — `src/Hope.Agent.Tools/DependencyInjection.cs`

```csharp
public static IServiceCollection AddAgentTools(this IServiceCollection services, IConfiguration configuration)
{
    // Auto-discover tất cả IWorkflowModule trong assembly này.
    // Để thêm tool mới: tạo hoặc cập nhật class module trong Modules/WorkflowModules.cs
    ApplyWorkflowModules(services);

    services.AddSingleton<IToolRegistry, ToolRegistry>();

    services.Configure<McpOptions>(configuration.GetSection("Mcp"));
    services.AddHostedService<McpToolDiscoveryService>();
    return services;
}
```

> **Không thêm `AddScoped<IAgentTool, ...>` trực tiếp vào đây.** Mọi tool mới phải nằm trong một `IWorkflowModule` ở `Modules/WorkflowModules.cs`.

### Đăng ký role — `src/Hope.Agent.MultiAgent/DependencyInjection.cs`

```csharp
public static IServiceCollection AddMultiAgent(this IServiceCollection services)
{
    // Auto-discover tất cả IWorkflowModule trong assembly này.
    // Để thêm role mới: tạo hoặc cập nhật class module trong Modules/WorkflowModules.cs
    ApplyWorkflowModules(services);

    services.AddScoped<IMultiAgentOrchestrator, ChiefMedicalAgent>();
    return services;
}
```

> **Không thêm `AddScoped<IAgentRole, ...>` trực tiếp vào đây.** Orchestrator `ChiefMedicalAgent` là ngoại lệ duy nhất được đăng ký thẳng.

### Đăng ký workflow — `src/Hope.Agent.Workflows/DependencyInjection.cs`

Workflow class đăng ký **thủ công** (không dùng module) vì Temporal worker cần biết tường minh danh sách workflow type:

```csharp
services.AddHostedTemporalWorker(options.TaskQueue)
    .AddScopedActivities<ClinicalActivities>()
    .AddWorkflow<PatientAdmissionWorkflow>()
    .AddWorkflow<EmergencyTriageWorkflow>()
    .AddWorkflow<AppointmentSchedulingWorkflow>()
    .AddWorkflow<MedicationReminderWorkflow>()
    .AddWorkflow<AuditReportWorkflow>();
    // ← thêm workflow mới tại đây
```

### Tổng kết: khi nào chạm file nào?

| Hành động                   | File cần chỉnh                                                                              |
| --------------------------- | ------------------------------------------------------------------------------------------- |
| Thêm `IAgentTool` mới       | `Hope.Agent.Tools/Modules/WorkflowModules.cs`                                               |
| Thêm `IAgentRole` mới       | `Hope.Agent.MultiAgent/Modules/WorkflowModules.cs`                                          |
| Thêm `[Workflow]` class mới | `Hope.Agent.Workflows/DependencyInjection.cs`                                               |
| Thêm REST endpoint          | `Hope.Agent.Api/Endpoints/WorkflowEndpoints.cs`                                             |
| Thêm phương thức dispatch   | `Hope.Agent.Application/Workflows/IWorkflowDispatcher.cs` + `TemporalWorkflowDispatcher.cs` |

---

## 11. Thêm Workflow Mới (End-to-End)

**Tình huống minh họa:** Thêm bước "xét duyệt trước bảo hiểm" vào `AppointmentSchedulingWorkflow` — kiểm tra với API công ty bảo hiểm trước khi gửi thông báo xác nhận cho bệnh nhân.

### Bước 1 — Tạo tool

```csharp
// src/Hope.Agent.Tools/InsuranceAuthTool.cs
public sealed class PriorAuthorizationTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "prior_authorization",
        "Gửi yêu cầu xét duyệt trước tới API công ty bảo hiểm.",
        """{ "patient_id": "string", "procedure_code": "string", "booking_id": "string" }""");

    public Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext ctx, CancellationToken ct)
    {
        // ... gọi payer API thực hoặc stub
        return Task.FromResult(JsonSerializer.Serialize(new {
            auth_id = $"PA-{Guid.CreateVersion7().ToString("N")[..8]}",
            status = "approved",
            valid_until = DateTimeOffset.UtcNow.AddDays(30).ToString("O"),
        }));
    }
}
```

### Bước 2 — Tạo agent role

```csharp
// src/Hope.Agent.MultiAgent/Roles/WorkflowSupportRoles.cs  (append cuối file)
internal sealed class PriorAuthAgent(IToolRegistry tools) : IAgentRole
{
    public string Name => "prior-auth";
    public string Description => "Gửi xét duyệt trước bảo hiểm cho thủ thuật đã đặt lịch.";
    public IReadOnlyList<string> Intents { get; } = ["prior_auth", "insurance_auth"];

    public async Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
    {
        var tool = tools.Find("prior_authorization");
        if (tool is null) return new AgentRoleResult(Name, false, "prior_authorization tool không khả dụng");

        var ctx = new ToolInvocationContext(task.UserId, task.ConversationId ?? Guid.Empty, task.CorrelationId ?? string.Empty);
        var args = JsonSerializer.Serialize(new {
            patient_id = task.Context.GetValueOrDefault("patient_id"),
            procedure_code = task.Context.GetValueOrDefault("procedure_code"),
            booking_id = task.Context.GetValueOrDefault("booking_id"),
        });
        var output = await tool.InvokeAsync(args, ctx, ct);
        return new AgentRoleResult(Name, true, output);
    }
}
```

### Bước 3 — Chèn bước vào workflow

```csharp
// Thêm sau Bước 4 (booking) trong AppointmentSchedulingWorkflow:
status = "prior-authorization";
stepLog.Add(status);

var paCtx = new Dictionary<string, string>
{
    ["patient_id"] = input.PatientId.ToString(),
    ["procedure_code"] = "99213",
    ["booking_id"] = bookingId,
};
var paDispatch = new AgentDispatchInput(input.UserId, "prior_auth",
    $"Xin xét duyệt trước bảo hiểm cho lịch {bookingId}", paCtx, null, null, 5);
var paResult = await Workflow.ExecuteActivityAsync(
    (ClinicalActivities a) => a.DispatchAgentAsync(paDispatch), actOpts);
stepLog.Add($"prior-auth:{paResult.Role}");
```

### Bước 4 — Đăng ký vào module (không chạm DI file)

**Không thêm vào `DependencyInjection.cs`.** Thay vào đó, thêm vào class module tương ứng:

```csharp
// Hope.Agent.Tools/Modules/WorkflowModules.cs — cập nhật AppointmentSchedulingToolModule
internal sealed class AppointmentSchedulingToolModule : IWorkflowModule
{
    public string WorkflowName => "appointment-scheduling";
    public void RegisterServices(IServiceCollection services)
    {
        services.AddScoped<IAgentTool, MapSpecialtyTool>();
        services.AddScoped<IAgentTool, GetDoctorSlotsTool>();
        services.AddScoped<IAgentTool, CommitBookingTool>();
        services.AddScoped<IAgentTool, PriorAuthorizationTool>(); // ← thêm vào đây
    }
}

// Hope.Agent.MultiAgent/Modules/WorkflowModules.cs — cập nhật AppointmentSchedulingRoleModule
internal sealed class AppointmentSchedulingRoleModule : IWorkflowModule
{
    public string WorkflowName => "appointment-scheduling";
    public void RegisterServices(IServiceCollection services)
    {
        services.AddScoped<IAgentRole, SpecialtyRoutingAgent>();
        services.AddScoped<IAgentRole, HisSlotsAgent>();
        services.AddScoped<IAgentRole, HisBookingAgent>();
        services.AddScoped<IAgentRole, PriorAuthAgent>(); // ← thêm vào đây
    }
}
```

Auto-discovery sẽ tự tìm thấy class đã cập nhật — không cần restart hay chỉnh DI file.

### Bước 5 — Build và xác nhận

```powershell
dotnet build Hope.Agent.sln --configuration Release --no-incremental
# Kết quả mong đợi: Build succeeded. 0 Error(s) 0 Warning(s)
```

---

## 12. Quy Tắc Bắt Buộc & Ràng Buộc Thiết Kế

### Không được circular dispatch

> **Quy tắc vàng:** Các role trong `WorkflowSupportRoles.cs` TUYỆT ĐỐI KHÔNG được gọi `IWorkflowDispatcher` hoặc bất kỳ thứ gì khởi tạo workflow mới.

**Lỗi gốc đã từng xảy ra:**

```
AppointmentSchedulingWorkflow
  → DispatchAgentAsync(intent: "scheduling")
  → ChiefMedicalAgent route tới SchedulingAgentRole (AgentRuntime)
  → SchedulingAgentRole gọi dispatcher.StartAppointmentSchedulingAsync(...)
  → Khởi tạo AppointmentSchedulingWorkflow mới
  → Vòng lặp vô hạn ❌
```

**Cách đã khắc phục:** Dùng intent đặc thù (`"specialty_routing"`, `"audit_collect"`, v.v.) chỉ khớp với `WorkflowSupportRoles` — không bao giờ route sang `AgentRuntime`.

---

### Không được dùng dict initializer trong lambda Temporal

Ràng buộc của Temporal deterministic runtime: expression-tree lambda truyền vào `Workflow.ExecuteActivityAsync` không được chứa `new Dictionary<>{ }` hay object initializer phức tạp nội tuyến — gây lỗi **CS8074** tại compile time.

```csharp
// ✗ SAI — lỗi CS8074
await Workflow.ExecuteActivityAsync(
    (ClinicalActivities a) => a.DispatchAgentAsync(
        new AgentDispatchInput(userId, "intent", "input",
            new Dictionary<string, string> { ["key"] = "value" })),  // ← lỗi ở đây
    opts);

// ✓ ĐÚNG — tách ra biến cục bộ trước khi truyền vào lambda
var ctx = new Dictionary<string, string> { ["key"] = "value" };
var dispatch = new AgentDispatchInput(userId, "intent", "input", ctx);
await Workflow.ExecuteActivityAsync(
    (ClinicalActivities a) => a.DispatchAgentAsync(dispatch),
    opts);
```

---

### Tất cả ID phải dùng `Guid.CreateVersion7()`

Toàn dự án dùng `Guid.CreateVersion7()` (UUID v7 sắp xếp theo thời gian) — **không dùng** `Guid.NewGuid()`. Áp dụng cho: `TaskId`, workflow correlation ID, booking ID, report ID. Workflow ID dùng format tiền tố chuỗi + `Guid.CreateVersion7():N` để dễ đọc và lọc trên dashboard Temporal.

---

### TreatWarningsAsErrors = true

Tất cả 13 project đều có `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`. Các warning phổ biến bị chuyển thành lỗi:

| Mã lỗi | Nguyên nhân                                                                    | Cách khắc phục                                         |
| ------ | ------------------------------------------------------------------------------ | ------------------------------------------------------ |
| CS9113 | Tham số primary constructor không được dùng trong thân class                   | Chuyển sang constructor thông thường với field rõ ràng |
| CS8074 | Dict initializer hoặc object initializer phức tạp trong expression-tree lambda | Tách ra biến cục bộ trước lambda (xem ví dụ trên)      |
| CS0105 | Directive `using` bị trùng lặp                                                 | Xóa bản thừa                                           |

---

### Hướng dẫn cài đặt timeout activity

| Loại bước                     | `StartToCloseTimeout` khuyến nghị | Lý do                                |
| ----------------------------- | --------------------------------- | ------------------------------------ |
| Tìm slot, kiểm tra bảo hiểm   | 2 phút                            | Gọi HIS API ngoài — phản hồi nhanh   |
| Ghi lịch hẹn (commit booking) | 2 phút                            | Ghi HL7; idempotent với `booking_id` |
| Thu thập log audit            | 5 phút                            | Có thể quét kho log lớn              |
| Sinh tường thuật bằng LLM     | 5 phút                            | Độ trễ GPT-4 + khả năng retry        |
| Xuất báo cáo + tạo hash       | 2 phút                            | CPU-bound; hoàn thành nhanh          |

---

### Quy ước truyền Context giữa các bước

Workflow truyền dữ liệu giữa các bước qua `Dictionary<string, string>` trong `AgentDispatchInput.Context`. Tên key là quy ước chung — cần ghi rõ trong XML doc của class workflow khi thêm bước mới.

**Không được** truyền binary blob hoặc chuỗi JSON lớn quá 64KB qua Context. Thay vào đó hãy lưu vào storage và truyền đường dẫn hoặc ID tham chiếu.

---

## 13. Vòng Lặp Suy Luận Agentic — Think → Act → Observe → Improve

> **Câu hỏi gốc:** Làm sao workflow _suy nghĩ_, _chọn công cụ_, và _cải thiện theo thời gian_?

Phần này giải thích kiến trúc agentic đầy đủ và các thành phần đã được implement.

### 13.1 Vấn đề với "One-shot agent"

Role cũ (trước khi có ReAct loop):

```
AgentTask → IAgentRole.HandleAsync() → tool.InvokeAsync() → AgentRoleResult
                                              ↑
                                    Cứng một tool, không loop
```

Nhược điểm:

- Không thể chain nhiều bước (tra cứu → tính toán → format kết quả)
- Không biết kết quả tệ → không tự sửa
- LLM provider cố định → không học provider nào tốt hơn
- Kết quả workflow thành công/thất bại không được ghi lại để cải thiện lần sau

### 13.2 ReAct Loop — `IReActLoop`

**Pattern:** Yao et al. (2022) _"ReAct: Synergizing Reasoning and Acting in Language Models"_

```
Loop (tối đa MaxIterations lần):
  1. LLM phát sinh:  Thought: <lý do>
                     Action: <tên tool>
                     Action Input: <JSON args>

  2. ReActLoop gọi tool, nhận Observation

  3. Append "Observation: {output}" vào conversation history

  4. LLM phát sinh lần tiếp (đã có thêm context)

  Khi LLM phát sinh "Final Answer: ..." → kết thúc loop
```

**Files:**

- Interface: `src/Hope.Agent.Application/Agents/ReAct/IReActLoop.cs`
- Implementation: `src/Hope.Agent.MultiAgent/ReAct/ReActLoop.cs`
- Đăng ký: `AddScoped<IReActLoop, ReActLoop>()` trong `MultiAgent.DependencyInjection`

**Sử dụng trong role:**

```csharp
// ClinicalAgent tự động dùng ReAct nếu IReActLoop được inject
internal sealed class ClinicalAgent(
    IRetriever retriever,
    ILLMRouter llm,
    IReActLoop? reactLoop = null,   // nullable = optional
    IReflector? reflector = null) : IAgentRole
{
    public async Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
    {
        if (reactLoop is not null)
        {
            var result = await reactLoop.RunAsync(task, [], new ReActOptions
            {
                MaxIterations = 5,
                EnableReflection = reflector is not null,
                SystemPromptSuffix = $"Clinical Guidelines:\n{ragContext}",
            }, ct);
            return new AgentRoleResult(Name, result.Success, result.FinalAnswer, ...);
        }
        // fallback: single-shot LLM call
    }
}
```

**Tùy chỉnh `ReActOptions`:**

| Field                | Default | Ý nghĩa                                             |
| -------------------- | ------- | --------------------------------------------------- |
| `MaxIterations`      | 5       | Số vòng tối đa trước khi trả kết quả cuối cùng      |
| `Temperature`        | 0.1     | Nhiệt độ LLM — thấp = nhất quán hơn                 |
| `EnableReflection`   | false   | Bật/tắt Constitutional-AI critique sau Final Answer |
| `SystemPromptSuffix` | null    | Thêm context cụ thể (RAG, hướng dẫn lâm sàng...)    |

### 13.3 Self-Reflection — `IReflector`

Sau khi ReAct trả `Final Answer` (hoặc `ClinicalAgent` one-shot), `IReflector.CritiqueAndRefineAsync` được gọi khi `EnableReflection = true`.

**Cách hoạt động (`LlmReflector` trong LLMGateway):**

```
Input:  userMessage + draftAnswer
Output: { score: 0..1, critique: "...", refined: "..." }

→ score >= 0.6 → dùng refined answer
→ score < 0.6  → cảnh báo log + vẫn dùng refined
```

`IReflector` đã được implement và đăng ký (`AddSingleton<IReflector, LlmReflector>()` trong `LLMGateway`). `ClinicalAgent` nhận nó qua optional injection.

Metadata trả về cho người gọi:

```
result.Metadata["reflection_score"]   = "0.87"
result.Metadata["reflection_critique"] = "Answer correctly cited sources..."
```

### 13.4 Adaptive Provider Selection — `IAdaptiveRouter`

`ChiefMedicalAgent` sử dụng UCB1 multi-armed bandit để chọn LLM provider tối ưu cho mỗi intent.

**Flow trong `SelectRoleAsync`:**

```
1. Gọi adaptiveRouter.SelectChatAsync(task.Intent)
   → RouterChoice { Provider = "openai", Model = "gpt-4o" }  // provider từng hoạt động tốt nhất cho intent này

2. Gọi llm.SelectChat(adaptiveChoice.Provider)
   → IChatCompletionProvider

3. Sau khi LLM trả kết quả:
   adaptiveRouter.RecordOutcomeAsync(intent, provider, model, reward=1.0, latencyMs, failed=false)
   → Bandit cập nhật ước lượng UCB1

4. Nếu LLM thất bại:
   adaptiveRouter.RecordOutcomeAsync(..., reward=0.0, failed=true)
   → Provider đó giảm điểm UCB1
```

**Theo thời gian:** UCB1 tự động chuyển sang provider đáng tin cậy hơn cho mỗi loại intent lâm sàng, không cần cấu hình thủ công.

### 13.5 Vòng Phản Hồi — `IWorkflowOutcomeSink`

Mỗi lần `ClinicalActivities.DispatchAgentAsync` hoàn thành, outcome được ghi vào hệ thống học.

**Files:**

- Interface: `src/Hope.Agent.Application/Agents/IWorkflowOutcomeSink.cs`
- Implementation: `src/Hope.Agent.MultiAgent/Learning/WorkflowOutcomeSink.cs`
- Đăng ký: `AddScoped<IWorkflowOutcomeSink, WorkflowOutcomeSink>()`

**Luồng:**

```
ClinicalActivities.DispatchAgentAsync()
    ↓ role thực thi xong
    ↓
WorkflowOutcomeSink.RecordAsync(outcome)
    ├── IFeedbackStore.RecordAsync(rating: +1/-1)
    │        → DB row: intent, role, success/failure
    └── ISkillLibrary.IncrementUsageAsync(skillId, rewardDelta)
             → EMA reward update cho LearnedSkill
```

**Kết quả theo thời gian:**

- Intent `"optimize_slots"` thường dẫn đến booking thành công → reward cao → `OptimizationAgent` được ưu tiên hơn
- Intent `"clinical"` với một provider nhất định có latency tệ → adaptive router giảm điểm provider đó

### 13.6 Sơ Đồ Tổng Thể — Các Vòng Lặp Cải Tiến

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                         TEMPORAL WORKFLOW                                     │
│                                                                               │
│  Step N ──→ DispatchAgentAsync(intent, input, context)                       │
│                        │                                                      │
│              ┌─────────▼──────────┐                                          │
│              │  ChiefMedicalAgent │ ←── IAdaptiveRouter (UCB1 bandit)        │
│              │  SelectRoleAsync() │     chọn LLM provider tối ưu/intent      │
│              └─────────┬──────────┘                                          │
│                        │  chọn role theo intent                               │
│              ┌─────────▼────────────────────────────────────────────┐        │
│              │  ClinicalAgent  (priority: ToT ▶ ReAct ▶ one-shot)   │        │
│              │                                                        │        │
│              │  ① IPatientMemoryService.RetrieveAsync()              │        │
│              │       └─ Qdrant vector search: lịch sử bệnh nhân      │        │
│              │              ↓ inject vào SystemPromptSuffix           │        │
│              │                                                        │        │
│              │  ② ITreeOfThoughts.RunAsync()  ──────────────────┐   │        │
│              │       branch[0] ── ReActLoop ──────────┐          │   │        │
│              │       branch[1] ── ReActLoop ──────────┼─ IJudge  │   │        │
│              │       branch[2] ── ReActLoop ──────────┘  score   │   │        │
│              │                                    best answer ◀──┘   │        │
│              │                                                        │        │
│              │  ③ Emergency keywords? ──→ AgentHandoff("emergency") │        │
│              │                                                        │        │
│              │  ④ IPatientMemoryService.WriteAsync()                 │        │
│              │       └─ Qdrant upsert: ghi lại câu hỏi + câu trả lời │        │
│              └────────────────────────────────────────────────────────┘        │
│                        │                                                      │
│              ┌─────────▼──────────────────────────────────────────┐          │
│              │  WorkflowOutcomeSink  (fire-and-forget)             │          │
│              │    ├─ IFeedbackStore.RecordAsync()    (rating ±1)   │          │
│              │    ├─ ISkillLibrary.IncrementUsageAsync() (EMA)     │          │
│              │    └─ IOptimizationCostHints.RecordOutcomeAsync()   │          │
│              │         (booking intents → MCMF adaptive costs)     │          │
│              └────────────────────────────────────────────────────┘          │
└──────────────────────────────────────────────────────────────────────────────┘
           ↑ Feedback loops tự động cải thiện hệ thống theo thời gian
```

### 13.7 Thêm ReAct vào Role Mới

1. Inject `IReActLoop?` vào constructor của role
2. Gọi `reactLoop.RunAsync(task, tools, opts, ct)` thay vì trực tiếp gọi tool
3. Không cần đăng ký gì thêm — `IReActLoop` đã registered global

```csharp
// Ví dụ: role mới dùng ReAct
internal sealed class MyNewRole(IToolRegistry tools, IReActLoop? reactLoop = null) : IAgentRole
{
    public string Name => "my_role";
    public string Description => "Does X, Y, Z using available tools.";
    public IReadOnlyList<string> Intents { get; } = ["intent_a", "intent_b"];

    public async Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
    {
        var relevantTools = tools.All.Where(t => t.Definition.Name.StartsWith("my_")).ToList();

        if (reactLoop is not null)
        {
            var result = await reactLoop.RunAsync(task, relevantTools,
                new ReActOptions { MaxIterations = 3, EnableReflection = true }, ct);
            return new AgentRoleResult(Name, result.Success, result.FinalAnswer,
                new Dictionary<string, string> { ["steps"] = result.Steps.Count.ToString() });
        }

        // Fallback nếu ReAct chưa available
        var tool = tools.Find("my_primary_tool")!;
        var output = await tool.InvokeAsync(task.Input, new ToolInvocationContext(...), ct);
        return new AgentRoleResult(Name, true, output);
    }
}
```

### 13.8 Tóm Tắt Tính Năng Agentic Đã Implement

Tất cả 4 pattern nâng cao từ roadmap đã được implement đầy đủ:

| Pattern                   | Interface                | Implementation         | Trạng thái    |
| ------------------------- | ------------------------ | ---------------------- | ------------- |
| **Tree of Thoughts**      | `ITreeOfThoughts`        | `TreeOfThoughtsSearch` | ✅ Hoàn thành |
| **MCMF Adaptive Costs**   | `IOptimizationCostHints` | `AdaptiveCostHints`    | ✅ Hoàn thành |
| **Cross-workflow Memory** | `IPatientMemoryService`  | `PatientMemoryService` | ✅ Hoàn thành |
| **Multi-hop Handoffs**    | `AgentHandoff` (record)  | Populated trong roles  | ✅ Hoàn thành |

Chi tiết từng pattern: xem §13.9 – §13.12.

---

### 13.9 Tree of Thoughts — `ITreeOfThoughts`

**Pattern:** Yao et al. (2023) _"Tree of Thoughts: Deliberate Problem Solving with Large Language Models"_

Thay vì một chuỗi suy luận tuyến tính (ReAct), Tree of Thoughts tạo **N nhánh song song** rồi chọn nhánh tốt nhất theo điểm `IJudge`.

#### Luồng hoạt động

```
ITreeOfThoughts.RunAsync(task, tools, ToTOptions)
        │
        ├─ Task.WhenAll: chạy BranchCount ReActLoop song song
        │       branch[0]: ReActLoop.RunAsync(temp=0.7) → FinalAnswer_0
        │       branch[1]: ReActLoop.RunAsync(temp=0.7) → FinalAnswer_1
        │       branch[2]: ReActLoop.RunAsync(temp=0.7) → FinalAnswer_2
        │
        ├─ Task.WhenAll: IJudge.ScoreAsync cho từng nhánh
        │       JudgeVerdict { Score: 0..1, Passed, Reasoning }
        │
        └─ Trả về nhánh có Score cao nhất → ToTResult.BestAnswer
```

Temperature cao hơn (`0.7` mặc định so với `0.2` của ReAct) tạo ra **diversity** giữa các nhánh — nhánh 1 có thể dùng cách tiếp cận bệnh học, nhánh 2 dùng hướng dẫn điều trị, nhánh 3 ưu tiên phân tích rủi ro.

#### Interface

```csharp
// src/Hope.Agent.Application/Agents/ReAct/ITreeOfThoughts.cs
public interface ITreeOfThoughts
{
    Task<ToTResult> RunAsync(
        AgentTask task,
        IReadOnlyList<IAgentTool> availableTools,
        ToTOptions? options = null,
        CancellationToken ct = default);
}

public sealed class ToTOptions
{
    public int BranchCount { get; init; } = 3;       // số nhánh song song
    public int MaxStepsPerBranch { get; init; } = 3;  // ReAct iterations/nhánh
    public float Temperature { get; init; } = 0.7f;   // cao = đa dạng hơn
    public string? SystemPromptSuffix { get; init; }
}

public sealed record ToTResult(
    bool Success,
    string BestAnswer,
    IReadOnlyList<ToTBranch> Branches,
    int WinnerBranchIndex,
    double WinnerScore);

public sealed record ToTBranch(
    int Index, string Answer, double Score, bool Passed,
    string JudgeReasoning, int StepCount);
```

#### Tích hợp với ClinicalAgent

`ClinicalAgent` sử dụng priority hierarchy:

```
ToT available?  → dùng ToT     (chất lượng cao nhất, chi phí tính toán cao nhất)
ReAct available?→ dùng ReAct   (trung bình)
else            → one-shot LLM (nhanh nhất, cơ bản nhất)
```

```csharp
// Mỗi path đều inject patient memory context qua SystemPromptSuffix
if (treeOfThoughts is not null)
{
    var totResult = await treeOfThoughts.RunAsync(task, [], new ToTOptions
    {
        BranchCount = 3, MaxStepsPerBranch = 3, Temperature = 0.6f,
        SystemPromptSuffix = contextSuffix,
    }, ct);
    citations["tot_branches"]     = totResult.Branches.Count.ToString();
    citations["tot_winner_score"] = totResult.WinnerScore.ToString("F2");
}
```

#### Metadata trả về

| Key                   | Ý nghĩa                                  |
| --------------------- | ---------------------------------------- |
| `tot_branches`        | Số nhánh đã khám phá                     |
| `tot_winner_score`    | Điểm IJudge của nhánh thắng (0..1)       |
| `react_steps`         | Tổng số bước ReAct (nếu dùng path ReAct) |
| `reflection_critique` | Nội dung critique nếu bật reflection     |

#### Đăng ký DI

```csharp
// MultiAgent/DependencyInjection.cs (đã có)
services.AddScoped<ITreeOfThoughts, TreeOfThoughtsSearch>();
```

`IJudge` được inject từ `LLMGateway` (singleton). `IReActLoop` được inject từ `MultiAgent` (scoped). Mỗi nhánh gọi `reactLoop` độc lập — không có shared state.

---

### 13.10 MCMF Adaptive Costs — `IOptimizationCostHints`

MCMF cost function truyền thống chỉ dùng **thông tin tĩnh** (specialty, wait time, urgency). Với adaptive costs, bác sĩ có lịch sử booking thành công cao được ưu tiên qua **cost thấp hơn** trong graph MCMF.

#### Thuật toán EMA

```
Khi booking kết thúc (via WorkflowOutcomeSink):
    key = "{doctorId}:{specialty}"
    EMA_new = EMA_prev + α × (outcome - EMA_prev)
    α = 0.3  (≈ 3-4 sample để half-life decay)

Khi tính cost cạnh:
    successRate = EMA(key)  // default = 0.85 nếu chưa có data
    bonus = round((successRate - 0.5) × 30)  // 0..15 points
    edgeCost -= max(0, bonus)
```

Ví dụ thực tế:

- Bác sĩ A: 95% booking thành công → bonus = `(0.95-0.5)×30 = 13.5 ≈ 14` → cost giảm 14
- Bác sĩ B: 60% booking thành công → bonus = `(0.60-0.5)×30 = 3` → cost giảm 3
- Bác sĩ C: chưa có data → default 85% → bonus = `(0.85-0.5)×30 = 10.5 ≈ 11` → cost giảm 11

#### Interface

```csharp
// src/Hope.Agent.Application/Tools/IOptimizationCostHints.cs
public interface IOptimizationCostHints
{
    Task RecordOutcomeAsync(string doctorId, string specialty, bool succeeded, CancellationToken ct);
    Task<double> GetSuccessRateAsync(string doctorId, string specialty,
        double defaultRate = 0.85, CancellationToken ct = default);
}
```

#### Vòng phản hồi hoàn chỉnh

```
AppointmentSchedulingWorkflow
    → Bước 4 (his_booking) hoàn thành
    → ClinicalActivities ghi WorkflowOutcome { Intent="his_booking", Context={doctor_id, specialty} }
    → WorkflowOutcomeSink.RecordAsync()
         └─ IOptimizationCostHints.RecordOutcomeAsync(doctorId, specialty, success)
              └─ EMA update trong AdaptiveCostHints

Lần đặt lịch tiếp theo:
    → OptimizeBatchAppointmentsTool pre-fetch rates
    → ComputeEdgeCost nhận successRate mới → ưu tiên bác sĩ đã chứng minh năng lực
```

**Intents kích hoạt recording:** `his_booking`, `optimize_slots`, `schedule`. Context phải chứa key `doctor_id` (hoặc `doctor`) và `specialty`.

#### Lưu ý về persistence

`AdaptiveCostHints` là **in-memory**. Statistics reset khi restart. Để persist:

1. Thay `AdaptiveCostHints` bằng `EfAdaptiveCostHints` (EF Core backed)
2. Đăng ký trong DI: `services.AddSingleton<IOptimizationCostHints, EfAdaptiveCostHints>()`
3. Không cần thay đổi interface hay `OptimizeBatchAppointmentsTool`

#### Đăng ký DI

```csharp
// Tools/DependencyInjection.cs (đã có)
services.AddSingleton<IOptimizationCostHints, AdaptiveCostHints>();
```

Singleton vì EMA state phải tích lũy qua nhiều request scope.

---

### 13.11 Cross-Workflow Patient Memory — `IPatientMemoryService`

Mỗi workflow run là stateless — không biết bệnh nhân này đã được chẩn đoán gì trong lần khám trước. `IPatientMemoryService` giải quyết vấn đề này bằng **vector memory** qua Qdrant.

#### Luồng ghi/đọc

```
Trước khi ClinicalAgent reasoning:
    IPatientMemoryService.RetrieveAsync(patientId, query=task.Input, topK=3)
        → embed query (IEmbeddingProvider)
        → IMemoryStore.SearchAsync(patientId, embedding, topK, MemoryKind.Clinical)
        → trả về List<string> content, sorted by similarity
        → inject vào SystemPromptSuffix: "Previous patient history:\n1. ...\n2. ..."

Sau khi ClinicalAgent hoàn thành:
    IPatientMemoryService.WriteAsync(patientId, "Q: {input}\nA: {answer[..500]}")
        → embed content (IEmbeddingProvider)
        → IMemoryStore.UpsertAsync(MemoryRecord { UserId=patientId, Kind=Clinical }, embedding)
        → Qdrant upsert (idempotent)
```

#### Interface

```csharp
// src/Hope.Agent.Application/Agents/IPatientMemoryService.cs
public interface IPatientMemoryService
{
    Task WriteAsync(Guid patientId, string content,
        MemoryKind kind = MemoryKind.Clinical, float importance = 0.7f,
        CancellationToken ct = default);

    Task<IReadOnlyList<string>> RetrieveAsync(Guid patientId, string query,
        int topK = 3, CancellationToken ct = default);
}
```

#### MemoryRecord domain entity

```csharp
// src/Hope.Agent.Domain/Memory/MemoryRecord.cs
public sealed class MemoryRecord
{
    public Guid Id { get; init; }        // Guid.CreateVersion7()
    public Guid UserId { get; init; }    // = patientId
    public MemoryKind Kind { get; init; }// Clinical = 3
    public string Content { get; init; }
    public float Importance { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public enum MemoryKind { Episodic=0, Semantic=1, Procedural=2, Clinical=3 }
```

#### Yêu cầu cơ sở hạ tầng

`PatientMemoryService` yêu cầu **Qdrant** chạy và có collection `clinical_memories`. Nếu Qdrant không khả dụng, mọi exception đều bị swallow — `ClinicalAgent` vẫn hoạt động bình thường, chỉ thiếu patient history context.

#### Nội dung được ghi vào memory

Content bị giới hạn 500 ký tự để tránh oversized vector embedding:

```csharp
var summary = answer.Length > 500 ? answer[..500] : answer;
_ = patientMemory.WriteAsync(task.UserId, $"Q: {task.Input}\nA: {summary}", ct: ct);
```

#### Đăng ký DI

```csharp
// MultiAgent/DependencyInjection.cs (đã có)
services.AddScoped<IPatientMemoryService, PatientMemoryService>();
```

`IEmbeddingProvider` và `IMemoryStore` đã được đăng ký trong `Infrastructure` và `LLMGateway` — không cần thêm gì.

---

### 13.12 Multi-hop Agent Handoffs

`ChiefMedicalAgent` đã xử lý handoffs từ trước — loop tối đa 4 hops:

```csharp
// ChiefMedicalAgent.DispatchAsync (đã có, không cần thay đổi)
if (result.Handoffs is { Count: > 0 } && hop < 3)
{
    var next = result.Handoffs[0];
    if (_byName.TryGetValue(next.TargetRole, out var nextRole))
    {
        currentTask = currentTask with { Intent = next.TargetRole, Input = next.Payload };
        current = nextRole;
        continue;   // vòng lặp hop tiếp theo
    }
}
```

Phần mới: các role giờ **tự populate** field `Handoffs` dựa trên nội dung output của mình.

#### Bảng handoff theo role

| Role              | Điều kiện phát sinh handoff       | Target         | Reason                                                       | Payload        |
| ----------------- | --------------------------------- | -------------- | ------------------------------------------------------------ | -------------- |
| `ClinicalAgent`   | Output chứa từ khóa cấp cứu       | `emergency`    | `"Clinical reasoning detected: {keyword}"`                   | Full answer    |
| `ComplianceAgent` | PHI markers được phát hiện        | `clinical`     | `"Compliance blocked: {markers}. Provide safe alternative."` | Original input |
| `SchedulingAgent` | Tool trả về no-slot / unavailable | `optimization` | `"No standard slot; request MCMF optimization"`              | Original input |
| `EmergencyAgent`  | Urgency level ≥ 4                 | `notification` | `"high-urgency triage"`                                      | Full response  |

#### Từ khóa phát hiện cấp cứu (ClinicalAgent)

```csharp
private static readonly string[] EmergencyMarkers =
[
    "stroke", "đột quỵ",
    "myocardial infarction", "nhồi máu cơ tim", "heart attack",
    "sepsis", "nhiễm khuẩn huyết",
    "cardiac arrest", "ngừng tim",
    "respiratory failure", "suy hô hấp",
    "code blue", "cấp cứu ngay",
    "immediate emergency", "life-threatening",
];
```

Matching: `answer.Contains(marker, StringComparison.OrdinalIgnoreCase)` — không phân biệt hoa thường, không phân biệt ngôn ngữ (Anh/Việt cả hai).

#### Ví dụ luồng multi-hop

```
AgentTask(intent="clinical", input="bệnh nhân đau ngực dữ dội kèm vã mồ hôi")
    │
    ▼  ClinicalAgent trả lời: "...có thể là nhồi máu cơ tim cấp — cần cấp cứu ngay..."
    │  → phát hiện "nhồi máu cơ tim" → AgentHandoff(target="emergency", ...)
    │
    ▼  hop 1: ChiefMedicalAgent chuyển sang EmergencyAgent
              input = full clinical answer (Payload)
    │
    ▼  EmergencyAgent: "level=5, route=er" → level≥4 → AgentHandoff(target="notification")
    │
    ▼  hop 2: ChiefMedicalAgent chuyển sang NotificationAgent
              → gửi cảnh báo khẩn cấp qua IEventPublisher + IRealtimeNotifier
    │
    ▼  Kết thúc — trả về trace đầy đủ 3 role trong AgentDispatchResult
```

#### Ghi chú về payload

`AgentHandoff.Payload` truyền **toàn bộ output** của role nguồn (không chỉ tóm tắt). Role đích có thể dùng đây như input. Giới hạn: không truyền binary hoặc JSON > 64KB qua Payload (xem quy tắc §12).
