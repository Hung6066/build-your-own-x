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

$policy = Join-Path $RepoRoot "policies\security\policy-bundle.json"
$sig = Join-Path $RepoRoot "policies\security\policy-bundle.sig"
$engine = Join-Path $RepoRoot "src\Hope.Agent.Infrastructure\Security\JsonPolicyEngine.cs"
$sandbox = Join-Path $RepoRoot "src\Hope.Agent.AgentRuntime\Security\SandboxedToolExecutor.cs"
$meters = Join-Path $RepoRoot "src\Hope.Agent.Application\Observability\HopeMeters.cs"
$prod = Join-Path $RepoRoot "src\Hope.Agent.Api\appsettings.Production.json"
$ci = Join-Path $RepoRoot ".github\workflows\security-ci.yml"
$redteam = Join-Path $RepoRoot "tests\hope-redteam-regression.ps1"

$checks = @()
$checks += Assert-Contains $policy '"version": "security-policy-v1"' "versioned policy bundle"
$checks += Assert-Contains $policy '"deny_unknown_tool"' "policy rule explainability"
$checks += Assert-Contains $policy '"reason":' "policy deny reason"
if (!(Test-Path $sig)) { throw "Missing signed policy bundle artifact: $sig" }
$checks += [pscustomobject]@{ Check = "signed policy artifact"; Status = "PASS" }
$checks += Assert-Contains $engine "PolicyDecision" "policy engine decision object"
$checks += Assert-Contains $engine "cachedDigest" "policy bundle digest explainability"
$checks += Assert-Contains $engine "signature verification failed" "policy signature verification"
$checks += Assert-Contains $sandbox "kill switch" "tool kill switch"
$checks += Assert-Contains $sandbox "sandbox_isolation_required" "write tool isolation guard"
$checks += Assert-Contains $prod '"Mode": "container"' "production container sandbox mode"
$checks += Assert-Contains $prod '"HighRiskWorkerPool": "high-risk-tools"' "high-risk worker pool config"
$checks += Assert-Contains $meters "BlockedToolCalls" "blocked_tool_calls metric"
$checks += Assert-Contains $meters "PolicyDenials" "policy_denials metric"
$checks += Assert-Contains $meters "PromptInjectionDetected" "prompt_injection_detected metric"
$checks += Assert-Contains $meters "PhiRedactionCount" "phi_redaction_count metric"
$checks += Assert-Contains $meters "CrossTenantAccessDenied" "cross_tenant_access_denied metric"
$checks += Assert-Contains $meters "SuspiciousAutonomyActions" "suspicious_autonomy_action metric"
$checks += Assert-Contains (Join-Path $RepoRoot "src\Hope.Agent.Api\appsettings.json") "hope_security_blocked_tool_calls_total" "blocked tool alert rule"
$checks += Assert-Contains (Join-Path $RepoRoot "src\Hope.Agent.Api\appsettings.json") "hope_security_cross_tenant_access_denied_total" "cross tenant alert rule"
$checks += Assert-Contains $ci "Trivy" "container/dependency scan in CI"
$checks += Assert-Contains $ci "Checkov" "IaC scan in CI"
$checks += Assert-Contains $ci "cosign" "container signing in CI"
$checks += Assert-Contains $ci "slsa" "SLSA provenance in CI"
$checks += Assert-Contains $redteam "Prompt injection" "prompt injection regression"
$checks += Assert-Contains $redteam "unauthorized tool" "unauthorized tool regression"
$checks += Assert-Contains $redteam "cross-tenant" "cross-tenant leakage regression"

$checks | Format-Table -AutoSize
Write-Host "PASS hope-security-gate-p1"
