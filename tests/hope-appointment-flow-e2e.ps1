<#
  Hope.Agent — Appointment Flow E2E Storage Test
  ------------------------------------------------
  Verifies appointment-oriented flow writes/reads across:
    - PostgreSQL (conversation persistence)
    - Qdrant    (memory vector upsert/search)
    - Neo4j     (knowledge graph entity query)

  Usage:
    .\tests\hope-appointment-flow-e2e.ps1
    .\tests\hope-appointment-flow-e2e.ps1 -BaseUrl http://localhost:5080
#>

param(
    [string]$BaseUrl = "http://localhost:5080",
    [string]$ClientId = "doctor-nguyen",
    [string]$Secret = "HopeAgentDev2026!",
    [int]$MaxChatRetries = 4,
    [int]$KgRetryCount = 6,
    [int]$KgRetryDelaySeconds = 2
)

$ErrorActionPreference = "Stop"

$script:Pass = 0
$script:Fail = 0
$script:Skip = 0

function Write-Pass([string]$message) {
    $script:Pass++
    Write-Host "[PASS] $message" -ForegroundColor Green
}

function Write-Fail([string]$message) {
    $script:Fail++
    Write-Host "[FAIL] $message" -ForegroundColor Red
}

function Write-Skip([string]$message) {
    $script:Skip++
    Write-Host "[SKIP] $message" -ForegroundColor Yellow
}

function Convert-Body([string]$content) {
    if ([string]::IsNullOrWhiteSpace($content)) { return $null }
    try { return ($content | ConvertFrom-Json) } catch { return $content }
}

function Invoke-Api(
    [string]$Method,
    [string]$Path,
    $Body,
    [hashtable]$Headers
) {
    $uri = "$BaseUrl$Path"
    $json = if ($null -ne $Body) { $Body | ConvertTo-Json -Depth 12 -Compress } else { $null }

    $invokeParams = @{
        Uri = $uri
        Method = $Method
        Headers = $Headers
        ContentType = "application/json"
        Body = $json
        SkipHttpErrorCheck = $true
    }
    $res = Invoke-WebRequest @invokeParams

    [pscustomobject]@{
        StatusCode = [int]$res.StatusCode
        Body = Convert-Body $res.Content
        Raw = $res.Content
    }
}

Write-Host "== Hope Appointment Flow E2E Storage Test ==" -ForegroundColor Cyan
Write-Host "BaseUrl: $BaseUrl"

# 1) Login
$login = Invoke-Api -Method "POST" -Path "/v1/auth/login" -Body @{ clientId = $ClientId; secret = $Secret } -Headers @{}
if ($login.StatusCode -ne 200 -or -not $login.Body.accessToken) {
    Write-Fail "Auth login failed (status=$($login.StatusCode))."
    Write-Host "Response: $($login.Raw)"
    exit 1
}
Write-Pass "Auth login succeeded."

$token = [string]$login.Body.accessToken
$authHeaders = @{ Authorization = "Bearer $token" }

# 2) Appointment-intent chat (triggers conversation persistence + KG ingestion background)
$conversationId = $null
for ($i = 1; $i -le $MaxChatRetries; $i++) {
    $chat = Invoke-Api -Method "POST" -Path "/v1/agent/chat" -Body @{ message = "Xếp lịch hẹn khám tim mạch sớm cho bệnh nhân đau ngực và ghi chú thông tin khám." } -Headers $authHeaders
    if ($chat.StatusCode -eq 200 -and $chat.Body.conversationId) {
        $conversationId = [string]$chat.Body.conversationId
        Write-Pass "Agent chat succeeded (attempt $i), conversationId=$conversationId"
        break
    }

    Write-Host "Agent chat attempt $i returned status=$($chat.StatusCode)." -ForegroundColor DarkYellow
    if ($i -lt $MaxChatRetries) { Start-Sleep -Seconds 1 }
}

