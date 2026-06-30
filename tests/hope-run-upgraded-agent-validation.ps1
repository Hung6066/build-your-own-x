<#
  Hope.Agent - Upgraded Agent Validation

  Runs the P0-P2 validation path:
    - build the solution
    - verify workflow implementation does not use common non-deterministic APIs
    - seed realistic persistence data, including optimization_cost_hints

  Usage:
    .\tests\hope-run-upgraded-agent-validation.ps1
    .\tests\hope-run-upgraded-agent-validation.ps1 -SkipBuild
#>

param(
    [string]$PostgresContainer = "hope-agent-postgres-1",
    [string]$PostgresUser = "hope",
    [string]$PostgresDb = "hope_agent",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

Write-Host "== Hope Upgraded Agent Validation ==" -ForegroundColor Cyan

if (-not $SkipBuild) {
    Write-Host ""
    Write-Host "1) Building solution..." -ForegroundColor Cyan
    dotnet build Hope.Agent.sln
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "[PASS] Build succeeded." -ForegroundColor Green
}

Write-Host ""
Write-Host "2) Checking workflow temporal determinism..." -ForegroundColor Cyan
$determinismMatches = rg -n "DateTimeOffset\.UtcNow|Guid\.NewGuid\(\)|Random\(" src\Hope.Agent.Workflows\WorkflowsImpl
if ($LASTEXITCODE -eq 0) {
    Write-Host "[FAIL] Non-deterministic workflow calls found:" -ForegroundColor Red
    $determinismMatches | Out-Host
    exit 1
}

if ($LASTEXITCODE -ne 1) {
    Write-Host "[FAIL] Determinism scan failed with exit code $LASTEXITCODE." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "[PASS] No common non-deterministic calls found in workflow implementations." -ForegroundColor Green

Write-Host ""
Write-Host "3) Running upgraded clinical persistence seed..." -ForegroundColor Cyan
& .\tests\hope-clinical-persistence-flows.ps1 `
    -DirectPostgres `
    -PostgresContainer $PostgresContainer `
    -PostgresUser $PostgresUser `
    -PostgresDb $PostgresDb
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "Upgraded agent validation result: PASS" -ForegroundColor Cyan
