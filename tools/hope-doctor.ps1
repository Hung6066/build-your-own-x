#!/usr/bin/env pwsh
<#
.SYNOPSIS
    hope doctor — Phase 13 diagnostic CLI for Hope.Agent.
.DESCRIPTION
    Calls GET /v1/diagnostics on a running Hope.Agent.Api instance and renders
    the result as a status table (postgres, redis, neo4j, kafka, llm).
.PARAMETER BaseUrl
    Base URL of the Hope.Agent.Api instance. Defaults to http://localhost:5000.
.PARAMETER Token
    Bearer token. Reads $env:HOPE_TOKEN if not supplied.
.EXAMPLE
    ./hope-doctor.ps1 -BaseUrl https://hope.example.com -Token $env:HOPE_TOKEN
#>
param(
    [string]$BaseUrl = ${env:HOPE_BASE_URL} ?? "http://localhost:5000",
    [string]$Token = ${env:HOPE_TOKEN}
)

if (-not $Token) {
    Write-Error "Token required. Pass -Token or set HOPE_TOKEN."
    exit 2
}

$headers = @{ Authorization = "Bearer $Token" }
try {
    $report = Invoke-RestMethod -Uri "$BaseUrl/v1/diagnostics" -Headers $headers -Method GET
}
catch {
    Write-Error "Failed to call /v1/diagnostics: $($_.Exception.Message)"
    exit 1
}

Write-Host ""
Write-Host "Hope.Agent doctor — $($report.generatedAt)" -ForegroundColor Cyan
Write-Host ("─" * 64)

$report.checks | ForEach-Object {
    $statusText = if ($_.healthy) { "[ OK ]" } else { "[FAIL]" }
    $color = if ($_.healthy) { "Green" } else { "Red" }
    Write-Host ("{0,-8} {1,-12} {2,8:N1}ms  {3}" -f $statusText, $_.name, $_.duration.TotalMilliseconds, $_.message) -ForegroundColor $color
}

Write-Host ("─" * 64)
if ($report.allHealthy) {
    Write-Host "All systems healthy." -ForegroundColor Green
    exit 0
}
else {
    Write-Host "One or more checks failed." -ForegroundColor Red
    exit 3
}
