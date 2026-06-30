<#
  Hope.Agent — Clinical Persistence Flow Seed

  Purpose:
    - Seed realistic synthetic clinical data through authenticated AI Agent tools
    - Persist rows in Postgres for:
        medical_summaries
        reminder_records
        audit_reports
        optimization_cost_hints
    - Run an audit tool chain: collect_audit_logs -> detect_audit_anomalies -> export_audit_report

  Prerequisites:
    - API is running (default: http://localhost:5080)
    - Postgres container is running and migrations have been applied

  Usage:
    .\tests\hope-clinical-persistence-flows.ps1
    .\tests\hope-clinical-persistence-flows.ps1 -BaseUrl http://localhost:5080
#>

param(
    [string]$BaseUrl = "http://localhost:5080",
    [string]$ClientId = "doctor-nguyen",
    [string]$Secret = "HopeAgentDev2026!",
    [string]$PostgresContainer = "hope-agent-postgres-1",
    [string]$PostgresUser = "hope",
    [string]$PostgresDb = "hope_agent",
    [switch]$SkipSchemaEnsure,
    [switch]$DirectPostgres
)

$ErrorActionPreference = "Stop"

function Convert-Body([string]$content) {
    if ([string]::IsNullOrWhiteSpace($content)) { return $null }
    try { return ($content | ConvertFrom-Json) } catch { return $content }
}

function Invoke-Api(
    [string]$Method,
    [string]$Path,
    $Body,
    [hashtable]$Headers
) {
    $uri = "$BaseUrl$Path"
    $json = if ($null -ne $Body) { $Body | ConvertTo-Json -Depth 32 -Compress } else { $null }

    $invokeParams = @{
        Uri = $uri
        Method = $Method
        Headers = $Headers
        ContentType = "application/json"
        SkipHttpErrorCheck = $true
    }
    if ($null -ne $json) { $invokeParams.Body = $json }

    $res = Invoke-WebRequest @invokeParams
    [pscustomobject]@{
        StatusCode = [int]$res.StatusCode
        Body = Convert-Body $res.Content
        Raw = $res.Content
    }
}

function Invoke-Tool([string]$Name, $Arguments, [hashtable]$Headers) {
    $res = Invoke-Api -Method "POST" -Path "/v1/tools/$Name/invoke" -Body @{ arguments = $Arguments } -Headers $Headers
    if ($res.StatusCode -lt 200 -or $res.StatusCode -ge 300) {
        throw "Tool '$Name' failed (status=$($res.StatusCode)): $($res.Raw)"
    }
    return $res.Body
}

function Invoke-PsqlScalar([string]$Sql) {
    $raw = docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -t -A -c $Sql
    if ($LASTEXITCODE -ne 0) { throw "Postgres query failed: $Sql" }
    return ($raw | Select-Object -First 1).ToString().Trim()
}

function Get-TableCount([string]$Table) {
    return [int](Invoke-PsqlScalar "SELECT COUNT(*) FROM $Table;")
}

function Invoke-Psql([string]$Sql) {
    docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -v ON_ERROR_STOP=1 -c $Sql | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Postgres command failed." }
}

function Ensure-PersistenceSchema {
    Write-Host "Ensuring Postgres persistence tables exist..." -ForegroundColor Cyan

    Invoke-Psql @'
CREATE TABLE IF NOT EXISTS medical_summaries (
    "Id" uuid PRIMARY KEY,
    "SummaryId" character varying(64) NOT NULL UNIQUE,
    "PatientId" uuid NULL,
    "UserId" uuid NULL,
    "SummaryType" character varying(64) NOT NULL,
    "Audience" character varying(64) NOT NULL,
    "Specialty" character varying(128) NULL,
    "SourceContext" text NOT NULL,
    "SummaryText" text NOT NULL,
    "Model" character varying(128) NULL,
    "Status" character varying(32) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    "CorrelationId" character varying(128) NULL
);
'@

    Invoke-Psql @'
CREATE TABLE IF NOT EXISTS reminder_records (
    "Id" uuid PRIMARY KEY,
    "ReminderId" character varying(64) NOT NULL UNIQUE,
    "PatientId" uuid NOT NULL,
    "UserId" uuid NULL,
    "WorkflowId" character varying(128) NULL,
    "ReminderType" character varying(64) NOT NULL,
    "MedicationName" character varying(256) NOT NULL,
    "Dosage" character varying(128) NULL,
    "Frequency" character varying(64) NOT NULL,
    "StartAt" timestamp with time zone NOT NULL,
    "DurationDays" integer NOT NULL,
    "PreferredChannel" character varying(64) NOT NULL,
    "AdherenceRiskScore" integer NOT NULL,
    "Status" character varying(32) NOT NULL,
    "ConfirmedCount" integer NOT NULL DEFAULT 0,
    "MissedCount" integer NOT NULL DEFAULT 0,
    "LastConfirmedAt" timestamp with time zone NULL,
    "LastMissedAt" timestamp with time zone NULL,
    "EscalationReason" text NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    "CorrelationId" character varying(128) NULL
);
'@

    Invoke-Psql @'
CREATE TABLE IF NOT EXISTS audit_reports (
    "Id" uuid PRIMARY KEY,
    "ReportId" character varying(64) NOT NULL UNIQUE,
    "RequestedBy" uuid NOT NULL,
    "ReportType" character varying(64) NOT NULL,
    "PeriodStart" timestamp with time zone NULL,
    "PeriodEnd" timestamp with time zone NULL,
    "Narrative" text NOT NULL,
    "MetricsJson" jsonb NULL,
    "AnomaliesJson" jsonb NULL,
    "Format" character varying(16) NOT NULL,
    "ExportPath" character varying(512) NOT NULL,
    "IntegrityHash" character varying(128) NOT NULL,
    "ByteSize" integer NOT NULL,
    "SigningAlgorithm" character varying(32) NOT NULL,
    "ExportedAt" timestamp with time zone NOT NULL,
    "Status" character varying(32) NOT NULL,
    "CorrelationId" character varying(128) NULL
);
'@

    Invoke-Psql @'
CREATE TABLE IF NOT EXISTS optimization_cost_hints (
    "Id" uuid PRIMARY KEY,
    "DoctorId" character varying(64) NOT NULL,
    "Specialty" character varying(128) NOT NULL,
    "SuccessRate" double precision NOT NULL,
    "Samples" bigint NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL
);
'@

    Invoke-Psql 'CREATE INDEX IF NOT EXISTS "IX_medical_summaries_PatientId_CreatedAt" ON medical_summaries ("PatientId", "CreatedAt");'
    Invoke-Psql 'CREATE INDEX IF NOT EXISTS "IX_medical_summaries_UserId_CreatedAt" ON medical_summaries ("UserId", "CreatedAt");'
    Invoke-Psql 'CREATE INDEX IF NOT EXISTS "IX_reminder_records_WorkflowId" ON reminder_records ("WorkflowId");'
    Invoke-Psql 'CREATE INDEX IF NOT EXISTS "IX_reminder_records_PatientId_StartAt" ON reminder_records ("PatientId", "StartAt");'
    Invoke-Psql 'CREATE INDEX IF NOT EXISTS "IX_reminder_records_UserId_UpdatedAt" ON reminder_records ("UserId", "UpdatedAt");'
    Invoke-Psql 'CREATE INDEX IF NOT EXISTS "IX_audit_reports_RequestedBy_ExportedAt" ON audit_reports ("RequestedBy", "ExportedAt");'
    Invoke-Psql 'CREATE INDEX IF NOT EXISTS "IX_audit_reports_ReportType_PeriodEnd" ON audit_reports ("ReportType", "PeriodEnd");'
    Invoke-Psql 'CREATE UNIQUE INDEX IF NOT EXISTS "IX_optimization_cost_hints_DoctorId_Specialty" ON optimization_cost_hints ("DoctorId", "Specialty");'
}

function ConvertTo-SqlText([string]$Value) {
    return "'" + ($Value -replace "'", "''") + "'"
}

function ConvertTo-SqlNullableText([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return "NULL" }
    return ConvertTo-SqlText $Value
}

function Invoke-DirectPostgresFlow {
    $beforeSummaries = Get-TableCount "medical_summaries"
    $beforeReminders = Get-TableCount "reminder_records"
    $beforeAuditReports = Get-TableCount "audit_reports"
    $beforeCostHints = Get-TableCount "optimization_cost_hints"

    Write-Host "Rows before: medical_summaries=$beforeSummaries reminder_records=$beforeReminders audit_reports=$beforeAuditReports optimization_cost_hints=$beforeCostHints"

    $patientId = [guid]::NewGuid().ToString()
    $userId = [guid]::NewGuid().ToString()
    $summaryId = "SUM-REALFLOW-$((Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'))"
    $reminderId = "REM-REALFLOW-$((Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'))"
    $reportId = "AUDIT-REALFLOW-$((Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'))"
    $now = (Get-Date).ToUniversalTime().ToString("O")
    $startAt = (Get-Date).ToUniversalTime().AddHours(1).ToString("O")
    $periodStart = (Get-Date).ToUniversalTime().AddDays(-30).ToString("O")
    $periodEnd = (Get-Date).ToUniversalTime().ToString("O")

    $ehrContext = @"
Bệnh nhân: nữ, 58 tuổi.
Lý do khám: tái khám đái tháo đường type 2 và tăng huyết áp.
Tiền sử: T2DM 8 năm, tăng huyết áp 5 năm, rối loạn lipid máu.
Thuốc hiện tại: Metformin 500mg x 2 lần/ngày, Amlodipine 5mg sáng, Atorvastatin 20mg tối.
Dị ứng: chưa ghi nhận.
Dấu hiệu sinh tồn: HA 138/84 mmHg, mạch 78 lần/phút, BMI 27.1.
Cận lâm sàng gần nhất: HbA1c 7.8%, LDL-C 118 mg/dL, eGFR 82 mL/phút/1.73m2.
Vấn đề: quên thuốc buổi tối 2-3 lần/tuần, cần nhắc thuốc và tái khám sau 30 ngày.
"@

    $summaryText = @"
SOAP Note
S: Bệnh nhân tái khám T2DM/tăng huyết áp, thỉnh thoảng quên thuốc buổi tối 2-3 lần/tuần.
O: HA 138/84, BMI 27.1, HbA1c 7.8%, LDL-C 118, eGFR 82. Chưa ghi nhận dị ứng.
A: Kiểm soát đường huyết chưa đạt mục tiêu; tăng huyết áp tương đối ổn; nguy cơ tuân thủ thuốc trung bình.
P: Duy trì Metformin/Amlodipine/Atorvastatin theo đơn hiện tại, thiết lập nhắc thuốc buổi tối, tái khám sau 30 ngày kèm HbA1c và lipid máu.
"@

    $metricsJson = @{
        report_id = $reportId
        report_type = "compliance"
        period_start = $periodStart
        period_end = $periodEnd
        collected_at = $now
        metrics = @{
            total_patient_records_accessed = 892
            phi_export_events = 34
            consent_violations = 0
            role_escalations = 2
            failed_compliance_checks = 5
            icd10_coding_sessions = 156
            uncoded_discharge_summaries = 8
        }
        event_count = 4210
    } | ConvertTo-Json -Depth 8 -Compress

    $anomaliesJson = @{
        anomaly_count = 1
        risk_level = "high"
        anomalies = @(
            @{
                type = "bulk_phi_export"
                severity = "high"
                detail = "34 sự kiện xuất dữ liệu PHI - vượt ngưỡng bình thường"
                recommendation = "Xác nhận ủy quyền xuất dữ liệu và kiểm tra DLP logs"
            }
        )
        analyzed_at = $now
    } | ConvertTo-Json -Depth 8 -Compress

    $narrative = @"
Báo cáo tuân thủ 30 ngày ghi nhận hoạt động truy cập hồ sơ bệnh nhân và xuất dữ liệu PHI ở mức cần theo dõi.
Không phát hiện vi phạm đồng ý điều trị. Một số kiểm tra tuân thủ thất bại cần được rà soát theo quy trình vận hành.
Khuyến nghị: đối soát quyền truy cập theo vai trò, rà soát các phiên coding ICD-10 chưa hoàn tất, và duy trì kiểm tra audit hằng tuần.
"@

    $hashInput = "$reportId|$narrative|$metricsJson|$anomaliesJson"
    $hashBytes = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($hashInput))
    $hash = [System.BitConverter]::ToString($hashBytes).Replace("-", "").ToLowerInvariant()
    $byteSize = [System.Text.Encoding]::UTF8.GetByteCount($hashInput)

    Invoke-Psql @"
INSERT INTO medical_summaries (
    "Id", "SummaryId", "PatientId", "UserId", "SummaryType", "Audience", "Specialty",
    "SourceContext", "SummaryText", "Model", "Status", "CreatedAt", "UpdatedAt", "CorrelationId"
) VALUES (
    '$([guid]::NewGuid())', $(ConvertTo-SqlText $summaryId), '$patientId', '$userId', 'soap', 'clinician', $(ConvertTo-SqlText "Nội tiết"),
    $(ConvertTo-SqlText $ehrContext), $(ConvertTo-SqlText $summaryText), 'direct-postgres-seed', 'completed', '$now', '$now', 'direct-postgres-flow'
)
ON CONFLICT ("SummaryId") DO UPDATE SET
    "SummaryText" = EXCLUDED."SummaryText",
    "UpdatedAt" = EXCLUDED."UpdatedAt",
    "Status" = EXCLUDED."Status";
"@

    Invoke-Psql @"
INSERT INTO reminder_records (
    "Id", "ReminderId", "PatientId", "UserId", "WorkflowId", "ReminderType", "MedicationName", "Dosage",
    "Frequency", "StartAt", "DurationDays", "PreferredChannel", "AdherenceRiskScore", "Status",
    "ConfirmedCount", "MissedCount", "LastConfirmedAt", "CreatedAt", "UpdatedAt", "CorrelationId"
) VALUES (
    '$([guid]::NewGuid())', $(ConvertTo-SqlText $reminderId), '$patientId', '$userId', 'seed-reminder-$patientId',
    'medication', 'Metformin', '500mg', 'twice_daily', '$startAt', 30, 'zalo', 55, 'confirmed',
    1, 0, '$now', '$now', '$now', 'direct-postgres-flow'
)
ON CONFLICT ("ReminderId") DO UPDATE SET
    "Status" = EXCLUDED."Status",
    "ConfirmedCount" = EXCLUDED."ConfirmedCount",
    "LastConfirmedAt" = EXCLUDED."LastConfirmedAt",
    "UpdatedAt" = EXCLUDED."UpdatedAt";
"@

    Invoke-Psql @"
INSERT INTO audit_reports (
    "Id", "ReportId", "RequestedBy", "ReportType", "PeriodStart", "PeriodEnd", "Narrative",
    "MetricsJson", "AnomaliesJson", "Format", "ExportPath", "IntegrityHash", "ByteSize",
    "SigningAlgorithm", "ExportedAt", "Status", "CorrelationId"
) VALUES (
    '$([guid]::NewGuid())', $(ConvertTo-SqlText $reportId), '$userId', 'compliance', '$periodStart', '$periodEnd',
    $(ConvertTo-SqlText $narrative), $(ConvertTo-SqlText $metricsJson)::jsonb, $(ConvertTo-SqlText $anomaliesJson)::jsonb,
    'json', '/reports/$reportId.json', '$hash', $byteSize, 'SHA-256', '$now', 'exported', 'direct-postgres-flow'
)
ON CONFLICT ("ReportId") DO UPDATE SET
    "Narrative" = EXCLUDED."Narrative",
    "MetricsJson" = EXCLUDED."MetricsJson",
    "AnomaliesJson" = EXCLUDED."AnomaliesJson",
    "IntegrityHash" = EXCLUDED."IntegrityHash",
    "ExportedAt" = EXCLUDED."ExportedAt",
    "Status" = EXCLUDED."Status";
"@

    Invoke-Psql @"
INSERT INTO optimization_cost_hints (
    "Id", "DoctorId", "Specialty", "SuccessRate", "Samples", "UpdatedAt"
) VALUES (
    '$([guid]::NewGuid())', 'DR-ENDO-001', $(ConvertTo-SqlText "Nội tiết"), 0.92, 12, '$now'
)
ON CONFLICT ("DoctorId", "Specialty") DO UPDATE SET
    "SuccessRate" = EXCLUDED."SuccessRate",
    "Samples" = optimization_cost_hints."Samples" + 1,
    "UpdatedAt" = EXCLUDED."UpdatedAt";
"@

    $afterSummaries = Get-TableCount "medical_summaries"
    $afterReminders = Get-TableCount "reminder_records"
    $afterAuditReports = Get-TableCount "audit_reports"
    $afterCostHints = Get-TableCount "optimization_cost_hints"

    Write-Host "Rows after:  medical_summaries=$afterSummaries reminder_records=$afterReminders audit_reports=$afterAuditReports optimization_cost_hints=$afterCostHints"
    if ($afterSummaries -le $beforeSummaries) { throw "medical_summaries count did not increase." }
    if ($afterReminders -le $beforeReminders) { throw "reminder_records count did not increase." }
    if ($afterAuditReports -le $beforeAuditReports) { throw "audit_reports count did not increase." }
    if ($afterCostHints -lt $beforeCostHints) { throw "optimization_cost_hints count decreased." }

    Write-Host ""
    Write-Host "Latest persisted records:" -ForegroundColor Cyan
    docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -c 'SELECT "SummaryId", "PatientId", "SummaryType", "Audience", "Status", "CreatedAt" FROM medical_summaries ORDER BY "CreatedAt" DESC LIMIT 1;'
    docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -c 'SELECT "ReminderId", "PatientId", "MedicationName", "Frequency", "Status", "ConfirmedCount", "UpdatedAt" FROM reminder_records ORDER BY "UpdatedAt" DESC LIMIT 1;'
    docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -c 'SELECT "ReportId", "ReportType", "Format", "Status", "IntegrityHash", "ExportedAt" FROM audit_reports ORDER BY "ExportedAt" DESC LIMIT 1;'
    docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -c 'SELECT "DoctorId", "Specialty", "SuccessRate", "Samples", "UpdatedAt" FROM optimization_cost_hints ORDER BY "UpdatedAt" DESC LIMIT 1;'

    Write-Host ""
    Write-Host "Clinical direct persistence seed result: PASS" -ForegroundColor Cyan
}

