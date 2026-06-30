param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"

function Read-Text([string]$Path) {
    $full = Join-Path $RepoRoot $Path
    if (!(Test-Path $full)) { throw "Missing file: $Path" }
    Get-Content $full -Raw
}

function Assert-Contains([string]$Text, [string]$Pattern, [string]$Name) {
    if ($Text -notmatch [regex]::Escape($Pattern)) {
        [pscustomobject]@{ Check = $Name; Status = "FAIL"; Detail = "Missing '$Pattern'" }
    } else {
        [pscustomobject]@{ Check = $Name; Status = "PASS"; Detail = "" }
    }
}

$checks = @()
$app = Read-Text "src\Hope.Agent.Api\appsettings.json"
$prod = Read-Text "src\Hope.Agent.Api\appsettings.Production.json"
$worker = Read-Text "src\Hope.Agent.Worker\appsettings.json"
$domain = Read-Text "src\Hope.Agent.Domain\Security\EnterpriseSecurityRecords.cs"
$appsec = Read-Text "src\Hope.Agent.Application\Security\EnterpriseSecurityP2.cs"
$services = Read-Text "src\Hope.Agent.Infrastructure\Security\EnterpriseSecurityP2Services.cs"
$endpoints = Read-Text "src\Hope.Agent.Api\Endpoints\EnterpriseSecurityEndpoints.cs"
$db = Read-Text "src\Hope.Agent.Infrastructure\Persistence\AgentDbContext.cs"
$router = Read-Text "src\Hope.Agent.Infrastructure\Learning\BanditAdaptiveRouter.cs"
$orchestrator = Read-Text "src\Hope.Agent.AgentRuntime\AgentOrchestrator.cs"
$meters = Read-Text "src\Hope.Agent.Application\Observability\HopeMeters.cs"
$governance = Read-Text "src\Hope.Agent.Application\Governance\GovernancePolicyOptions.cs"
$migrationFile = Get-ChildItem (Join-Path $RepoRoot "src\Hope.Agent.Infrastructure\Migrations") -Filter "*_AddEnterpriseSecurityP2.cs" |
    Sort-Object Name -Descending |
    Select-Object -First 1
if (!$migrationFile) { throw "Missing AddEnterpriseSecurityP2 migration" }
$migration = Get-Content $migrationFile.FullName -Raw

$checks += Assert-Contains $appsec "EnterpriseDataPerimeterOptions" "data perimeter options"
$checks += Assert-Contains $appsec "PurposeAccess" "purpose-based access config"
$checks += Assert-Contains $appsec "BreakGlassReviewDueHours" "break-glass review SLA"
$checks += Assert-Contains $services "data_residency_region_mismatch" "region-aware data residency deny"
$checks += Assert-Contains $services "purpose_not_allowed_for_tenant" "tenant purpose denial"
$checks += Assert-Contains $appsec "SecureModelRoutingOptions" "secure model routing options"
$checks += Assert-Contains $services "provider_not_phi_approved" "PHI provider block"
$checks += Assert-Contains $services "phi_cost_latency_router_blocked" "cost latency router PHI block"
$checks += Assert-Contains $router "ISecureModelRoutingPolicy" "adaptive router policy guard"
$checks += Assert-Contains $domain "ContextProvenanceRecord" "fine-grained provenance entity"
$checks += Assert-Contains $orchestrator "ContextProvenanceWrite" "agent answer provenance mirror"
$checks += Assert-Contains $endpoints "/provenance" "provenance debug endpoint"
$checks += Assert-Contains $domain "SecurityIncidentRecord" "incident entity"
$checks += Assert-Contains $services "BuildForensicExportAsync" "forensic export service"
$checks += Assert-Contains $endpoints "/incidents/{id:guid}/forensics" "forensic export endpoint"
$checks += Assert-Contains $domain "BreakGlassAccessRecord" "break-glass entity"
$checks += Assert-Contains $endpoints "/break-glass" "break-glass workflow endpoint"
$checks += Assert-Contains $domain "AdversarialSimulationRun" "adversarial simulation entity"
$checks += Assert-Contains $services "AdversarialSimulationWorker" "continuous red-team worker"
$checks += Assert-Contains $app "AdversarialSimulation" "API adversarial config"
$checks += Assert-Contains $prod "ReplayAgainstCanary" "production canary replay config"
$checks += Assert-Contains $worker "IncidentResponse" "worker incident response config"
$checks += Assert-Contains $db "ContextProvenanceRecords" "DbContext provenance set"
$checks += Assert-Contains $db "SecurityIncidents" "DbContext incident set"
$checks += Assert-Contains $migration "context_provenance_records" "migration provenance table"
$checks += Assert-Contains $migration "security_incidents" "migration incidents table"
$checks += Assert-Contains $migration "adversarial_simulation_runs" "migration simulations table"
$checks += Assert-Contains $meters "DataPerimeterDenials" "data perimeter denial metric"
$checks += Assert-Contains $meters "ModelRoutingPolicyBlocks" "model routing block metric"
$checks += Assert-Contains $meters "SecurityIncidentsOpened" "incident metric"
$checks += Assert-Contains $meters "AdversarialSimulationRuns" "adversarial simulation metric"
$checks += Assert-Contains $governance "data_perimeter_denial" "data perimeter alert"
$checks += Assert-Contains $governance "model_routing_policy_block" "model routing alert"
$checks += Assert-Contains $governance "security_incident_opened" "incident alert"

$failed = $checks | Where-Object Status -eq "FAIL"
$checks | Select-Object Check, Status | Format-Table -AutoSize
if ($failed) {
    $failed | Format-List
    throw "FAIL hope-enterprise-security-p2"
}

Write-Host "PASS hope-enterprise-security-p2" -ForegroundColor Green
