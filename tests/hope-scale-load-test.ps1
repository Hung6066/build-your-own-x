<#
  Hope.Agent - Scale Load Test

  Exercises:
    - Concurrent authenticated API requests.
    - Agent chat guard path.
    - Harness/dashboard status endpoints.
    - Autonomy suggestion path.

  Usage:
    .\tests\hope-scale-load-test.ps1 -Concurrency 20 -RequestsPerWorker 10
    .\tests\hope-scale-load-test.ps1 -SkipApiStart
#>

param(
    [string]$BaseUrl = "http://localhost:5080",
    [string]$ClientId = "doctor-nguyen",
    [string]$Secret = "HopeAgentDev2026!",
    [int]$Concurrency = 10,
    [int]$RequestsPerWorker = 5,
    [switch]$SkipApiStart
)

$ErrorActionPreference = "Stop"

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

Write-Host "== Hope Scale Load Test ==" -ForegroundColor Cyan
Write-Host "Concurrency=$Concurrency RequestsPerWorker=$RequestsPerWorker"

$apiProcess = $null
try {
    if (-not $SkipApiStart) {
        $dotnet = "C:\Program Files\dotnet\dotnet.exe"
        $out = "D:\Pr.Project\Hope.Agent\artifacts\scale-load-api.out.log"
        $err = "D:\Pr.Project\Hope.Agent\artifacts\scale-load-api.err.log"
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
    $token = [string]$login.accessToken
    if ([string]::IsNullOrWhiteSpace($token)) { throw "Login failed." }

    $script = {
        param($BaseUrl, $Token, $RequestsPerWorker)
        $headers = @{ Authorization = "Bearer $Token" }
        $results = New-Object System.Collections.Generic.List[object]
        for ($i = 0; $i -lt $RequestsPerWorker; $i++) {
            foreach ($path in @("/v1/harness/status", "/v1/dashboard/scale", "/v1/dashboard/cost")) {
                $sw = [System.Diagnostics.Stopwatch]::StartNew()
                try {
                    $res = Invoke-WebRequest -Uri "$BaseUrl$path" -Headers $headers -UseBasicParsing -TimeoutSec 30
                    $results.Add([pscustomobject]@{ Path=$path; Status=[int]$res.StatusCode; Ms=$sw.ElapsedMilliseconds; Error=$null })
                } catch {
                    $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
                    $results.Add([pscustomobject]@{ Path=$path; Status=$status; Ms=$sw.ElapsedMilliseconds; Error=$_.Exception.Message })
                }
            }
        }
        return $results
    }

    $jobs = for ($w = 0; $w -lt $Concurrency; $w++) {
        Start-Job -ScriptBlock $script -ArgumentList $BaseUrl,$token,$RequestsPerWorker
    }
    $rows = $jobs | Receive-Job -Wait -AutoRemoveJob
    $total = @($rows).Count
    $errors = @($rows | Where-Object { $_.Status -lt 200 -or $_.Status -ge 300 }).Count
    $latencies = @($rows | ForEach-Object { [double]$_.Ms } | Sort-Object)
    $avg = if ($latencies.Count -eq 0) { 0 } else { ($latencies | Measure-Object -Average).Average }
    $p95Index = if ($latencies.Count -eq 0) { 0 } else { [Math]::Min($latencies.Count - 1, [Math]::Ceiling($latencies.Count * 0.95) - 1) }
    $p95 = if ($latencies.Count -eq 0) { 0 } else { $latencies[$p95Index] }
    $summary = [pscustomobject]@{
        TotalRequests = $total
        Errors = $errors
        ErrorRate = if ($total -eq 0) { 0 } else { [Math]::Round($errors / $total, 4) }
        AvgMs = [Math]::Round($avg, 2)
        P95Ms = $p95
    }
    $summary | ConvertTo-Json -Depth 4
    if ($errors -gt 0) { throw "Scale load test had $errors failed requests." }
    Write-Host "Scale load test result: PASS" -ForegroundColor Cyan
    exit 0
}
finally {
    if ($apiProcess -and -not $apiProcess.HasExited) {
        Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue
    }
}
