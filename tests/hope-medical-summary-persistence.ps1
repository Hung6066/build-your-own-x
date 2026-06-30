<#
  Hope.Agent — Medical Summary Persistence Integration Test

  Purpose:
    - Dispatch a medical summary request through the agent runtime
    - Wait until the summary is persisted to Postgres
    - Verify medical_summaries row count increased in Postgres

  Prerequisites:
    - API is running (default: http://localhost:5080)
    - Temporal worker enabled in API runtime
    - Docker container with Postgres is running

  Usage:
    .\tests\hope-medical-summary-persistence.ps1
    .\tests\hope-medical-summary-persistence.ps1 -BaseUrl http://localhost:5080
#>

param(
    [string]$BaseUrl = "http://localhost:5080",
    [string]$ClientId = "doctor-nguyen",
    [string]$Secret = "HopeAgentDev2026!",
    [string]$PostgresContainer = "hope-agent-postgres-1",
    [string]$PostgresUser = "hope",
    [string]$PostgresDb = "hope_agent",
    [int]$PersistPollMaxAttempts = 30,
    [int]$PersistPollDelaySeconds = 1
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

function Get-MedicalSummaryCount {
    $sql = "SELECT COUNT(*) FROM medical_summaries;"
    $countRaw = docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -t -A -c $sql
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to query Postgres container '$PostgresContainer'."
    }

    $value = ($countRaw | Select-Object -First 1).ToString().Trim()
    try {
        return [int]::Parse($value, [System.Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        throw "Invalid medical summary count output: '$value'"
    }
}

Write-Host "== Hope Medical Summary Persistence Integration Test ==" -ForegroundColor Cyan
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

$beforeCount = Get-MedicalSummaryCount
Write-Host "Rows before dispatch: $beforeCount"

$patientId = [guid]::NewGuid()
$dispatch = Invoke-Api -Method "POST" -Path "/v1/multi-agent/dispatch" -Body @{
    intent = "medical_summary"
    input = "Tóm tắt bệnh án cho bệnh nhân có đau ngực nhẹ, huyết áp ổn định, tiền sử tăng huyết áp và đang dùng amlodipine. Hãy tạo SOAP note ngắn gọn bằng tiếng Việt."
    context = @{
        patient_id = $patientId
        summary_type = "soap"
        audience = "clinician"
        specialty = "cardiology"
        model = "integration-test"
    }
} -Headers $authHeaders

if ($dispatch.StatusCode -ne 200) {
    Write-Host "[FAIL] Medical summary dispatch failed (status=$($dispatch.StatusCode))." -ForegroundColor Red
    Write-Host "Response: $($dispatch.Raw)"
    exit 1
}

Write-Host "[PASS] Medical summary dispatch succeeded." -ForegroundColor Green

$afterCount = $null
for ($i = 1; $i -le $PersistPollMaxAttempts; $i++) {
    $currentCount = Get-MedicalSummaryCount
    Write-Host ("Poll {0}/{1}: rows={2}" -f $i, $PersistPollMaxAttempts, $currentCount)

    if ($currentCount -gt $beforeCount) {
        $afterCount = $currentCount
        break
    }

    Start-Sleep -Seconds $PersistPollDelaySeconds
}

if ($null -eq $afterCount) {
    Write-Host "[FAIL] medical_summaries count did not increase within timeout." -ForegroundColor Red
    exit 1
}

Write-Host "[PASS] medical_summaries count increased by $($afterCount - $beforeCount)." -ForegroundColor Green

docker exec $PostgresContainer psql -U $PostgresUser -d $PostgresDb -c 'SELECT "SummaryId", "PatientId", "SummaryType", "Audience", "Specialty", "Status", "CreatedAt" FROM medical_summaries ORDER BY "CreatedAt" DESC LIMIT 1;'

Write-Host ""
Write-Host "Integration result: PASS" -ForegroundColor Cyan
exit 0