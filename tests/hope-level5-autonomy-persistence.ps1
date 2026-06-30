<#
  Hope.Agent — Level 5 Autonomy Persistence Integration Test

  Purpose:
    - Seed realistic patient timeline data
    - Exercise AGI-like goal creation, eval gate, drift detection, readiness, action execution, reflection/learning
    - Verify new Level 5 persistence tables increase in Postgres

  Usage:
    .\tests\hope-level5-autonomy-persistence.ps1
    .\tests\hope-level5-autonomy-persistence.ps1 -BaseUrl http://localhost:5080 -SkipApiStart
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

function Convert-Body([string]$content) {
    if ([string]::IsNullOrWhiteSpace($content)) { return $null }
    try { return ($content | ConvertFrom-Json) } catch { return $content }
}

function Invoke-Api([string]$Method, [string]$Path, $Body, [hashtable]$Headers) {
    $uri = "$BaseUrl$Path"
    $json = if ($null -ne $Body) { $Body | ConvertTo-Json -Depth 20 -Compress } else { $null }
    try {
        $res = Invoke-WebRequest -Uri $uri -Method $Method -Headers $Headers -ContentType "application/json" -Body $json
        return [pscustomobject]@{ StatusCode = [int]$res.StatusCode; Body = Convert-Body $res.Content; Raw = $res.Content }
    }
    catch {
        $response = $_.Exception.Response
        if ($null -eq $response) { throw }
        $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
        $raw = $reader.ReadToEnd()
        $reader.Dispose()
        return [pscustomobject]@{ StatusCode = [int]$response.StatusCode; Body = Convert-Body $raw; Raw = $raw }
    }
}

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

function Get-Count([string]$Table) {
    return [int](Invoke-PsqlScalar "SELECT COUNT(*) FROM $Table;")
}

