#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Hope Agent – Evaluation trend viewer.
    Shows score history per suite with delta arrows so you can see whether
    the agent is getting smarter over time.

.EXAMPLE
    .\hope-eval.ps1
    .\hope-eval.ps1 -Suite cardiology -Days 60
    .\hope-eval.ps1 -Run         # trigger a new eval run and show results
    .\hope-eval.ps1 -AddCase     # interactive: add a new test case
#>
param(
    [string]  $BaseUrl = "http://localhost:5000",
    [string]  $Token = $env:HOPE_TOKEN,
    [string]  $Suite = "default",
    [int]     $Days = 30,
    [switch]  $Run,
    [switch]  $AddCase
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$headers = @{ Authorization = "Bearer $Token"; "Content-Type" = "application/json" }

function Write-Header([string]$msg) {
    Write-Host "`n$msg" -ForegroundColor Cyan
    Write-Host ("-" * 60) -ForegroundColor DarkGray
}

function Invoke-Api([string]$Method, [string]$Path, [object]$Body = $null) {
    $uri = "$BaseUrl$Path"
    $params = @{ Method = $Method; Uri = $uri; Headers = $headers; TimeoutSec = 120 }
    if ($Body) { $params.Body = ($Body | ConvertTo-Json -Depth 5) }
    try {
        return Invoke-RestMethod @params
    }
    catch {
        $status = $_.Exception.Response?.StatusCode.value__ ?? "?"
        Write-Host "  [HTTP $status] $($_.Exception.Message)" -ForegroundColor Red
        exit 2
    }
}

# ── Trigger a new eval run ──────────────────────────────────────────────────
if ($Run) {
    Write-Header "Running eval suite '$Suite' …"
    $run = Invoke-Api POST "/v1/learning/eval/run?suite=$Suite"
    $passColor = if ($run.passed -eq $run.total) { "Green" } elseif ($run.failed -eq 0) { "Yellow" } else { "Red" }
    Write-Host "  Passed : $($run.passed)/$($run.total)" -ForegroundColor $passColor
    Write-Host "  Failed : $($run.failed)"               -ForegroundColor $(if ($run.failed -gt 0) { "Red" } else { "Green" })
    Write-Host ("  Score  : {0:F3}" -f $run.avgJudgeScore) -ForegroundColor Cyan
    exit $(if ($run.failed -gt 0) { 1 } else { 0 })
}

# ── Add a test case interactively ──────────────────────────────────────────
if ($AddCase) {
    Write-Header "Add eval case to suite '$Suite'"
    $name = Read-Host "Case name"
    $userMsg = Read-Host "User message (prompt)"
    $reference = Read-Host "Reference / gold-standard answer"
    $tags = Read-Host "Tags (comma-separated, optional)"
    $body = @{ suite = $Suite; name = $name; userMessage = $userMsg; referenceAnswer = $reference; tags = $tags }
    $result = Invoke-Api POST "/v1/learning/eval/cases" -Body $body
    Write-Host "  Created: $($result.id)" -ForegroundColor Green
    exit 0
}

# ── Show trend ──────────────────────────────────────────────────────────────
Write-Header "Eval trend — suite='$Suite'  last $Days days"

$trend = Invoke-Api GET "/v1/learning/eval/trend?suite=$Suite&days=$Days"

if ($trend.Count -eq 0) {
    Write-Host "  No eval runs found for suite '$Suite' in the last $Days days." -ForegroundColor Yellow
    Write-Host "  Run:  .\hope-eval.ps1 -Run -Suite $Suite" -ForegroundColor DarkGray
    exit 0
}

# Table header
$fmt = "{0,-24}  {1,5}  {2,6}  {3,6}  {4,7}  {5}"
Write-Host ($fmt -f "RunAt (UTC)", "Total", "Passed", "Failed", "AvgScore", "Delta") -ForegroundColor DarkGray
Write-Host ("-" * 65) -ForegroundColor DarkGray

foreach ($pt in $trend) {
    $ts = ([DateTimeOffset]$pt.runAt).ToString("yyyy-MM-dd HH:mm")
    $score = "{0:F3}" -f $pt.avgScore
    $passColor = if ($pt.failed -eq 0) { "Green" } elseif ($pt.passed -ge $pt.total * 0.8) { "Yellow" } else { "Red" }

    # Delta arrow + color
    if ($null -eq $pt.deltaScore) {
        $delta = "  —  "
        $deltaColor = "DarkGray"
    }
    elseif ($pt.deltaScore -gt 0.005) {
        $delta = "+{0:F3} ↑" -f $pt.deltaScore
        $deltaColor = "Green"
    }
    elseif ($pt.deltaScore -lt -0.005) {
        $delta = "{0:F3} ↓" -f $pt.deltaScore
        $deltaColor = "Red"
    }
    else {
        $delta = "{0:F3} →" -f $pt.deltaScore
        $deltaColor = "Yellow"
    }

    $line = $fmt -f $ts, $pt.total, $pt.passed, $pt.failed, $score, ""
    Write-Host $line -NoNewline -ForegroundColor $passColor
    Write-Host $delta -ForegroundColor $deltaColor
}

Write-Host ""

# Overall verdict
$first = $trend[0].avgScore
$last = $trend[-1].avgScore
$total = $last - $first
if ($trend.Count -gt 1) {
    if ($total -gt 0.02) {
        Write-Host ("  Overall: score improved by +{0:F3} across {1} runs " -f $total, $trend.Count) -ForegroundColor Green
    }
    elseif ($total -lt -0.05) {
        Write-Host ("  Overall: score REGRESSED by {0:F3} across {1} runs — investigate!" -f $total, $trend.Count) -ForegroundColor Red
    }
    else {
        Write-Host ("  Overall: score stable ({0:F3} → {1:F3}) across {2} runs" -f $first, $last, $trend.Count) -ForegroundColor Yellow
    }
}

exit 0
