param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"

function Text($path) {
    $full = Join-Path $RepoRoot $path
    if (!(Test-Path $full)) { throw "Missing $path" }
    Get-Content $full -Raw
}

function Assert-Contains($text, $needle, $name) {
    if ($text -notmatch [regex]::Escape($needle)) {
        [pscustomobject]@{ Check = $name; Status = "FAIL"; Detail = "Missing $needle" }
    } else {
        [pscustomobject]@{ Check = $name; Status = "PASS"; Detail = "" }
    }
}

$checks = @()
$consolidator = Text "src\Hope.Agent.Infrastructure\Memory\LlmMemoryConsolidator.cs"
$hybrid = Text "src\Hope.Agent.Infrastructure\Memory\HybridMemoryStore.cs"
$prefs = Text "src\Hope.Agent.Domain\Personalization\UserPreference.cs"
$prefStore = Text "src\Hope.Agent.Application\Personalization\IUserPreferenceStore.cs"
$apiKeyStore = Text "src\Hope.Agent.Infrastructure\Security\EfApiKeyLifecycleStore.cs"
$apiKeyEndpoints = Text "src\Hope.Agent.Api\Endpoints\ApiKeyLifecycleEndpoints.cs"
$auth = Text "src\Hope.Agent.Api\Security\ApiKeyAuthHandler.cs"
$promptOpt = Text "src\Hope.Agent.Infrastructure\Prompts\PromptOptimizationService.cs"
$learning = Text "src\Hope.Agent.Api\Endpoints\LearningEndpoints.cs"
$orchestrator = Text "src\Hope.Agent.AgentRuntime\AgentOrchestrator.cs"
$meters = Text "src\Hope.Agent.Application\Observability\HopeMeters.cs"
$tenant = Text "src\Hope.Agent.Api\Middleware\TenantContextMiddleware.cs"
$prod = Text "src\Hope.Agent.Api\appsettings.Production.json"
$workflow = Text "src\Hope.Agent.Workflows\WorkflowsImpl\WorkflowCommon.cs"
$migrationFile = Get-ChildItem (Join-Path $RepoRoot "src\Hope.Agent.Infrastructure\Migrations") -Filter "*_AddApiKeyLifecycleAndPreferenceSchema.cs" | Sort-Object Name -Descending | Select-Object -First 1
if (!$migrationFile) { throw "Missing AddApiKeyLifecycleAndPreferenceSchema migration" }
$migration = Get-Content $migrationFile.FullName -Raw

$checks += Assert-Contains $consolidator "ADD|UPDATE|DELETE|NOOP" "Mem0-style memory operations"
$checks += Assert-Contains $consolidator "LinkToGraphAsync" "memory graph linking"
$checks += Assert-Contains $hybrid "QdrantMemoryStore" "hybrid Qdrant memory wired"
$checks += Assert-Contains $hybrid "falling back to Postgres" "hybrid fallback"
$checks += Assert-Contains $promptOpt "OptimizeAsync" "prompt optimizer service"
$checks += Assert-Contains $promptOpt "GenerateCandidates" "prompt candidate generation"
$checks += Assert-Contains $promptOpt "ActivateVersionAsync" "prompt promotion path"
$checks += Assert-Contains $learning "/prompts/{promptName}/optimize" "prompt optimization endpoint"
$checks += Assert-Contains $prefs "PreferredLanguage" "preference language schema"
$checks += Assert-Contains $prefs "SafetyMode" "preference safety schema"
$checks += Assert-Contains $prefs "PreferencesJson" "preference metadata json"
$checks += Assert-Contains $prefStore "SetSafetyAsync" "preference safety store API"
$checks += Assert-Contains $apiKeyStore "RevokeAsync" "API key revocation persistence"
$checks += Assert-Contains $apiKeyStore "RotateAsync" "API key rotation persistence"
$checks += Assert-Contains $apiKeyStore "Where(x => x.TenantId == tenantId)" "API key tenant-scoped list"
$checks += Assert-Contains $apiKeyEndpoints "/v1/security/api-keys" "API key lifecycle endpoint group"
$checks += Assert-Contains $apiKeyEndpoints "TenantFromClaims(user)" "API key tenant claim source of truth"
$checks += Assert-Contains $auth "FindValidAsync" "auth handler checks persisted lifecycle keys"
$checks += Assert-Contains $tenant "claim wins" "tenant claim source of truth"
$checks += Assert-Contains $tenant "tenant_mismatch" "tenant header mismatch reject"
$checks += Assert-Contains $prod '"StorageEncryption"' "TDE/storage encryption production guard"
$checks += Assert-Contains $prod '"AtRestEnabled": true' "storage encryption enabled in production config"
$checks += Assert-Contains $prod '"RedisHa"' "Redis HA production config"
$checks += Assert-Contains $prod '"redis-prod:6380,ssl=true' "Redis TLS production config"
$checks += Assert-Contains $prod '"https://qdrant-prod"' "Qdrant TLS production config"
$checks += Assert-Contains $prod '"TargetHosts"' "Temporal HA target hosts"
$checks += Assert-Contains $prod '"PostgresReadReplica"' "Postgres read replica config"
$checks += Assert-Contains $orchestrator "SecurityShieldFailures" "shield failure metric emitted"
$checks += Assert-Contains $meters "SecurityShieldFailures" "shield failure metric declared"
$checks += Assert-Contains $orchestrator "EnforceContextBudget" "hard context budget enforcement"
$checks += Assert-Contains $orchestrator "previousChunk" "stream duplicate chunk guard"
$checks += Assert-Contains (Text "src\Hope.Agent.AgentRuntime\Roles\ReminderAgentRole.cs") "phi.Redact(task.Input)" "Reminder PHI log redaction"
$checks += Assert-Contains (Text "src\Hope.Agent.AgentRuntime\Roles\AuditReportAgentRole.cs") "phi.Redact(task.Input)" "Audit PHI log redaction"
$checks += Assert-Contains (Text "src\Hope.Agent.AgentRuntime\Roles\SchedulingAgentRole.cs") "phi.Redact(task.Input)" "Scheduling PHI log redaction"
$checks += Assert-Contains (Text "src\Hope.Agent.AgentRuntime\Roles\InsuranceVerificationAgentRole.cs") "[REDACTED]" "Insurance PHI metadata redaction"
$checks += Assert-Contains $workflow "DefaultActivityOptions" "workflow common activity options"
$checks += Assert-Contains $migration "api_key_records" "API key lifecycle migration"
$checks += Assert-Contains $migration "PreferredLanguage" "preference schema migration"
$checks += Assert-Contains (Text "src\Hope.Agent.Domain\Security\ApiKeyRecord.cs") "Guid TenantId" "API key TenantId non-null model"
$checks += Assert-Contains (Text "src\Hope.Agent.Domain\Personalization\UserPreference.cs") "Guid TenantId" "preference TenantId non-null model"
$checks += Assert-Contains (Text "src\Hope.Agent.Infrastructure\Persistence\AgentDbContext.cs") "Property(x => x.TenantId).IsRequired()" "tenant columns required in EF model"

$failed = $checks | Where-Object Status -eq "FAIL"
$checks | Select-Object Check,Status | Format-Table -AutoSize
if ($failed) {
    $failed | Format-List
    throw "FAIL hope-remaining-gap-validation"
}

Write-Host "PASS hope-remaining-gap-validation" -ForegroundColor Green
