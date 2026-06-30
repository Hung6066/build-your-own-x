<#
  Hope.Agent - Production Scale P0/P1/P2 Validation

  Validates the production-scale control plane:
    - API/Worker split in config and deployment manifests.
    - Durable queue policy: Temporal/Kafka for work, Postgres as ledger.
    - Tenant isolation, budget, lifecycle, deployment safety config.
    - Dashboard runtime exposes scale/cost/registry controls.
    - Migration contains tenant/version/idempotency/dispatch metadata columns.

  Usage:
    .\tests\hope-production-scale-p0-p2.ps1
    .\tests\hope-production-scale-p0-p2.ps1 -SkipApiStart
#>

param(
    [string]$BaseUrl = "http://localhost:5080",
    [string]$ClientId = "doctor-nguyen",
    [string]$Secret = "HopeAgentDev2026!",
    [switch]$SkipApiStart
)

$ErrorActionPreference = "Stop"
$Root = "D:\Pr.Project\Hope.Agent"

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "[FAIL] $Message" }
    Write-Host "[PASS] $Message" -ForegroundColor Green
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

Write-Host "== Hope Production Scale P0/P1/P2 Validation ==" -ForegroundColor Cyan

$apiProcess = $null
try {
    $prod = Get-Content "$Root\src\Hope.Agent.Api\appsettings.Production.json" -Raw | ConvertFrom-Json
    Assert-True ($prod.Runtime.EnableHostedServices -eq $false) "P0: production API disables hosted services."
    Assert-True ($prod.Runtime.ApiAcceptsBackgroundJobs -eq $false) "P0: production API does not accept background execution."
    Assert-True ($prod.Runtime.DurableQueueBackend -match "Temporal|Kafka") "P0: durable queue backend is Temporal/Kafka."
    Assert-True ($prod.Runtime.PostgresQueueHighThroughputAllowed -eq $false) "P0: Postgres is not allowed as high-throughput queue."
    Assert-True ($prod.ToolApproval.AllowUnconfiguredToolAccess -eq $false) "P0: production tool RBAC defaults deny unknown tools."
    Assert-True ($prod.ToolApproval.RequireIdempotencyKeyForWrites -eq $true) "P0: write tools require idempotency key."
    Assert-True ($prod.TenantIsolation.RequireTenantIdForWrites -eq $true) "P0: tenant id is required for production writes."
    Assert-True ($prod.DeploymentSafety.RequireEvalGateBeforeDeploy -eq $true) "P2: deploy requires eval gate."
    Assert-True ($prod.DeploymentSafety.AutoRollbackOnEvalFailure -eq $true) "P2: eval failure can trigger rollback."

    $composeRoot = Get-Content "$Root\docker-compose.yml" -Raw
    $composeDeploy = Get-Content "$Root\deployments\docker-compose.yml" -Raw
    $k8sWorker = Get-Content "$Root\deployments\k8s\12-worker.yaml" -Raw
    Assert-True ($composeRoot -match "hope-agent-worker" -and $composeRoot -match "PROJECT: Hope.Agent.Worker") "P0: root compose has worker service."
    Assert-True ($composeDeploy -match "worker:" -and $composeDeploy -match "PROJECT: Hope.Agent.Worker") "P0: deployment compose has worker service."
    Assert-True ($k8sWorker -match "hope-agent-worker" -and $k8sWorker -match "HorizontalPodAutoscaler") "P0/P2: Kubernetes worker deployment and HPA exist."

    $migration = Get-Content "$Root\src\Hope.Agent.Infrastructure\Migrations\20260606130000_AddProductionScaleMetadata.cs" -Raw
    foreach ($needle in @("TenantId", "DeploymentVersion", "PromptVersion", "ModelVersion", "ToolsetVersion", "PolicyVersion", "IdempotencyKey", "QueueBackend", "DispatchedToDurableQueue", "CompensationToolName")) {
        Assert-True ($migration -match $needle) "P0/P1: migration contains $needle."
    }

    if (-not $SkipApiStart) {
        $dotnet = "C:\Program Files\dotnet\dotnet.exe"
        $out = "$Root\artifacts\prod-scale-api.out.log"
        $err = "$Root\artifacts\prod-scale-api.err.log"
        Remove-Item $out,$err -ErrorAction SilentlyContinue
        $env:ASPNETCORE_ENVIRONMENT = "Development"
        $apiProcess = Start-Process -FilePath $dotnet `
            -ArgumentList @('bin\Debug\net9.0\Hope.Agent.Api.dll','--urls',$BaseUrl) `
            -WorkingDirectory "$Root\src\Hope.Agent.Api" `
            -RedirectStandardOutput $out `
            -RedirectStandardError $err `
            -PassThru `
            -WindowStyle Hidden
    }

    Wait-ApiReady
    $login = Invoke-RestMethod -Uri "$BaseUrl/v1/auth/login" -Method Post -ContentType "application/json" `
        -Body (@{ clientId = $ClientId; secret = $Secret } | ConvertTo-Json -Compress)
    $headers = @{ Authorization = "Bearer $($login.accessToken)" }

    $scale = Invoke-RestMethod -Uri "$BaseUrl/v1/dashboard/scale" -Headers $headers -TimeoutSec 30
    Assert-True ($scale.queues.durableQueueBackend -match "Temporal|Kafka") "P0: scale dashboard exposes durable queue backend."
    Assert-True ($scale.queues.postgresQueueHighThroughputAllowed -eq $false) "P0: scale dashboard confirms Postgres queue is ledger-only."
    Assert-True ($scale.tenantIsolation.requireTenantScopedRetrieval -eq $true) "P1: dashboard exposes tenant-scoped retrieval policy."
    Assert-True ($scale.costControl.enableRealtimeAlerts -eq $true) "P2: dashboard exposes real-time cost alerts."
    Assert-True ($scale.dataLifecycle.phiRedactionRequired -eq $true) "P2: dashboard exposes PHI redaction lifecycle policy."
    Assert-True ($scale.deploymentSafety.enableShadowTraffic -eq $true) "P2: dashboard exposes shadow/canary deployment policy."

    $registry = Invoke-RestMethod -Uri "$BaseUrl/v1/dashboard/agent-registry" -Headers $headers -TimeoutSec 30
    Assert-True ($null -ne $registry.registry.agents.autonomy) "P2: agent registry exposes autonomy agent."
    Assert-True ($registry.registry.agents.autonomy.workflowDag -eq "clinical_autonomy") "P2: autonomy agent maps to workflow DAG."

    $cost = Invoke-RestMethod -Uri "$BaseUrl/v1/dashboard/cost" -Headers $headers -TimeoutSec 30
    Assert-True ($null -ne $cost.byTenant) "P0/P2: cost dashboard groups by tenant."

    Write-Host "Production scale P0/P1/P2 validation result: PASS" -ForegroundColor Cyan
    exit 0
}
finally {
    if ($apiProcess -and -not $apiProcess.HasExited) {
        Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue
    }
}