Write-Host "== Hope Clinical Persistence Flow Seed ==" -ForegroundColor Cyan
Write-Host "BaseUrl: $BaseUrl"
Write-Host "PostgresContainer: $PostgresContainer"

if (-not $SkipSchemaEnsure) {
    Ensure-PersistenceSchema
}

if ($DirectPostgres) {
    Invoke-DirectPostgresFlow
    exit 0
}

$login = Invoke-Api -Method "POST" -Path "/v1/auth/login" -Body @{ clientId = $ClientId; secret = $Secret } -Headers @{}
if ($login.StatusCode -ne 200 -or -not $login.Body.accessToken) {
    Write-Host "[FAIL] Login failed (status=$($login.StatusCode))." -ForegroundColor Red
    Write-Host $login.Raw
    exit 1
}

$authHeaders = @{ Authorization = "Bearer $([string]$login.Body.accessToken)" }
Write-Host "[PASS] Login succeeded." -ForegroundColor Green

$beforeSummaries = Get-TableCount "medical_summaries"
$beforeReminders = Get-TableCount "reminder_records"
$beforeAuditReports = Get-TableCount "audit_reports"

Write-Host "Rows before: medical_summaries=$beforeSummaries reminder_records=$beforeReminders audit_reports=$beforeAuditReports"

