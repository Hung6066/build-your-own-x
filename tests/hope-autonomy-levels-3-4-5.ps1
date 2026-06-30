<#
  Hope.Agent - Autonomy Levels 3/4/5 Integration Test

  Verifies:
    - Level 3: medium-risk suggestion is queued as Pending / RequiresApproval.
    - Level 4: low-risk suggestion is auto-executed by the worker.
    - Level 5: eval gate, drift detection, readiness, AGI-like goals/reflections persist.

  Usage:
    .\tests\hope-autonomy-levels-3-4-5.ps1
    .\tests\hope-autonomy-levels-3-4-5.ps1 -SkipApiStart
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

function Ensure-MinSchema {
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

function Seed-Memory([string]$PatientId, [string]$Text, [string]$Source) {
    $now = (Get-Date).ToUniversalTime().ToString("O")
    Invoke-Psql @"
INSERT INTO agent_memories (
    "Id", "UserId", "ConversationId", "Kind", "Content", "Source", "Importance", "Metadata", "CreatedAt"
) VALUES (
    '$([guid]::NewGuid())', '$PatientId', NULL, 3, $(ConvertTo-SqlText $Text), '$Source', 0.91, '{}'::jsonb, '$now'
);
"@
}

function Seed-Reminder([string]$PatientId, [string]$UserId, [string]$ReminderId) {
    $now = (Get-Date).ToUniversalTime().ToString("O")
    Invoke-Psql @"
INSERT INTO reminder_records (
    "Id", "ReminderId", "PatientId", "UserId", "WorkflowId", "ReminderType", "MedicationName", "Dosage",
    "Frequency", "StartAt", "DurationDays", "PreferredChannel", "AdherenceRiskScore", "Status",
    "ConfirmedCount", "MissedCount", "CreatedAt", "UpdatedAt", "CorrelationId"
) VALUES (
    '$([guid]::NewGuid())', '$ReminderId', '$PatientId', '$UserId', 'levels-345-workflow',
    'medication', 'Metformin', '500mg', 'twice_daily', '$now', 30, 'zalo', 60, 'scheduled',
    0, 0, '$now', '$now', 'levels-345'
);
"@
}

Write-Host "== Hope Autonomy Levels 3/4/5 Integration Test ==" -ForegroundColor Cyan
Ensure-MinSchema

$apiProcess = $null
try {
    if (-not $SkipApiStart) {
        $dotnet = "C:\Program Files\dotnet\dotnet.exe"
        $out = "D:\Pr.Project\Hope.Agent\artifacts\levels-345-api.out.log"
        $err = "D:\Pr.Project\Hope.Agent\artifacts\levels-345-api.err.log"
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
    if ($login.StatusCode -ne 200 -or -not $login.Body.accessToken) { throw "Login failed: $($login.Raw)" }
    $headers = @{ Authorization = "Bearer $($login.Body.accessToken)" }
    Write-Host "[PASS] Login." -ForegroundColor Green

    $beforeSucceeded = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM autonomous_actions WHERE "Status" = 3;')
    $beforeGoals = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM autonomy_goals;')
    $beforeReflections = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM autonomy_reflections;')
    $beforeGates = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM autonomy_eval_gate_runs;')
    $beforeDrift = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM autonomy_drift_signals;')

    # Level 3: no existing reminder -> create_reminder_record is Medium and must stay Pending / RequiresApproval in Development.
    $patientL3 = [guid]::NewGuid().ToString()
    Seed-Memory $patientL3 "Bệnh nhân T2DM dùng Metformin, chưa có reminder trong hồ sơ, cần tạo reminder draft." "levels-345-l3"
    $l3 = Invoke-Api -Method "POST" -Path "/v1/agents/suggestions" -Body @{
        patientId = $patientL3
        goal = "Level 3 test: suggest reminder draft, require approval for medium-risk write."
    } -Headers $headers
    if ($l3.StatusCode -ne 200) { throw "Level 3 suggestion failed: $($l3.Raw)" }
    $l3DecisionId = [string]$l3.Body.decisionId
    $l3Status = Invoke-PsqlScalar "SELECT ""DecisionStatus"" FROM agent_decisions WHERE ""DecisionId"" = '$l3DecisionId';"
    $l3ActionStatus = Invoke-PsqlScalar "SELECT ""Status"" FROM autonomous_actions WHERE ""DecisionId"" = '$l3DecisionId' ORDER BY ""CreatedAt"" DESC LIMIT 1;"
    if ($l3Status -ne "3" -or $l3ActionStatus -ne "0") {
        throw "Level 3 failed. Expected decision RequiresApproval(3) and action Pending(0), got decision=$l3Status action=$l3ActionStatus."
    }
    Write-Host "[PASS] Level 3: medium-risk action is pending approval." -ForegroundColor Green

    # Level 4: existing reminder -> update_reminder_status is Low and auto-executes.
    $patientL4 = [guid]::NewGuid().ToString()
    $userL4 = [guid]::NewGuid().ToString()
    $reminderId = "REM-L4-$((Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'))"
    Seed-Memory $patientL4 "Bệnh nhân T2DM dùng Metformin, hay quên thuốc buổi tối, có reminder cần follow-up." "levels-345-l4"
    Seed-Reminder $patientL4 $userL4 $reminderId
    $l4 = Invoke-Api -Method "POST" -Path "/v1/agents/suggestions" -Body @{
        patientId = $patientL4
        goal = "Level 4 test: auto-execute low-risk reminder status update."
    } -Headers $headers
    if ($l4.StatusCode -ne 200) { throw "Level 4 suggestion failed: $($l4.Raw)" }

    Start-Sleep -Seconds 40
    $afterSucceeded = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM autonomous_actions WHERE "Status" = 3;')
    if ($afterSucceeded -le $beforeSucceeded) {
        throw "Level 4 failed. Succeeded action count did not increase."
    }
    Write-Host "[PASS] Level 4: low-risk action auto-executed." -ForegroundColor Green

    # Level 5: control plane and AGI-like loop.
    $gate = Invoke-Api -Method "POST" -Path "/v1/autonomy/level5/eval-gate/run" -Body @{ suiteName = "levels_345" } -Headers $headers
    if ($gate.StatusCode -ne 200 -or -not $gate.Body.passed) { throw "Level 5 eval gate failed: $($gate.Raw)" }

    $drift = Invoke-Api -Method "POST" -Path "/v1/autonomy/level5/drift/detect" -Body @{} -Headers $headers
    if ($drift.StatusCode -ne 200 -or [int]$drift.Body.severity -gt 1) { throw "Level 5 drift failed: $($drift.Raw)" }

    $readiness = Invoke-Api -Method "GET" -Path "/v1/autonomy/level5/readiness" -Body $null -Headers $headers
    if ($readiness.StatusCode -ne 200 -or -not $readiness.Body.ready) { throw "Level 5 readiness failed: $($readiness.Raw)" }

    $agi = Invoke-Api -Method "POST" -Path "/v1/autonomy/agi-like/run" -Body @{} -Headers $headers
    if ($agi.StatusCode -ne 200 -or [int]$agi.Body.goalsCreated -lt 1) { throw "Level 5 AGI-like failed: $($agi.Raw)" }
    Start-Sleep -Seconds 35
    $reflect = Invoke-Api -Method "POST" -Path "/v1/autonomy/agi-like/run" -Body @{} -Headers $headers
    if ($reflect.StatusCode -ne 200) { throw "Level 5 reflection failed: $($reflect.Raw)" }

    $afterGoals = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM autonomy_goals;')
    $afterReflections = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM autonomy_reflections;')
    $afterGates = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM autonomy_eval_gate_runs;')
    $afterDrift = [int](Invoke-PsqlScalar 'SELECT COUNT(*) FROM autonomy_drift_signals;')
    if ($afterGoals -le $beforeGoals) { throw "Level 5 failed. autonomy_goals did not increase." }
    if ($afterReflections -le $beforeReflections) { throw "Level 5 failed. autonomy_reflections did not increase." }
    if ($afterGates -le $beforeGates) { throw "Level 5 failed. eval gates did not increase." }
    if ($afterDrift -le $beforeDrift) { throw "Level 5 failed. drift signals did not increase." }
    Write-Host "[PASS] Level 5: eval, drift, readiness, goals and reflections persisted." -ForegroundColor Green

    Write-Host ""
    Write-Host "Level 3 decisionId: $l3DecisionId"
    Write-Host "Level 4 decisionId: $($l4.Body.decisionId)"
    Write-Host "Level 5 gateId: $($gate.Body.gateId), driftSignalId: $($drift.Body.signalId)"
    Write-Host ""
    Write-Host "Autonomy levels 3/4/5 result: PASS" -ForegroundColor Cyan
    exit 0
}
finally {
    if ($apiProcess -and -not $apiProcess.HasExited) {
        Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue
    }
}
