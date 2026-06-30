# Hope.Agent - Full Integration Test (Real Login)
# Tests all 27 sections against running API
param([string]$BaseUrl = "http://localhost:5080")
$p=0;$f=0;$s=0
$token=$null

# Login with service account from appsettings.Development.json
function Get-Token {
  try {
    $body = @{clientId="doctor-nguyen";secret="Hope@2026!"} | ConvertTo-Json -Compress
    $resp = Invoke-WebRequest "$BaseUrl/v1/auth/login" -Method POST -Body $body -ContentType "application/json" -TimeoutSec 10 -UseBasicParsing
    $sc = [int]$resp.StatusCode
    $r = if ($resp.Content) { $resp.Content | ConvertFrom-Json } else { $null }
    if ($sc -eq 200 -and $r -and $r.accessToken) {
      Write-Host "Login OK (doctor-nguyen, roles=doctor+admin)" -F Cyan
      return $r.accessToken
    }
    Write-Host "Login failed: $sc" -F Yellow; return $null
  } catch {
    $c = $_.Exception.Response.StatusCode.value__
    Write-Host "Login error: $c - $($_.Exception.Message.Substring(0,[Math]::Min(80,$_.Exception.Message.Length)))" -F Yellow
    return $null
  }
}
$token = Get-Token

function ok($l,$c){$script:p++;Write-Host "  [PASS] $l ($c)" -F Green}
function no($l,$c){$script:f++;Write-Host "  [FAIL] $l ($c)" -F Red}
function sk($l){$script:s++;Write-Host "  [SKIP] $l" -F Yellow}
function T($m,$u,$b,$e,$l){
  $uri="$BaseUrl$u";$h=@{"Content-Type"="application/json"};if($token){$h["Authorization"]="Bearer $token"}
  $j=if($b){$b|ConvertTo-Json -Depth 5 -Compress}else{$null}
  try{$sc=0;$resp=Invoke-WebRequest -Uri $uri -Method $m -Body $j -Headers $h -TimeoutSec 10 -UseBasicParsing
    $sc=[int]$resp.StatusCode
    $r=$null
    if($resp.Content){
      $content=$resp.Content.Trim()
      if(($content.StartsWith("{") -or $content.StartsWith("["))){
        try{$r=$content|ConvertFrom-Json}catch{$r=$content}
      } else {
        $r=$content
      }
    }
    if($sc -eq $e){ok $l $sc}else{no "$l -> $sc (expected $e)" $sc};return $r,$sc}
  catch{$code=$_.Exception.Response.StatusCode.value__
    if($code -eq $e){ok $l $code}else{no "$l -> $code (expected $e)" $code};return $null,$code}
}

Write-Host "`n=== Hope.Agent Full System Test ===" -F Cyan
Write-Host "Target: $BaseUrl | Start: $(Get-Date -Format HH:mm:ss)`n"

Write-Host "--- S1: Health and Meta ---" -F Magenta
T "GET" "/healthz/live" $null 200 "Liveness probe"
T "GET" "/healthz/startup" $null 200 "Startup probe (H-6)"
T "GET" "/.well-known/security.txt" $null 200 "security.txt"
T "GET" "/.well-known/jwks.json" $null 200 "JWKS endpoint"
T "GET" "/openapi/v1.json" $null 200 "OpenAPI spec"

Write-Host "`n--- S2: Auth ---" -F Magenta
T "POST" "/v1/auth/login" @{clientId="test";secret="wrong"} 401 "Login: bad creds -> 401"
T "POST" "/v1/auth/login" @{username="x"} 400 "Login: missing fields -> 400"

Write-Host "`n--- S3: FHIR R4 (H-1) ---" -F Magenta
$pat=@{resourceType="Patient";id="p1";name=@(@{family="Nguyen";given=@("Van","A")})}
T "POST" "/v1/fhir/Patient" $pat 200 "FHIR: valid Patient"
$obs=@{resourceType="Observation";status="final";code=@{coding=@(@{system="http://loinc.org";code="8480-6"})};subject=@{reference="Patient/p1"}}
T "POST" "/v1/fhir/Observation" $obs 200 "FHIR: valid Observation"
$med=@{resourceType="MedicationRequest";id="m1";status="active";intent="order";medicationCodeableConcept=@{text="Metformin"};subject=@{reference="Patient/p1"}}
T "POST" "/v1/fhir/MedicationRequest" $med 200 "FHIR: valid MedicationRequest"
T "POST" "/v1/fhir/Patient" @{resourceType="Patient"} 422 "FHIR: missing fields -> 422"
T "POST" "/v1/fhir/BadType" @{resourceType="BadType"} 400 "FHIR: unsupported -> 400"

Write-Host "`n--- S4: Security ---" -F Magenta
T "POST" "/v1/auth/login" @{clientId="DROP TABLE;--";secret="x"} 401 "SQL injection blocked"
T "POST" "/v1/auth/login" @{clientId="OR-1-eq-1";secret="x"} 401 "SQL injection blocked"