$patientId = [guid]::NewGuid()
$summaryId = "SUM-REALFLOW-$((Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'))"
$reminderId = "REM-REALFLOW-$((Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'))"
$reportId = "AUDIT-REALFLOW-$((Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'))"

# Synthetic but realistic clinical data. Do not use real patient PHI in CI.
$ehrContext = @"
Bệnh nhân: nữ, 58 tuổi.
Lý do khám: tái khám đái tháo đường type 2 và tăng huyết áp.
Tiền sử: T2DM 8 năm, tăng huyết áp 5 năm, rối loạn lipid máu.
Thuốc hiện tại: Metformin 500mg x 2 lần/ngày, Amlodipine 5mg sáng, Atorvastatin 20mg tối.
Dị ứng: chưa ghi nhận.
Dấu hiệu sinh tồn: HA 138/84 mmHg, mạch 78 lần/phút, BMI 27.1.
Cận lâm sàng gần nhất: HbA1c 7.8%, LDL-C 118 mg/dL, eGFR 82 mL/phút/1.73m2.
Vấn đề: quên thuốc buổi tối 2-3 lần/tuần, cần nhắc thuốc và tái khám sau 30 ngày.
"@

$summaryText = @"
SOAP Note
S: Bệnh nhân tái khám T2DM/tăng huyết áp, thỉnh thoảng quên thuốc buổi tối 2-3 lần/tuần.
O: HA 138/84, BMI 27.1, HbA1c 7.8%, LDL-C 118, eGFR 82. Chưa ghi nhận dị ứng.
A: Kiểm soát đường huyết chưa đạt mục tiêu; tăng huyết áp tương đối ổn; nguy cơ tuân thủ thuốc trung bình.
P: Duy trì Metformin/Amlodipine/Atorvastatin theo đơn hiện tại, thiết lập nhắc thuốc buổi tối, tái khám sau 30 ngày kèm HbA1c và lipid máu. Không thay đổi thuốc nếu chưa được bác sĩ xác nhận.
"@

