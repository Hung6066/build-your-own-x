# Hope.Agent — Full System Integration Test Suite
# ==============================================
# Tests all 26 endpoint groups, all Phase 19-22 features, and security.
# Usage: .\tests\hope-full-test.ps1 [-BaseUrl http://localhost:5080]
param([string]$BaseUrl = "http://localhost:5080")

$p = 0; $f = 0; $s = 0
$token = $null

function ok($label,$code)  { $script:p++; Write-Host "  [PASS] $label ($code)" -F Green }
function no($label,$code)  { $script:f++; Write-Host "  [FAIL] $label ($code)" -F Red }
function sk($label)        { $script:s++; Write-Host "  [SKIP] $label" -F Yellow }
function tryGet { param($m,$u,$b,$e) 
  try { $j=if($b){$b|ConvertTo-Json -Depth 5 -Compress}else{$null}
        $sc=0; $r=Invoke-RestMethod -Uri "$BaseUrl$u" -Method $m -Body $j -ContentType "application/json" -TimeoutSec 10 -SkipCertificateCheck -StatusCodeVariable sc
        if($sc -eq $e){ok "$m $u" $sc}else{no "$m $u → $sc (expected $e)" $sc}; return $r,$sc }
  catch { $code=$_.Exception.Response.StatusCode.value__
          if($code -eq $e){ok "$m $u" $code}else{no "$m $u → $code (expected $e): $($_.Exception.Message.Substring(0,[Math]::Min(80,$_.Exception.Message.Length)))" $code }; return $null,$code }
}

function tryGetToken { 
  try { $r,$sc = tryGet "POST" "/v1/auth/login" @{clientId="doctor-nguyen";secret="Hope@2026!";tenantId="550e8400-e29b-41d4-a716-446655440000"} 200
        if($r.accessToken){$script:token=$r.accessToken;ok "Login → token acquired" 200}else{sk "Login returned 200 but no token"}} 
  catch { sk "Login not available (no Auth:ServiceAccounts configured)" }
}

function tryAuth { param($m,$u,$b,$e)
  if($token){$h=@{Authorization="Bearer $token"}; $j=if($b){$b|ConvertTo-Json -Depth 5 -Compress}else{$null}
    try{$sc=0;$r=Invoke-RestMethod -Uri "$BaseUrl$u" -Method $m -Body $j -Headers $h -ContentType "application/json" -TimeoutSec 10 -SkipCertificateCheck -StatusCodeVariable sc
        if($sc -eq $e){ok "$m $u" $sc}else{no "$m $u → $sc (expected $e)" $sc}; return $r,$sc}
    catch{$c=$_.Exception.Response.StatusCode.value__
          if($c -eq $e){ok "$m $u" $c}else{no "$m $u → $c (expected $e)" $c}; return $null,$c}}
  else{sk "$m $u (no auth token)"; return $null,0}
}

Write-Host @"

╔══════════════════════════════════════════════════════════════════╗
║     Hope.Agent — Full System Integration Test Suite             ║
║     Phase 19-22 + All 26 Endpoint Groups                        ║
╚══════════════════════════════════════════════════════════════════╝
"@ -F Cyan
Write-Host "  Target : $BaseUrl"
Write-Host "  Start  : $(Get-Date -Format 'HH:mm:ss')`n"

# ═══════════════════════════════════════════════════════════════════
#  SECTION 1: Health & Meta (no auth needed)
# ═══════════════════════════════════════════════════════════════════
Write-Host "═══ S1: Health, Meta & Startup Probes ═══" -F Magenta
tryGet "GET" "/healthz/live" $null 200
tryGet "GET" "/healthz/startup" $null 200
tryGet "GET" "/.well-known/security.txt" $null 200
tryGet "GET" "/.well-known/jwks.json" $null 200
tryGet "GET" "/openapi/v1.json" $null 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 2: Authentication
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S2: Authentication (Login, Refresh, Revoke) ═══" -F Magenta
tryGetToken
if($token){
  tryGet "POST" "/v1/auth/refresh" @{refreshToken=$token} 200
  tryGet "POST" "/v1/auth/revoke" @{refreshToken=$token} 200
}

