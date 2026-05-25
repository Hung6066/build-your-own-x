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

| Class               | `Name`         | Intent chính                                       |
| ------------------- | -------------- | -------------------------------------------------- |
| `SchedulingAgent`   | `scheduling`   | `schedule`, `appointment`, `reschedule`, `booking` |
| `ClinicalAgent`     | `clinical`     | `diagnosis`, `triage`, `clinical_note`             |
| `BillingAgent`      | `billing`      | `billing`, `insurance`, `coverage`, `claim`        |
| `ComplianceAgent`   | `compliance`   | `audit`, `compliance`, `policy`                    |
| `EmergencyAgent`    | `emergency`    | `emergency`, `critical`, `code_blue`               |
| `NotificationAgent` | `notification` | `notify`, `alert`, `reminder`                      |

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