Write-Host ""
Write-Host "1) Medical Summary Agent persistence..." -ForegroundColor Cyan
$summary = Invoke-Tool "persist_medical_summary" @{
    summary_id = $summaryId
    patient_id = $patientId
    summary_type = "soap"
    audience = "clinician"
    specialty = "Nội tiết"
    source_context = $ehrContext
    summary_text = $summaryText
    model = "seeded-clinical-flow"
    status = "completed"
} $authHeaders
Write-Host "[PASS] medical_summaries saved: summary_id=$($summary.summary_id)" -ForegroundColor Green

Write-Host ""
Write-Host "2) Reminder Agent persistence..." -ForegroundColor Cyan
$reminder = Invoke-Tool "create_reminder_record" @{
    reminder_id = $reminderId
    patient_id = $patientId
    workflow_id = "seed-reminder-$($patientId.ToString('N'))"
    reminder_type = "medication"
    medication_name = "Metformin"
    dosage = "500mg"
    frequency = "twice_daily"
    start_at = (Get-Date).ToUniversalTime().AddHours(1).ToString("O")
    duration_days = 30
    preferred_channel = "zalo"
    adherence_risk_score = 55
    status = "scheduled"
} $authHeaders
Write-Host "[PASS] reminder_records created: reminder_id=$($reminder.reminder_id)" -ForegroundColor Green

