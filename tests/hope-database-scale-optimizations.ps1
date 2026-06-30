param(
    [string]$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
)

$ErrorActionPreference = "Stop"

function Assert-Contains {
    param(
        [string]$Path,
        [string]$Pattern,
        [string]$Name
    )

    $content = Get-Content -LiteralPath $Path -Raw
    if ($content -notmatch [regex]::Escape($Pattern)) {
        throw "Missing $Name in $Path"
    }

    [pscustomobject]@{ Check = $Name; Status = "PASS" }
}

$migration = Join-Path $RepoRoot "src\Hope.Agent.Infrastructure\Migrations\20260607110000_AddDatabaseScaleOptimizations.cs"
$dbContext = Join-Path $RepoRoot "src\Hope.Agent.Infrastructure\Persistence\AgentDbContext.cs"
$outbox = Join-Path $RepoRoot "src\Hope.Agent.Infrastructure\Eventing\EfOutboxStore.cs"
$rag = Join-Path $RepoRoot "src\Hope.Agent.Infrastructure\Rag\AgenticRagService.cs"
$dashboard = Join-Path $RepoRoot "src\Hope.Agent.Api\Endpoints\DashboardEndpoints.cs"
$apiConfig = Join-Path $RepoRoot "src\Hope.Agent.Api\appsettings.json"
$workerConfig = Join-Path $RepoRoot "src\Hope.Agent.Worker\appsettings.json"

$checks = @()
$checks += Assert-Contains $migration "outbox_events" "outbox table migration"
$checks += Assert-Contains $migration "agent_ops_hourly_metrics" "ops hourly rollup table"
$checks += Assert-Contains $migration "tenant_cost_daily" "tenant cost rollup table"
$checks += Assert-Contains $migration "workflow_success_daily" "workflow success rollup table"
$checks += Assert-Contains $migration "scale_partition_policies" "partition policy table"
$checks += Assert-Contains $migration "hope_ensure_scale_partitions" "partition maintenance function"
$checks += Assert-Contains $migration 'IX_audit_logs_TenantId_Action_OccurredAt' "audit composite index"
$checks += Assert-Contains $migration 'IX_agent_decisions_TenantId_PatientId_CreatedAt' "decision tenant patient cursor index"
$checks += Assert-Contains $migration 'IX_agentic_rag_retrievals_RunId_Iteration_CreatedAt' "rag retrieval run iteration index"
$checks += Assert-Contains $migration "to_tsvector('simple'" "postgres full-text indexes"
$checks += Assert-Contains $migration "gin_trgm_ops" "trigram fuzzy indexes"
$checks += Assert-Contains $dbContext "DbSet<OutboxEvent>" "outbox DbSet"
$checks += Assert-Contains $dbContext "DbSet<AgentOpsHourlyMetric>" "rollup DbSet"
$checks += Assert-Contains $outbox "OutboxPublisherWorker" "outbox publisher worker"
$checks += Assert-Contains $outbox "Math.Pow(2, attempt - 1)" "exponential backoff"
$checks += Assert-Contains $outbox "Random.Shared.Next" "retry jitter"
$checks += Assert-Contains $rag 'retrievalStrategy = "hybrid-vector-bm25-rrf-simhash-budget"' "rag strategy metric"
$checks += Assert-Contains $rag "SimHash" "context near-duplicate removal"
$checks += Assert-Contains $rag "SourceTrust" "context trust weighting"
$checks += Assert-Contains $dashboard "/audit-cursor" "audit cursor endpoint"
$checks += Assert-Contains $dashboard "/autonomy/decisions-cursor" "decision cursor endpoint"
$checks += Assert-Contains $dashboard "/autonomy/actions-cursor" "action cursor endpoint"
$checks += Assert-Contains $apiConfig '"DatabaseScale"' "api database scale config"
$checks += Assert-Contains $apiConfig '"Outbox"' "api outbox config"
$checks += Assert-Contains $workerConfig '"DatabaseScale"' "worker database scale config"
$checks += Assert-Contains $workerConfig '"Outbox"' "worker outbox config"

$checks | Format-Table -AutoSize
Write-Host "PASS hope-database-scale-optimizations"
