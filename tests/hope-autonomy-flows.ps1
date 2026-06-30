<#
  Hope.Agent - Controlled Autonomy Smoke Flow

  Seeds realistic patient timeline data, calls /v1/agents/suggestions, and verifies:
    - agent_decisions increased
    - autonomous_actions increased
    - Development low-risk action is queued for auto execution

  Usage:
    .\tests\hope-autonomy-flows.ps1
    .\tests\hope-autonomy-flows.ps1 -BaseUrl http://localhost:5080
#>

param(
    [string]$BaseUrl = "http://localhost:5080",
    [string]$ClientId = "doctor-nguyen",
    [string]$Secret = "HopeAgentDev2026!",
    [string]$PostgresContainer = "hope-agent-postgres-1",
    [string]$PostgresUser = "hope",
    [string]$PostgresDb = "hope_agent",
    [switch]$SkipApiStart
)

$ErrorActionPreference = "Stop"

function Invoke-Psql([string]$Sql) {
    docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -v ON_ERROR_STOP=1 -c $Sql | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Postgres command failed." }
}

function Invoke-PsqlScalar([string]$Sql) {
    $raw = docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -t -A -c $Sql
    if ($LASTEXITCODE -ne 0) { throw "Postgres query failed: $Sql" }
    return ($raw | Select-Object -First 1).ToString().Trim()
}

function ConvertTo-SqlText([string]$Value) {
    return "'" + ($Value -replace "'", "''") + "'"
}