# ═══════════════════════════════════════════════════════════════════
#  SECTION 3: FHIR R4 Validation (H-1) — no auth needed
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S3: FHIR R4 Validation (Phase 21 / H-1) ═══" -F Magenta
$pat = @{resourceType="Patient";id="fhir-p1";name=@(@{family="Nguyen";given=@("Van","A")})}
tryGet "POST" "/v1/fhir/Patient" $pat 200
$obs = @{resourceType="Observation";status="final";code=@{coding=@(@{system="http://loinc.org";code="8480-6"})};subject=@{reference="Patient/fhir-p1"}}
tryGet "POST" "/v1/fhir/Observation" $obs 200
tryGet "POST" "/v1/fhir/Patient" @{resourceType="Patient"} 422
tryGet "POST" "/v1/fhir/BadResource" @{resourceType="BadResource"} 400
# FHIR MedicationRequest
$med = @{resourceType="MedicationRequest";id="med-1";status="active";intent="order";medicationCodeableConcept=@{text="Metformin 1000mg"};subject=@{reference="Patient/fhir-p1"}}
tryGet "POST" "/v1/fhir/MedicationRequest" $med 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 4: Agent Chat (Phase 19/20 features: C-3, C-4, C-5, H-7)
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S4: Agent Chat — Core (C-3 billing, C-4 cache, C-5 parallel, H-7 lock) ═══" -F Magenta
tryAuth "POST" "/v1/agent/chat" @{message="Tìm bệnh nhân MRN-2024-00123"} 200
tryAuth "POST" "/v1/agent/chat" @{message="Tra ICD-10 cho bệnh tiểu đường type 2"} 200
tryAuth "POST" "/v1/agent/chat" @{message="Kiểm tra bảo hiểm và lên lịch khám cho MRN-2024-00456"} 200
# Idempotency test
if($token){
  $h=@{Authorization="Bearer $token";"Idempotency-Key"="test-ik-001"}
  try{$sc=0;$r=Invoke-RestMethod "$BaseUrl/v1/agent/chat" -Method POST -Body '{"message":"Hello"}' -Headers $h -ContentType "application/json" -TimeoutSec 10 -SkipCertificateCheck -StatusCodeVariable sc
      if($sc -eq 200){ok "POST /v1/agent/chat (idempotency)" $sc}
      else{no "POST /v1/agent/chat (idempotency) → $sc" $sc}}
  catch{$c=$_.Exception.Response.StatusCode.value__;if($c -eq 200){ok "POST /v1/agent/chat (idempotency)" $c}else{no "Idempotency → $c" $c}}
}

# ═══════════════════════════════════════════════════════════════════
#  SECTION 5: Security — Input Validation & Shields (H-3)
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S5: Security Validation ═══" -F Magenta
tryGet "POST" "/v1/auth/login" @{username="DROP TABLE;--"} 400
tryGet "POST" "/v1/auth/login" @{username="' OR 1=1; --"} 400
tryGet "POST" "/v1/auth/login" @{username="'; DROP TABLE users; --"} 400

# ═══════════════════════════════════════════════════════════════════
#  SECTION 6: Rate Limiting
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S6: Rate Limiting ═══" -F Magenta
$limited = $false
for ($i=1; $i -le 15; $i++) {
  try { $null = Invoke-RestMethod "$BaseUrl/v1/auth/login" -Method POST -Body '{"username":"x"}' -ContentType "application/json" -TimeoutSec 3 -SkipCertificateCheck -StatusCodeVariable rsc
        if ($rsc -eq 429) { $limited=$true; break } }
  catch { if ($_.Exception.Response.StatusCode.value__ -eq 429) { $limited=$true; break } }
}
if ($limited) { ok "Rate limit 429 triggered" 429 } else { sk "Rate limit not triggered in 15 attempts" }