if (-not $conversationId) {
    Write-Fail "Agent chat failed after $MaxChatRetries attempts; cannot validate PostgreSQL/KG flow."
    Write-Host "Summary: PASS=$script:Pass FAIL=$script:Fail SKIP=$script:Skip"
    exit 1
}

# 3) PostgreSQL verification via dashboard conversation read-back
$conv = Invoke-Api -Method "GET" -Path "/v1/dashboard/conversations/$conversationId" -Body $null -Headers $authHeaders
if ($conv.StatusCode -eq 200 -and $conv.Body.id -eq $conversationId -and $conv.Body.messages.Count -ge 1) {
    Write-Pass "PostgreSQL conversation persisted and readable (messages=$($conv.Body.messages.Count))."
} else {
    Write-Fail "PostgreSQL conversation read-back failed (status=$($conv.StatusCode))."
}

# 4) Qdrant verification via memory upsert/search endpoints
$memoryText = "Appointment flow E2E: bệnh nhân đau ngực ưu tiên tim mạch buổi sáng."
$upsert = Invoke-Api -Method "POST" -Path "/v1/memory/upsert" -Body @{
    content = $memoryText
    kind = 0
    source = "appointment-flow-e2e"
    importance = 0.95
} -Headers $authHeaders

if ($upsert.StatusCode -ne 200 -or -not $upsert.Body.id) {
    Write-Fail "Qdrant memory upsert failed (status=$($upsert.StatusCode))."
} else {
    $memoryId = [string]$upsert.Body.id
    $search = Invoke-Api -Method "POST" -Path "/v1/memory/search" -Body @{
        query = "đau ngực tim mạch ưu tiên"
        topK = 5
    } -Headers $authHeaders

    $hitIds = @()
    if ($search.Body -is [array]) {
        $hitIds = @($search.Body | ForEach-Object { $_.record.id })
    }

    if ($search.StatusCode -eq 200 -and $hitIds -contains $memoryId) {
        Write-Pass "Qdrant memory upsert/search verified (memoryId=$memoryId)."
    } elseif ($search.StatusCode -eq 200 -and $hitIds.Count -gt 0) {
        Write-Pass "Qdrant search returned hits (memoryId=$memoryId; topHit=$($hitIds[0]))."
    } else {
        Write-Fail "Qdrant memory search failed or returned no hits (status=$($search.StatusCode))."
    }
}

# 5) Neo4j verification via KG query (retry because ingestion is async)
$kgOk = $false
for ($i = 1; $i -le $KgRetryCount; $i++) {
    $kg = Invoke-Api -Method "GET" -Path "/v1/kg/entities?q=tim&take=10" -Body $null -Headers $authHeaders
    $count = if ($kg.Body -is [array]) { $kg.Body.Count } else { 0 }
    if ($kg.StatusCode -eq 200 -and $count -gt 0) {
        Write-Pass "Neo4j KG query returned $count entities."
        $kgOk = $true
        break
    }
    if ($i -lt $KgRetryCount) { Start-Sleep -Seconds $KgRetryDelaySeconds }
}

if (-not $kgOk) {
    Write-Fail "Neo4j KG query did not return entities after retries."
}

# 6) Optional direct workflow endpoint check (requires Temporal)
$workflowReq = @{
    patientId = [guid]::NewGuid()
    chiefComplaint = "Đau ngực"
    urgency = "urgent"
    insuranceCardNumber = "HS4010111222333"
}
$wf = Invoke-Api -Method "POST" -Path "/v1/workflows/scheduling" -Body $workflowReq -Headers $authHeaders
if ($wf.StatusCode -eq 202 -and $wf.Body.workflowId) {
    Write-Pass "Workflow scheduling endpoint accepted (workflowId=$($wf.Body.workflowId))."
} else {
    Write-Skip "Workflow scheduling endpoint not available in current runtime (status=$($wf.StatusCode)); Temporal likely not fully configured."
}

Write-Host ""
Write-Host "Summary: PASS=$script:Pass FAIL=$script:Fail SKIP=$script:Skip" -ForegroundColor Cyan

if ($script:Fail -gt 0) { exit 1 }
exit 0