function Ensure-AutonomySchema {
    Write-Host "Ensuring autonomy tables exist..." -ForegroundColor Cyan
    Invoke-Psql @'
CREATE TABLE IF NOT EXISTS agent_decisions (
    "Id" uuid PRIMARY KEY,
    "DecisionId" character varying(64) NOT NULL UNIQUE,
    "UserId" uuid NOT NULL,
    "PatientId" uuid NULL,
    "ConversationId" uuid NULL,
    "Intent" character varying(64) NOT NULL,
    "AgentProfile" character varying(64) NULL,
    "InputSummary" text NOT NULL,
    "MemoryRefsJson" jsonb NULL,
    "EvidenceJson" jsonb NULL,
    "ProposedActionJson" jsonb NULL,
    "RiskLevel" integer NOT NULL,
    "Confidence" double precision NOT NULL,
    "PolicyDecision" integer NOT NULL,
    "DecisionStatus" integer NOT NULL,
    "Reason" character varying(512) NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "CorrelationId" character varying(128) NULL
);
'@
    Invoke-Psql @'
CREATE TABLE IF NOT EXISTS autonomous_actions (
    "Id" uuid PRIMARY KEY,
    "ActionId" character varying(64) NOT NULL UNIQUE,
    "DecisionId" character varying(64) NOT NULL,
    "ToolName" character varying(128) NOT NULL,
    "ArgumentsJson" jsonb NOT NULL,
    "RiskLevel" integer NOT NULL,
    "Confidence" double precision NOT NULL,
    "Status" integer NOT NULL,
    "ScheduledFor" timestamp with time zone NULL,
    "ExecutedAt" timestamp with time zone NULL,
    "ResultJson" jsonb NULL,
    "Error" text NULL,
    "AttemptCount" integer NOT NULL DEFAULT 0,
    "CreatedAt" timestamp with time zone NOT NULL,
    "CorrelationId" character varying(128) NULL
);
'@
    Invoke-Psql @'
CREATE TABLE IF NOT EXISTS autonomy_goals (
    "Id" uuid PRIMARY KEY,
    "GoalId" character varying(64) NOT NULL UNIQUE,
    "PatientId" uuid NULL,
    "UserId" uuid NOT NULL,
    "GoalType" character varying(64) NOT NULL,
    "Description" text NOT NULL,
    "EvidenceJson" jsonb NOT NULL,
    "PriorityScore" double precision NOT NULL,
    "Confidence" double precision NOT NULL,
    "MaxAllowedRisk" integer NOT NULL,
    "Status" integer NOT NULL,
    "DecisionId" character varying(64) NULL,
    "Reason" character varying(512) NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "CompletedAt" timestamp with time zone NULL,
    "CorrelationId" character varying(128) NULL
);
'@
    Invoke-Psql @'
CREATE TABLE IF NOT EXISTS autonomy_reflections (
    "Id" uuid PRIMARY KEY,
    "ReflectionId" character varying(64) NOT NULL UNIQUE,
    "GoalId" character varying(64) NULL,
    "DecisionId" character varying(64) NULL,
    "ActionId" character varying(64) NULL,
    "PatientId" uuid NULL,
    "Succeeded" boolean NOT NULL,
    "Summary" text NOT NULL,
    "LessonsJson" jsonb NOT NULL,
    "ConfidenceDelta" double precision NOT NULL,
    "CorrelationId" character varying(128) NULL,
    "CreatedAt" timestamp with time zone NOT NULL
);
'@
    Invoke-Psql @'
CREATE TABLE IF NOT EXISTS autonomy_learning_facts (
    "Id" uuid PRIMARY KEY,
    "FactId" character varying(64) NOT NULL UNIQUE,
    "Kind" integer NOT NULL,
    "Key" character varying(256) NOT NULL,
    "ValueJson" jsonb NOT NULL,
    "Confidence" double precision NOT NULL,
    "Source" character varying(128) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "LastObservedAt" timestamp with time zone NULL,
    UNIQUE ("Kind", "Key")
);
'@
    Invoke-Psql @'
CREATE TABLE IF NOT EXISTS autonomy_eval_gate_runs (
    "Id" uuid PRIMARY KEY,
    "GateId" character varying(64) NOT NULL UNIQUE,
    "SuiteName" character varying(128) NOT NULL,
    "Passed" boolean NOT NULL,
    "PassRate" double precision NOT NULL,
    "MetricsJson" jsonb NOT NULL,
    "Reason" character varying(512) NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "CorrelationId" character varying(128) NULL
);
'@
    Invoke-Psql @'
CREATE TABLE IF NOT EXISTS autonomy_drift_signals (
    "Id" uuid PRIMARY KEY,
    "SignalId" character varying(64) NOT NULL UNIQUE,
    "SignalType" character varying(128) NOT NULL,
    "Severity" integer NOT NULL,
    "Score" double precision NOT NULL,
    "BaselineJson" jsonb NOT NULL,
    "CurrentJson" jsonb NOT NULL,
    "Status" integer NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "CorrelationId" character varying(128) NULL
);
'@
    Invoke-Psql @'
CREATE TABLE IF NOT EXISTS autonomy_compensations (
    "Id" uuid PRIMARY KEY,
    "CompensationId" character varying(64) NOT NULL UNIQUE,
    "ActionId" character varying(64) NOT NULL,
    "ToolName" character varying(128) NOT NULL,
    "ArgumentsJson" jsonb NOT NULL,
    "Status" integer NOT NULL,
    "ResultJson" jsonb NULL,
    "Error" text NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "ExecutedAt" timestamp with time zone NULL,
    "CorrelationId" character varying(128) NULL
);
'@
    Invoke-Psql @'
CREATE TABLE IF NOT EXISTS autonomy_reviews (
    "Id" uuid PRIMARY KEY,
    "ReviewId" character varying(64) NOT NULL UNIQUE,
    "DecisionId" character varying(64) NOT NULL,
    "ReviewerProfile" character varying(64) NOT NULL,
    "Verdict" integer NOT NULL,
    "Confidence" double precision NOT NULL,
    "Notes" text NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "CorrelationId" character varying(128) NULL
);
'@
    Invoke-Psql 'CREATE INDEX IF NOT EXISTS "IX_agent_decisions_PatientId_CreatedAt" ON agent_decisions ("PatientId", "CreatedAt");'
    Invoke-Psql 'CREATE INDEX IF NOT EXISTS "IX_agent_decisions_UserId_CreatedAt" ON agent_decisions ("UserId", "CreatedAt");'
    Invoke-Psql 'CREATE INDEX IF NOT EXISTS "IX_agent_decisions_DecisionStatus_CreatedAt" ON agent_decisions ("DecisionStatus", "CreatedAt");'
    Invoke-Psql 'CREATE INDEX IF NOT EXISTS "IX_autonomous_actions_DecisionId" ON autonomous_actions ("DecisionId");'
    Invoke-Psql 'CREATE INDEX IF NOT EXISTS "IX_autonomous_actions_Status_ScheduledFor" ON autonomous_actions ("Status", "ScheduledFor");'
    Invoke-Psql 'CREATE INDEX IF NOT EXISTS "IX_autonomy_goals_Status_CreatedAt" ON autonomy_goals ("Status", "CreatedAt");'
    Invoke-Psql 'CREATE INDEX IF NOT EXISTS "IX_autonomy_reflections_ActionId" ON autonomy_reflections ("ActionId");'
    Invoke-Psql 'CREATE INDEX IF NOT EXISTS "IX_autonomy_learning_facts_LastObservedAt" ON autonomy_learning_facts ("LastObservedAt");'
    Invoke-Psql 'CREATE INDEX IF NOT EXISTS "IX_autonomy_eval_gate_runs_Passed_CreatedAt" ON autonomy_eval_gate_runs ("Passed", "CreatedAt");'
    Invoke-Psql 'CREATE INDEX IF NOT EXISTS "IX_autonomy_drift_signals_Severity_CreatedAt" ON autonomy_drift_signals ("Severity", "CreatedAt");'
    Invoke-Psql 'CREATE INDEX IF NOT EXISTS "IX_autonomy_compensations_Status_CreatedAt" ON autonomy_compensations ("Status", "CreatedAt");'
    Invoke-Psql 'CREATE INDEX IF NOT EXISTS "IX_autonomy_reviews_DecisionId" ON autonomy_reviews ("DecisionId");'
}