Write-Host "`n--- S5: Agent Chat (C-3,4,5,H-7) ---" -F Magenta
T "POST" "/v1/agent/chat" @{message="Hello"} 200 "Agent: basic"
T "POST" "/v1/agent/chat" @{message=""} 400 "Agent: empty msg -> 400"
T "POST" "/v1/agent/chat" @{} 400 "Agent: missing msg -> 400"

Write-Host "`n--- S6: RAG ---" -F Magenta
T "POST" "/v1/rag/search" @{query="treatment diabetes type 2";topK=5} 200 "RAG: search"

Write-Host "`n--- S7: Memory ---" -F Magenta
T "POST" "/v1/memory/upsert" @{content="Patient Nguyen Van A, 65y, diabetes type 2";kind="Episodic";source="test"} 200 "Memory: upsert"
T "POST" "/v1/memory/search" @{query="diabetes patient";topK=5} 200 "Memory: search"

Write-Host "`n--- S8: Multi-Agent ---" -F Magenta
T "POST" "/v1/multi-agent/dispatch" @{intent="scheduling";input="Schedule endocrinology appointment"} 200 "Multi-agent: dispatch"

Write-Host "`n--- S9: Subagents ---" -F Magenta
$userId = [Guid]::NewGuid()
T "POST" "/v1/subagents/fan-out" @{userId=$userId.ToString();question="Evaluate patient";specs=@(@{profile="endocrinology"})} 200 "Subagents: fan-out"

Write-Host "`n--- S10: Knowledge Graph ---" -F Magenta
T "POST" "/v1/knowledge/query" @{query="MATCH (d:Drug) RETURN d LIMIT 5"} 200 "Knowledge: query"

Write-Host "`n--- S11: Learning ---" -F Magenta
T "POST" "/v1/learning/feedback" @{conversationId=[Guid]::NewGuid().ToString();rating=5;comment="Good"} 200 "Learning: feedback"

Write-Host "`n--- S12: Shadow A/B ---" -F Magenta
T "GET" "/v1/shadow/challengers" $null 200 "Shadow: challengers"

Write-Host "`n--- S13: Adversarial ---" -F Magenta
T "GET" "/v1/adversarial/patterns" $null 200 "Adversarial: patterns"

Write-Host "`n--- S14: Approvals ---" -F Magenta
T "GET" "/v1/approvals/pending" $null 200 "Approvals: pending"

Write-Host "`n--- S15: Channels ---" -F Magenta
T "GET" "/v1/channels" $null 200 "Channels: list"

Write-Host "`n--- S16: Insights ---" -F Magenta
T "GET" "/v1/insights/summary" $null 200 "Insights: summary"

Write-Host "`n--- S17: Dashboard ---" -F Magenta
T "GET" "/v1/dashboard/overview" $null 200 "Dashboard: overview"
T "GET" "/v1/dashboard/conversations?take=3" $null 200 "Dashboard: conversations"

Write-Host "`n--- S18: Diagnostics ---" -F Magenta
T "GET" "/v1/diagnostics" $null 200 "Diagnostics: run"
T "GET" "/v1/diagnostics/context/profiles" $null 200 "Diagnostics: profiles"

Write-Host "`n--- S19: Tools ---" -F Magenta
T "GET" "/v1/tools" $null 200 "Tools: list"

Write-Host "`n--- S20: Research ---" -F Magenta
T "POST" "/v1/research/vector-search" @{query="test";collection="clinical_guidelines";topK=3} 200 "Research: vector search"

Write-Host "`n--- S21: Workflows ---" -F Magenta
T "POST" "/v1/workflows/start" @{workflowName="schedule-appointment";input=@{patientId="MRN-001"}} 200 "Workflows: start"

Write-Host "`n--- S22: Webhooks ---" -F Magenta
T "GET" "/v1/webhooks" $null 200 "Webhooks: list"

Write-Host "`n--- S23: Training ---" -F Magenta
T "GET" "/v1/training/jobs?take=3" $null 200 "Training: jobs"
T "GET" "/v1/training/preference/count" $null 200 "Training: pref count"

Write-Host "`n--- S24: Kanban ---" -F Magenta
T "GET" "/v1/kanban/boards" $null 200 "Kanban: boards"

Write-Host "`n--- S25: Migration ---" -F Magenta
T "GET" "/v1/migration/status" $null 200 "Migration: status"

Write-Host "`n--- S26: Voice ---" -F Magenta
T "POST" "/v1/voice/synthesize" @{text="Xin chao";voice="alloy"} 200 "Voice: synthesize"

Write-Host "`n--- S27: MCP ---" -F Magenta
T "GET" "/mcp" $null 403 "MCP: endpoint"

$total=$p+$f+$s
Write-Host "`n========================================" -F Cyan
Write-Host "PASS:$p FAIL:$f SKIP:$s TOTAL:$total" -F White
if($f -eq 0){Write-Host "ALL TESTS PASSED" -F Green}else{Write-Host "$f tests failed" -F Red}
exit $f