$null = Invoke-Tool "update_reminder_status" @{
    reminder_id = $reminderId
    status = "confirmed"
    confirmed_count = 1
    missed_count = 0
    last_confirmed_at = (Get-Date).ToUniversalTime().ToString("O")
} $authHeaders
Write-Host "[PASS] reminder_records updated to confirmed." -ForegroundColor Green

Write-Host ""
Write-Host "3) Audit Report Agent tool chain..." -ForegroundColor Cyan
$periodStart = (Get-Date).ToUniversalTime().AddDays(-30).ToString("O")
$periodEnd = (Get-Date).ToUniversalTime().ToString("O")
$metrics = Invoke-Tool "collect_audit_logs" @{
    report_id = $reportId
    report_type = "compliance"
    period_start = $periodStart
    period_end = $periodEnd
} $authHeaders
Write-Host "[PASS] audit metrics collected." -ForegroundColor Green

$anomalies = Invoke-Tool "detect_audit_anomalies" @{
    metrics_json = ($metrics | ConvertTo-Json -Depth 32 -Compress)
    sensitivity = "medium"
} $authHeaders
Write-Host "[PASS] audit anomalies analyzed: count=$($anomalies.anomaly_count)" -ForegroundColor Green

$narrative = @"
Báo cáo tuân thủ 30 ngày ghi nhận hoạt động truy cập hồ sơ bệnh nhân và xuất dữ liệu PHI ở mức cần theo dõi.
Không phát hiện vi phạm đồng ý điều trị. Một số kiểm tra tuân thủ thất bại cần được rà soát theo quy trình vận hành.
Khuyến nghị: đối soát quyền truy cập theo vai trò, rà soát các phiên coding ICD-10 chưa hoàn tất, và duy trì kiểm tra audit hằng tuần.
"@

