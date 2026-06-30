<#
  Hope.Agent — Reminder Persistence Integration Test

  Purpose:
    - Start medication reminder workflow via API
        - Wait until the workflow is observable
    - Verify reminder_records row count increased in Postgres

  Prerequisites:
    - API is running (default: http://localhost:5080)
    - Temporal worker enabled in API runtime
    - Docker container with Postgres is running

  Usage:
    .\tests\hope-reminder-persistence.ps1
    .\tests\hope-reminder-persistence.ps1 -BaseUrl http://localhost:5080
#>

param(
    [string]$BaseUrl = "http://localhost:5080",
    [string]$ClientId = "doctor-nguyen",
    [string]$Secret = "HopeAgentDev2026!",
    [string]$PostgresContainer = "hope-agent-postgres-1",
    [string]$PostgresUser = "hope",
    [string]$PostgresDb = "hope_agent",
    [int]$StatusPollMaxAttempts = 60,
    [int]$StatusPollDelaySeconds = 2
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
    $json = if ($null -ne $Body) { $Body | ConvertTo-Json -Depth 12 -Compress } else { $null }

    try {
        $res = Invoke-WebRequest -Uri $uri -Method $Method -Headers $Headers -ContentType "application/json" -Body $json
        return [pscustomobject]@{
            StatusCode = [int]$res.StatusCode
            Body = Convert-Body $res.Content
            Raw = $res.Content
        }
    }
    catch {
        $response = $_.Exception.Response
        if ($null -eq $response) { throw }

        $status = [int]$response.StatusCode
        $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
        $raw = $reader.ReadToEnd()
        $reader.Dispose()

        return [pscustomobject]@{
            StatusCode = $status
            Body = Convert-Body $raw
            Raw = $raw
        }
    }
}

function Get-ReminderCount {
    $sql = "SELECT COUNT(*) FROM reminder_records;"
    $countRaw = docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -t -A -c $sql
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to query Postgres container '$PostgresContainer'."
    }

    $value = ($countRaw | Select-Object -First 1).ToString().Trim()
    try {
        return [int]::Parse($value, [System.Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        throw "Invalid reminder count output: '$value'"
    }
}

Write-Host "== Hope Reminder Persistence Integration Test ==" -ForegroundColor Cyan
Write-Host "BaseUrl: $BaseUrl"
Write-Host "PostgresContainer: $PostgresContainer"

$login = Invoke-Api -Method "POST" -Path "/v1/auth/login" -Body @{ clientId = $ClientId; secret = $Secret } -Headers @{}
if ($login.StatusCode -ne 200 -or -not $login.Body.accessToken) {
    Write-Host "[FAIL] Login failed (status=$($login.StatusCode))." -ForegroundColor Red
    Write-Host "Response: $($login.Raw)"
    exit 1
}

$token = [string]$login.Body.accessToken
$authHeaders = @{ Authorization = "Bearer $token" }
Write-Host "[PASS] Login succeeded." -ForegroundColor Green

$beforeCount = Get-ReminderCount
Write-Host "Rows before workflow: $beforeCount"

$patientId = [guid]::NewGuid()
$workflowStart = Invoke-Api -Method "POST" -Path "/v1/workflows/reminders" -Body @{
    patientId = $patientId
    medicationName = "Amlodipine"
    dosage = "5mg"
    frequency = "once_daily"
    durationDays = 14
    preferredChannel = "zalo"
    adherenceRiskScore = 35
} -Headers $authHeaders

if ($workflowStart.StatusCode -ne 202 -or -not $workflowStart.Body.workflowId) {
    Write-Host "[FAIL] Reminder start failed (status=$($workflowStart.StatusCode))." -ForegroundColor Red
    Write-Host "Response: $($workflowStart.Raw)"
    exit 1
}

$workflowId = [string]$workflowStart.Body.workflowId
Write-Host "[PASS] Reminder accepted. workflowId=$workflowId" -ForegroundColor Green

$observedStatus = $null
for ($i = 1; $i -le $StatusPollMaxAttempts; $i++) {
    $statusRes = Invoke-Api -Method "GET" -Path "/v1/workflows/$workflowId" -Body $null -Headers $authHeaders
    if ($statusRes.StatusCode -ne 200 -or -not $statusRes.Body.status) {
        Write-Host "[WARN] Poll attempt $i failed (status=$($statusRes.StatusCode))." -ForegroundColor Yellow
    } else {
        $current = [string]$statusRes.Body.status
        Write-Host ("Poll {0}/{1}: {2}" -f $i, $StatusPollMaxAttempts, $current)

        if ($current -in @("Running", "Scheduled", "Completed", "Failed", "Canceled", "Terminated", "TimedOut")) {
            $observedStatus = $current
            break
        }
    }

    Start-Sleep -Seconds $StatusPollDelaySeconds
}

if (-not $observedStatus) {
    Write-Host "[FAIL] Workflow did not become observable within timeout." -ForegroundColor Red
    exit 1
}

Write-Host "[PASS] Workflow observable with status '$observedStatus'." -ForegroundColor Green

$afterCount = Get-ReminderCount
Write-Host "Rows after workflow:  $afterCount"

if ($afterCount -le $beforeCount) {
    Write-Host "[FAIL] reminder_records count did not increase (before=$beforeCount, after=$afterCount)." -ForegroundColor Red
    exit 1
}

Write-Host "[PASS] reminder_records count increased by $($afterCount - $beforeCount)." -ForegroundColor Green

docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -c 'SELECT "ReminderId", "PatientId", "WorkflowId", "MedicationName", "Frequency", "Status", "CreatedAt" FROM reminder_records ORDER BY "CreatedAt" DESC LIMIT 1;'

Write-Host ""
Write-Host "Integration result: PASS" -ForegroundColor Cyan
exit 0