function Ensure-ClinicalSeedSchema {
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
CREATE TABLE IF NOT EXISTS agent_memories (
    "Id" uuid PRIMARY KEY,
    "UserId" uuid NOT NULL,
    "ConversationId" uuid NULL,
    "Kind" integer NOT NULL,
    "Content" text NOT NULL,
    "Source" text NULL,
    "Importance" real NOT NULL,
    "Metadata" jsonb NOT NULL DEFAULT '{}',
    "CreatedAt" timestamp with time zone NOT NULL
);
'@
}

function Wait-ApiReady {
    param([int]$Seconds = 30)
    for ($i = 0; $i -lt $Seconds; $i++) {
        try {
            Invoke-WebRequest -Uri "$BaseUrl/v1/auth/login" -Method Options -UseBasicParsing -TimeoutSec 2 | Out-Null
            return
        } catch {
            if ($_.Exception.Response) { return }
            Start-Sleep -Seconds 1
        }
    }
    throw "API did not become ready at $BaseUrl."
}

Write-Host "== Hope Controlled Autonomy Flow ==" -ForegroundColor Cyan
Ensure-AutonomySchema
Ensure-ClinicalSeedSchema

$patientId = [guid]::NewGuid().ToString()
$userId = [guid]::NewGuid().ToString()
$reminderId = "REM-AUTO-$((Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'))"
$now = (Get-Date).ToUniversalTime().ToString("O")

Invoke-Psql @"
INSERT INTO agent_memories (
    "Id", "UserId", "ConversationId", "Kind", "Content", "Source", "Importance", "Metadata", "CreatedAt"
) VALUES (
    '$([guid]::NewGuid())', '$patientId', NULL, 3,
    $(ConvertTo-SqlText "Bệnh nhân T2DM đang dùng Metformin, hay quên thuốc buổi tối và cần tái khám sau 30 ngày."),
    'autonomy-smoke-seed', 0.91, '{}'::jsonb, '$now'
);
"@