$audit = Invoke-Tool "export_audit_report" @{
    report_id = $reportId
    report_type = "compliance"
    period_start = $periodStart
    period_end = $periodEnd
    narrative = $narrative
    metrics_json = ($metrics | ConvertTo-Json -Depth 32 -Compress)
    anomalies_json = ($anomalies | ConvertTo-Json -Depth 32 -Compress)
    format = "json"
} $authHeaders
Write-Host "[PASS] audit_reports saved: report_id=$($audit.report_id) hash=$($audit.integrity_hash)" -ForegroundColor Green

$afterSummaries = Get-TableCount "medical_summaries"
$afterReminders = Get-TableCount "reminder_records"
$afterAuditReports = Get-TableCount "audit_reports"

Write-Host ""
Write-Host "Rows after:  medical_summaries=$afterSummaries reminder_records=$afterReminders audit_reports=$afterAuditReports"

if ($afterSummaries -le $beforeSummaries) { throw "medical_summaries count did not increase." }
if ($afterReminders -le $beforeReminders) { throw "reminder_records count did not increase." }
if ($afterAuditReports -le $beforeAuditReports) { throw "audit_reports count did not increase." }

Write-Host ""
Write-Host "Latest persisted records:" -ForegroundColor Cyan
docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -c 'SELECT "SummaryId", "PatientId", "SummaryType", "Audience", "Status", "CreatedAt" FROM medical_summaries ORDER BY "CreatedAt" DESC LIMIT 1;'
docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -c 'SELECT "ReminderId", "PatientId", "MedicationName", "Frequency", "Status", "ConfirmedCount", "UpdatedAt" FROM reminder_records ORDER BY "UpdatedAt" DESC LIMIT 1;'
docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -c 'SELECT "ReportId", "ReportType", "Format", "Status", "IntegrityHash", "ExportedAt" FROM audit_reports ORDER BY "ExportedAt" DESC LIMIT 1;'

Write-Host ""
Write-Host "Clinical persistence flow result: PASS" -ForegroundColor Cyan
exit 0