function Ensure-Level5Schema {
    Write-Host "Ensuring Level 5 autonomy tables exist..." -ForegroundColor Cyan
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

function Wait-ApiReady([int]$Seconds = 30) {
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

Write-Host "== Hope Level 5 Autonomy Persistence Integration Test ==" -ForegroundColor Cyan
Write-Host "BaseUrl: $BaseUrl"
Write-Host "PostgresContainer: $PostgresContainer"
Ensure-Level5Schema

$patientId = [guid]::NewGuid().ToString()
$userId = [guid]::NewGuid().ToString()
$reminderId = "REM-L5-$((Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'))"
$now = (Get-Date).ToUniversalTime().ToString("O")

Invoke-Psql @"
INSERT INTO agent_memories (
    "Id", "UserId", "ConversationId", "Kind", "Content", "Source", "Importance", "Metadata", "CreatedAt"
) VALUES (
    '$([guid]::NewGuid())', '$patientId', NULL, 3,
    $(ConvertTo-SqlText "Bệnh nhân T2DM đang dùng Metformin, hay quên thuốc buổi tối, cần theo dõi tái khám sau 30 ngày."),
    'level5-persistence-seed', 0.93, '{}'::jsonb, '$now'
);
"@

Invoke-Psql @"
INSERT INTO reminder_records (
    "Id", "ReminderId", "PatientId", "UserId", "WorkflowId", "ReminderType", "MedicationName", "Dosage",
    "Frequency", "StartAt", "DurationDays", "PreferredChannel", "AdherenceRiskScore", "Status",
    "ConfirmedCount", "MissedCount", "CreatedAt", "UpdatedAt", "CorrelationId"
) VALUES (
    '$([guid]::NewGuid())', '$reminderId', '$patientId', '$userId', 'level5-persistence-workflow',
    'medication', 'Metformin', '500mg', 'twice_daily', '$now', 30, 'zalo', 61, 'scheduled',
    0, 0, '$now', '$now', 'level5-persistence'
);
"@

$before = [ordered]@{
    decisions = Get-Count "agent_decisions"
    actions = Get-Count "autonomous_actions"
    goals = Get-Count "autonomy_goals"
    reflections = Get-Count "autonomy_reflections"
    learningFacts = Get-Count "autonomy_learning_facts"
    evalGates = Get-Count "autonomy_eval_gate_runs"
    driftSignals = Get-Count "autonomy_drift_signals"
}

$apiProcess = $null
try {
    if (-not $SkipApiStart) {
        $dotnet = "C:\Program Files\dotnet\dotnet.exe"
        $out = "D:\Pr.Project\Hope.Agent\artifacts\level5-autonomy-api.out.log"
        $err = "D:\Pr.Project\Hope.Agent\artifacts\level5-autonomy-api.err.log"
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
    $login = Invoke-Api -Method "POST" -Path "/v1/auth/login" -Body @{ clientId = $ClientId; secret = $Secret } -Headers @{}
    if ($login.StatusCode -ne 200 -or -not $login.Body.accessToken) {
        throw "Login failed: $($login.Raw)"
    }
    $headers = @{ Authorization = "Bearer $($login.Body.accessToken)" }
    Write-Host "[PASS] Login succeeded." -ForegroundColor Green

    $suggestion = Invoke-Api -Method "POST" -Path "/v1/agents/suggestions" -Body @{
        patientId = $patientId
        goal = "Level 5 persistence test: create safe follow-up suggestion from old patient data."
    } -Headers $headers
    if ($suggestion.StatusCode -ne 200 -or -not $suggestion.Body.decisionId) { throw "Suggestion failed: $($suggestion.Raw)" }

    $evalGate = Invoke-Api -Method "POST" -Path "/v1/autonomy/level5/eval-gate/run" -Body @{ suiteName = "level5_persistence" } -Headers $headers
    if ($evalGate.StatusCode -ne 200 -or -not $evalGate.Body.passed) { throw "Eval gate failed: $($evalGate.Raw)" }

    $drift = Invoke-Api -Method "POST" -Path "/v1/autonomy/level5/drift/detect" -Body @{} -Headers $headers
    if ($drift.StatusCode -ne 200 -or [int]$drift.Body.severity -gt 1) { throw "Drift detection failed/warning too high: $($drift.Raw)" }

    $readiness = Invoke-Api -Method "GET" -Path "/v1/autonomy/level5/readiness" -Body $null -Headers $headers
    if ($readiness.StatusCode -ne 200 -or -not $readiness.Body.ready) { throw "Level 5 readiness failed: $($readiness.Raw)" }

    $agiLike = Invoke-Api -Method "POST" -Path "/v1/autonomy/agi-like/run" -Body @{} -Headers $headers
    if ($agiLike.StatusCode -ne 200 -or [int]$agiLike.Body.goalsCreated -lt 1) { throw "AGI-like run failed: $($agiLike.Raw)" }

    Start-Sleep -Seconds 40

    $reflect = Invoke-Api -Method "POST" -Path "/v1/autonomy/agi-like/run" -Body @{} -Headers $headers
    if ($reflect.StatusCode -ne 200) { throw "AGI-like reflection run failed: $($reflect.Raw)" }

    $after = [ordered]@{
        decisions = Get-Count "agent_decisions"
        actions = Get-Count "autonomous_actions"
        goals = Get-Count "autonomy_goals"
        reflections = Get-Count "autonomy_reflections"
        learningFacts = Get-Count "autonomy_learning_facts"
        evalGates = Get-Count "autonomy_eval_gate_runs"
        driftSignals = Get-Count "autonomy_drift_signals"
    }

    Write-Host "Rows before:" ($before | ConvertTo-Json -Compress)
    Write-Host "Rows after: " ($after | ConvertTo-Json -Compress)

    if ($after.decisions -le $before.decisions) { throw "agent_decisions count did not increase." }
    if ($after.actions -le $before.actions) { throw "autonomous_actions count did not increase." }
    if ($after.goals -le $before.goals) { throw "autonomy_goals count did not increase." }
    if ($after.reflections -le $before.reflections) { throw "autonomy_reflections count did not increase." }
    if ($after.evalGates -le $before.evalGates) { throw "autonomy_eval_gate_runs count did not increase." }
    if ($after.driftSignals -le $before.driftSignals) { throw "autonomy_drift_signals count did not increase." }

    $newSucceeded = [int](Invoke-PsqlScalar "SELECT COUNT(*) FROM autonomous_actions WHERE ""CreatedAt"" >= '$now' AND ""Status"" = 3;")
    if ($newSucceeded -lt 1) { throw "No new autonomous action succeeded." }

    Write-Host "[PASS] New features persisted and executed." -ForegroundColor Green
    Write-Host "Suggestion:" -ForegroundColor Cyan
    $suggestion.Body | ConvertTo-Json -Depth 12
    Write-Host "Eval gate:" -ForegroundColor Cyan
    $evalGate.Body | ConvertTo-Json -Depth 12
    Write-Host "Drift:" -ForegroundColor Cyan
    $drift.Body | ConvertTo-Json -Depth 12
    Write-Host "Readiness:" -ForegroundColor Cyan
    $readiness.Body | ConvertTo-Json -Depth 12
    Write-Host "AGI-like first run:" -ForegroundColor Cyan
    $agiLike.Body | ConvertTo-Json -Depth 12
    Write-Host "AGI-like reflection run:" -ForegroundColor Cyan
    $reflect.Body | ConvertTo-Json -Depth 12

    docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -c 'SELECT "GateId", "SuiteName", "Passed", "PassRate", "Reason", "CreatedAt" FROM autonomy_eval_gate_runs ORDER BY "CreatedAt" DESC LIMIT 3;'
    docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -c 'SELECT "SignalId", "SignalType", "Severity", "Score", "Status", "CreatedAt" FROM autonomy_drift_signals ORDER BY "CreatedAt" DESC LIMIT 3;'
    docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -c 'SELECT "ReflectionId", "ActionId", "Succeeded", "ConfidenceDelta", "CreatedAt" FROM autonomy_reflections ORDER BY "CreatedAt" DESC LIMIT 3;'

    Write-Host ""
    Write-Host "Integration result: PASS" -ForegroundColor Cyan
    exit 0
}
finally {
    if ($apiProcess -and -not $apiProcess.HasExited) {
        Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue
    }
}