Invoke-Psql @"
INSERT INTO reminder_records (
    "Id", "ReminderId", "PatientId", "UserId", "WorkflowId", "ReminderType", "MedicationName", "Dosage",
    "Frequency", "StartAt", "DurationDays", "PreferredChannel", "AdherenceRiskScore", "Status",
    "ConfirmedCount", "MissedCount", "CreatedAt", "UpdatedAt", "CorrelationId"
) VALUES (
    '$([guid]::NewGuid())', '$reminderId', '$patientId', '$userId', 'autonomy-smoke-workflow',
    'medication', 'Metformin', '500mg', 'twice_daily', '$now', 30, 'zalo', 55, 'scheduled',
    0, 0, '$now', '$now', 'autonomy-smoke'
);
"@

$beforeDecisions = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM agent_decisions;')
$beforeActions = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM autonomous_actions;')
$beforeGoals = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM autonomy_goals;')
$beforeFacts = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM autonomy_learning_facts;')
$beforeGates = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM autonomy_eval_gate_runs;')
$beforeDrift = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM autonomy_drift_signals;')

$apiProcess = $null
try {
    if (-not $SkipApiStart) {
        $dotnet = "C:\Program Files\dotnet\dotnet.exe"
        $out = "D:\Pr.Project\Hope.Agent\artifacts\autonomy-api.out.log"
        $err = "D:\Pr.Project\Hope.Agent\artifacts\autonomy-api.err.log"
        Remove-Item $out,$err -ErrorAction SilentlyContinue
        $env:ASPNETCORE_ENVIRONMENT = "Development"
        $apiProcess = Start-Process -FilePath $dotnet `
            -ArgumentList @('bin\Debug\net9.0\Hope.Agent.Api.dll','--urls',$BaseUrl) `
            -WorkingDirectory 'D:\Pr.Project\Hope.Agent\src\Hope.Agent.Api' `
            -RedirectStandardOutput $out `
            -RedirectStandardError $err `
            -PassThru `
            -WindowStyle Hidden
    }

    Wait-ApiReady
    $login = Invoke-RestMethod -Uri "$BaseUrl/v1/auth/login" -Method Post -ContentType "application/json" `
        -Body (@{ clientId = $ClientId; secret = $Secret } | ConvertTo-Json -Compress)
    $headers = @{ Authorization = "Bearer $($login.accessToken)" }
    $suggestion = Invoke-RestMethod -Uri "$BaseUrl/v1/agents/suggestions" -Method Post -ContentType "application/json" -Headers $headers `
        -Body (@{ patientId = $patientId; goal = "Đánh giá dữ liệu cũ và gợi ý follow-up tự động nếu an toàn." } | ConvertTo-Json -Compress)

    $dailyReview = Invoke-RestMethod -Uri "$BaseUrl/v1/autonomy/daily-review/run" -Method Post -ContentType "application/json" -Headers $headers -Body '{}'
    $evalGate = Invoke-RestMethod -Uri "$BaseUrl/v1/autonomy/level5/eval-gate/run" -Method Post -ContentType "application/json" -Headers $headers -Body '{"suiteName":"smoke_level5"}'
    $drift = Invoke-RestMethod -Uri "$BaseUrl/v1/autonomy/level5/drift/detect" -Method Post -ContentType "application/json" -Headers $headers -Body '{}'
    $readiness = Invoke-RestMethod -Uri "$BaseUrl/v1/autonomy/level5/readiness" -Method Get -Headers $headers
    $agiLike = Invoke-RestMethod -Uri "$BaseUrl/v1/autonomy/agi-like/run" -Method Post -ContentType "application/json" -Headers $headers -Body '{}'

    Start-Sleep -Seconds 40
    $afterDecisions = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM agent_decisions;')
    $afterActions = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM autonomous_actions;')
    $afterGoals = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM autonomy_goals;')
    $afterFacts = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM autonomy_learning_facts;')
    $afterGates = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM autonomy_eval_gate_runs;')
    $afterDrift = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM autonomy_drift_signals;')
    $newSucceededActions = [int](Invoke-PsqlScalar "SELECT COUNT(*) FROM autonomous_actions WHERE ""CreatedAt"" >= '$now' AND ""Status"" = 3;")

    Write-Host "Rows before: agent_decisions=$beforeDecisions autonomous_actions=$beforeActions"
    Write-Host "Rows after:  agent_decisions=$afterDecisions autonomous_actions=$afterActions"
    Write-Host "AGI-like rows: autonomy_goals $beforeGoals -> $afterGoals; autonomy_learning_facts $beforeFacts -> $afterFacts"
    Write-Host "Level5 control rows: eval_gate_runs $beforeGates -> $afterGates; drift_signals $beforeDrift -> $afterDrift"
    if ($afterDecisions -le $beforeDecisions) { throw "agent_decisions count did not increase." }
    if ($afterActions -le $beforeActions) { throw "autonomous_actions count did not increase." }
    if ($afterGoals -le $beforeGoals) { throw "autonomy_goals count did not increase." }
    if ($afterGates -le $beforeGates) { throw "autonomy_eval_gate_runs count did not increase." }
    if ($afterDrift -le $beforeDrift) { throw "autonomy_drift_signals count did not increase." }
    if ($newSucceededActions -lt 1) { throw "no new low-risk autonomous action succeeded." }
    if ($dailyReview.reviewed -lt 1) { throw "daily autonomy review did not review any patient." }
    if ($agiLike.goalsCreated -lt 1) { throw "AGI-like loop did not create any goal." }

    Write-Host ""
    Write-Host "Suggestion result:" -ForegroundColor Cyan
    $suggestion | ConvertTo-Json -Depth 16
    Write-Host "Daily review result:" -ForegroundColor Cyan
    $dailyReview | ConvertTo-Json -Depth 8
    Write-Host "AGI-like result:" -ForegroundColor Cyan
    $agiLike | ConvertTo-Json -Depth 8
    Write-Host "Level 5 eval gate:" -ForegroundColor Cyan
    $evalGate | ConvertTo-Json -Depth 8
    Write-Host "Level 5 drift:" -ForegroundColor Cyan
    $drift | ConvertTo-Json -Depth 8
    Write-Host "Level 5 readiness:" -ForegroundColor Cyan
    $readiness | ConvertTo-Json -Depth 8

    Write-Host ""
    Write-Host "Latest autonomy records:" -ForegroundColor Cyan
    docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -c 'SELECT "DecisionId", "Intent", "RiskLevel", "Confidence", "PolicyDecision", "DecisionStatus", "Reason", "CreatedAt" FROM agent_decisions ORDER BY "CreatedAt" DESC LIMIT 3;'
    docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -c 'SELECT "ActionId", "DecisionId", "ToolName", "RiskLevel", "Confidence", "Status", "AttemptCount", "ExecutedAt", "Error" FROM autonomous_actions ORDER BY "CreatedAt" DESC LIMIT 3;'
    docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -c 'SELECT "GoalId", "GoalType", "Confidence", "Status", "DecisionId", "Reason", "CreatedAt" FROM autonomy_goals ORDER BY "CreatedAt" DESC LIMIT 3;'
    docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -c 'SELECT "FactId", "Kind", "Key", "Confidence", "Source", "LastObservedAt" FROM autonomy_learning_facts ORDER BY "LastObservedAt" DESC NULLS LAST LIMIT 3;'
    docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -c 'SELECT "GateId", "SuiteName", "Passed", "PassRate", "Reason", "CreatedAt" FROM autonomy_eval_gate_runs ORDER BY "CreatedAt" DESC LIMIT 3;'
    docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -c 'SELECT "SignalId", "SignalType", "Severity", "Score", "Status", "CreatedAt" FROM autonomy_drift_signals ORDER BY "CreatedAt" DESC LIMIT 3;'

    Write-Host ""
    Write-Host "Controlled autonomy flow result: PASS" -ForegroundColor Cyan
}
finally {
    if ($apiProcess -and -not $apiProcess.HasExited) {
        Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue
    }
}
