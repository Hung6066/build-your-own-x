param(
    [string]$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
)

$ErrorActionPreference = "Stop"

function Assert-Contains {
    param([string]$Path, [string]$Pattern, [string]$Name)
    $content = Get-Content -LiteralPath $Path -Raw
    if ($content -notmatch [regex]::Escape($Pattern)) {
        throw "Missing $Name in $Path"
    }
    [pscustomobject]@{ Check = $Name; Status = "PASS" }
}

$checks = @()
$appSecurity = Join-Path $RepoRoot "src\Hope.Agent.Application\Security\ProductionSecurityOptions.cs"
$validator = Join-Path $RepoRoot "src\Hope.Agent.Infrastructure\Security\ProductionSecurityValidator.cs"
$migration = Join-Path $RepoRoot "src\Hope.Agent.Infrastructure\Migrations\20260607120000_AddProductionSecurityP0.cs"
$di = Join-Path $RepoRoot "src\Hope.Agent.Infrastructure\DependencyInjection.cs"
$kafka = Join-Path $RepoRoot "src\Hope.Agent.Infrastructure\Eventing\KafkaEventPublisher.cs"
$qdrant = Join-Path $RepoRoot "src\Hope.Agent.Infrastructure\Memory\QdrantMemoryStore.cs"
$dlp = Join-Path $RepoRoot "src\Hope.Agent.Infrastructure\Channels\DlpExternalChannel.cs"
$audit = Join-Path $RepoRoot "src\Hope.Agent.Infrastructure\Persistence\AuditImmutabilityWorker.cs"
$toolPolicy = Join-Path $RepoRoot "src\Hope.Agent.Infrastructure\Security\ConfigurableToolApprovalPolicy.cs"
$ssrf = Join-Path $RepoRoot "src\Hope.Agent.Infrastructure\Security\HeuristicSsrfGuard.cs"
$tenantContext = Join-Path $RepoRoot "src\Hope.Agent.Application\Security\ITenantContext.cs"
$tenantInterceptor = Join-Path $RepoRoot "src\Hope.Agent.Infrastructure\Persistence\TenantSessionConnectionInterceptor.cs"
$tenantMiddleware = Join-Path $RepoRoot "src\Hope.Agent.Api\Middleware\TenantContextMiddleware.cs"
$prod = Join-Path $RepoRoot "src\Hope.Agent.Api\appsettings.Production.json"
$ci = Join-Path $RepoRoot ".github\workflows\security-ci.yml"

$checks += Assert-Contains $appSecurity "ZeroTrustOptions" "zero-trust options"
$checks += Assert-Contains $appSecurity "SecretManagementOptions" "secret/KMS options"
$checks += Assert-Contains $appSecurity "DataPerimeterOptions" "data perimeter options"
$checks += Assert-Contains $appSecurity "IEnvelopeEncryptionService" "envelope encryption abstraction"
$checks += Assert-Contains $validator "Production P0 security validation failed" "production fail-fast validator"
$checks += Assert-Contains $migration "ENABLE ROW LEVEL SECURITY" "postgres RLS migration"
$checks += Assert-Contains $migration "ALTER COLUMN `"TenantId`" SET NOT NULL" "tenant id not-null migration"
$checks += Assert-Contains $migration "hope_set_tenant_context" "tenant context function"
$checks += Assert-Contains $tenantContext "AsyncLocalTenantContext" "runtime tenant context"
$checks += Assert-Contains $tenantInterceptor "set_config('app.tenant_id'" "db session tenant interceptor"
$checks += Assert-Contains $tenantMiddleware "X-Tenant-Id" "api tenant context middleware"
$checks += Assert-Contains $migration "security_posture_checks" "security posture ledger"
$checks += Assert-Contains $kafka "SslCertificateLocation" "Kafka mTLS client config"
$checks += Assert-Contains $qdrant 'point.Payload["tenant_id"]' "Qdrant tenant payload"
$checks += Assert-Contains $dlp "RedactPhiOnExternalChannels" "external channel DLP"
$checks += Assert-Contains $audit "audit.chain.verification_failed" "scheduled audit tamper verification"
$checks += Assert-Contains $toolPolicy "unconfigured_tool_default_deny" "default deny unknown tools"
$checks += Assert-Contains $ssrf "Host outside egress allowlist" "egress allowlist SSRF guard"
$checks += Assert-Contains $prod '"RequireMtls": true' "production mTLS required"
$checks += Assert-Contains $prod '"RequireWorkloadIdentity": true' "production workload identity required"
$checks += Assert-Contains $prod '"RequireKmsEnvelopeEncryption": true' "production KMS required"
$checks += Assert-Contains $prod '"RequireRedisAcl": true' "production Redis ACL required"
$checks += Assert-Contains $prod '"SecurityProtocol": "Ssl"' "production Kafka SSL"
$checks += Assert-Contains $ci "hope-redteam-regression.ps1" "CI red-team regression"
$checks += Assert-Contains $ci "hope-production-security-p0.ps1" "CI P0 security validation"
$checks += Assert-Contains $di "DlpExternalChannel" "DLP channel decorator registration"
$checks += Assert-Contains $di "ProductionSecurityValidator" "production validator registration"

$checks | Format-Table -AutoSize
Write-Host "PASS hope-production-security-p0"