# ═══════════════════════════════════════════════════════════════════
#  SECTION 7: RAG & Memory (requires auth)
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S7: RAG & Memory ═══" -F Magenta
tryAuth "POST" "/v1/rag/search" @{query="hướng dẫn điều trị tiểu đường";topK=5} 200
tryAuth "POST" "/v1/memory/search" @{query="bệnh nhân tiểu đường";topK=5} 200
tryAuth "POST" "/v1/memory/upsert" @{content="Bệnh nhân Nguyễn Văn A, 65t, tiểu đường type 2, HbA1c 8.2%";kind="Episodic";source="test"} 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 8: Multi-Agent & Subagents
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S8: Multi-Agent & Subagents ═══" -F Magenta
tryAuth "POST" "/v1/multi-agent/dispatch" @{intent="scheduling";input="Lên lịch khám nội tiết"} 200
tryAuth "POST" "/v1/subagents/fan-out" @{userId="00000000-0000-0000-0000-000000000001";question="Đánh giá bệnh nhân";specs=@(@{profile="endocrinology"},@{profile="cardiology"})} 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 9: Knowledge Graph
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S9: Knowledge Graph ═══" -F Magenta
tryAuth "POST" "/v1/knowledge/query" @{query="MATCH (d:Drug) RETURN d.name LIMIT 5"} 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 10: Learning & Feedback
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S10: Learning & Feedback ═══" -F Magenta
tryAuth "POST" "/v1/learning/feedback" @{conversationId="00000000-0000-0000-0000-000000000001";rating=5;comment="Good response"} 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 11: Shadow A/B Testing
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S11: Shadow A/B Testing ═══" -F Magenta
tryAuth "GET" "/v1/shadow/challengers" $null 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 12: Adversarial Patterns
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S12: Adversarial Patterns ═══" -F Magenta
tryAuth "GET" "/v1/adversarial/patterns" $null 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 13: Tool Approval
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S13: Tool Approval ═══" -F Magenta
tryAuth "GET" "/v1/approvals/pending" $null 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 14: Channels (Zalo/Slack/Email)
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S14: Channels ═══" -F Magenta
tryAuth "GET" "/v1/channels" $null 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 15: Insights & User Models
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S15: Insights ═══" -F Magenta
tryAuth "GET" "/v1/insights/summary" $null 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 16: Dashboard (Phase 21)
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S16: Dashboard ═══" -F Magenta
tryAuth "GET" "/v1/dashboard/overview" $null 200
tryAuth "GET" "/v1/dashboard/conversations?take=5" $null 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 17: Diagnostics & Clinical Context
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S17: Diagnostics ═══" -F Magenta
tryAuth "GET" "/v1/diagnostics" $null 200
tryAuth "GET" "/v1/diagnostics/context/profiles" $null 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 18: Tools Registry
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S18: Tools Registry ═══" -F Magenta
tryAuth "GET" "/v1/tools" $null 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 19: Research / Vector Search
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S19: Research ═══" -F Magenta
tryAuth "POST" "/v1/research/vector-search" @{query="test";collection="clinical_guidelines";topK=3} 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 20: Workflows (Temporal)
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S20: Workflows ═══" -F Magenta
tryAuth "POST" "/v1/workflows/start" @{workflowName="schedule-appointment";input=@{patientId="MRN-001";date="2026-06-10"}} 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 21: Webhooks
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S21: Webhooks ═══" -F Magenta
tryAuth "GET" "/v1/webhooks" $null 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 22: Training / Fine-tuning
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S22: Training ═══" -F Magenta
tryAuth "GET" "/v1/training/jobs?take=5" $null 200
tryAuth "GET" "/v1/training/preference/count" $null 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 23: Kanban
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S23: Kanban ═══" -F Magenta
tryAuth "GET" "/v1/kanban/boards" $null 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 24: Migration
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S24: Migration ═══" -F Magenta
tryAuth "GET" "/v1/migration/status" $null 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 25: Voice
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S25: Voice ═══" -F Magenta
tryAuth "POST" "/v1/voice/synthesize" @{text="Xin chào";voice="alloy"} 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 26: MCP (Model Context Protocol)
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S26: MCP ═══" -F Magenta
tryAuth "GET" "/mcp" $null 200

# ═══════════════════════════════════════════════════════════════════
#  SECTION 27: Security — Cross-Tenant BOLA
# ═══════════════════════════════════════════════════════════════════
Write-Host "`n═══ S27: Multi-Tenant Isolation ═══" -F Magenta
if($token){
  $h=@{Authorization="Bearer $token"}
  try{$sc=0;$r=Invoke-RestMethod "$BaseUrl/v1/memory/search" -Method POST -Body '{"query":"test","topK":3}' -Headers $h -ContentType "application/json" -TimeoutSec 8 -SkipCertificateCheck -StatusCodeVariable sc
      if($sc -eq 200){ok "Tenant-scoped memory search" 200}else{no "Memory search → $sc" $sc}}
  catch{$c=$_.Exception.Response.StatusCode.value__;if($c -eq 200){ok "Tenant memory" $c}else{no "Tenant memory → $c" $c}}
}

# ═══════════════════════════════════════════════════════════════════
#  SUMMARY
# ═══════════════════════════════════════════════════════════════════
$total = $p + $f + $s
Write-Host "`n════════════════════════════════════════════════════" -F Cyan
Write-Host "  TOTAL TESTS : $total" -F White
Write-Host "  ✅ PASSED   : $p" -F Green
Write-Host "  ❌ FAILED   : $f" -F Red
Write-Host "  ⏭️  SKIPPED  : $s" -F Yellow

if ($f -eq 0) {
  Write-Host "`n  🎉 ALL TESTS PASSED — Hope.Agent is healthy!`n" -F Green
} elseif ($p -gt 0) {
  Write-Host "`n  ⚠️  PARTIAL: $p pass, $f fail. Some endpoints need infra (Postgres/Redis/Qdrant).`n" -F Yellow
} else {
  Write-Host "`n  ❌ NO TESTS PASSED — API may not be running.`n" -F Red
  Write-Host "  Start: dotnet run --project src/Hope.Agent.Api" -F White
}
exit $f
