<#
  Hope.Agent — Appointment Scheduling Persistence Integration Test

  Purpose:
    - Start appointment scheduling workflow via API
    - Wait until workflow reaches Completed
    - Verify appointment_bookings row count increased in Postgres

  Prerequisites:
    - API is running (default: http://localhost:5080)
    - Temporal worker enabled in API runtime
    - Docker container with Postgres is running

  Usage:
    .\tests\hope-appointment-persistence.ps1
    .\tests\hope-appointment-persistence.ps1 -BaseUrl http://localhost:5080
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

function Get-BookingCount {
    $sql = "SELECT COUNT(*) FROM appointment_bookings;"
    $countRaw = docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -t -A -c $sql
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to query Postgres container '$PostgresContainer'."
    }

    $value = ($countRaw | Select-Object -First 1).ToString().Trim()
    try {
        return [int]::Parse($value, [System.Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        throw "Invalid booking count output: '$value'"
    }
}

Write-Host "== Hope Appointment Persistence Integration Test ==" -ForegroundColor Cyan
Write-Host "BaseUrl: $BaseUrl"
Write-Host "PostgresContainer: $PostgresContainer"

# 1) Login
$login = Invoke-Api -Method "POST" -Path "/v1/auth/login" -Body @{ clientId = $ClientId; secret = $Secret } -Headers @{}
if ($login.StatusCode -ne 200 -or -not $login.Body.accessToken) {
    Write-Host "[FAIL] Login failed (status=$($login.StatusCode))." -ForegroundColor Red
    Write-Host "Response: $($login.Raw)"
    exit 1
}

$token = [string]$login.Body.accessToken
$authHeaders = @{ Authorization = "Bearer $token" }
Write-Host "[PASS] Login succeeded." -ForegroundColor Green

# 2) Count rows before workflow
$beforeCount = Get-BookingCount
Write-Host "Rows before workflow: $beforeCount"

# 3) Start scheduling workflow
$patientId = [guid]::NewGuid()
$workflowStart = Invoke-Api -Method "POST" -Path "/v1/workflows/scheduling" -Body @{
    patientId = $patientId
    chiefComplaint = "chest pain"
    urgency = "urgent"
    insuranceCardNumber = "HS4010111222333"
} -Headers $authHeaders

if ($workflowStart.StatusCode -ne 202 -or -not $workflowStart.Body.workflowId) {
    Write-Host "[FAIL] Scheduling start failed (status=$($workflowStart.StatusCode))." -ForegroundColor Red
    Write-Host "Response: $($workflowStart.Raw)"
    exit 1
}

$workflowId = [string]$workflowStart.Body.workflowId
Write-Host "[PASS] Scheduling accepted. workflowId=$workflowId" -ForegroundColor Green

# 4) Poll workflow status until completed/failed
$terminalStatus = $null
for ($i = 1; $i -le $StatusPollMaxAttempts; $i++) {
    $statusRes = Invoke-Api -Method "GET" -Path "/v1/workflows/$workflowId" -Body $null -Headers $authHeaders
    if ($statusRes.StatusCode -ne 200 -or -not $statusRes.Body.status) {
        Write-Host "[WARN] Poll attempt $i failed (status=$($statusRes.StatusCode))." -ForegroundColor Yellow
    } else {
        $current = [string]$statusRes.Body.status
        Write-Host ("Poll {0}/{1}: {2}" -f $i, $StatusPollMaxAttempts, $current)

        if ($current -in @("Completed", "Failed", "Canceled", "Terminated", "TimedOut")) {
            $terminalStatus = $current
            break
        }
    }

    Start-Sleep -Seconds $StatusPollDelaySeconds
}

if (-not $terminalStatus) {
    Write-Host "[FAIL] Workflow did not reach terminal status within timeout." -ForegroundColor Red
    exit 1
}

if ($terminalStatus -ne "Completed") {
    Write-Host "[FAIL] Workflow terminal status is '$terminalStatus' (expected 'Completed')." -ForegroundColor Red
    exit 1
}

Write-Host "[PASS] Workflow completed." -ForegroundColor Green

# 5) Verify row count increased
$afterCount = Get-BookingCount
Write-Host "Rows after workflow:  $afterCount"

if ($afterCount -le $beforeCount) {
    Write-Host "[FAIL] appointment_bookings count did not increase (before=$beforeCount, after=$afterCount)." -ForegroundColor Red
    exit 1
}

Write-Host "[PASS] appointment_bookings count increased by $($afterCount - $beforeCount)." -ForegroundColor Green

# 6) Show latest row for visibility
docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -c 'SELECT "BookingId", "PatientId", "DoctorId", "SlotId", "Status", "ConfirmedAt" FROM appointment_bookings ORDER BY "ConfirmedAt" DESC LIMIT 1;'

Write-Host ""
Write-Host "Integration result: PASS" -ForegroundColor Cyan
exit 0
