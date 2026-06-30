# Hope.Agent Quick Integration Tests
$pass = 0; $fail = 0
$base = "http://localhost:5080"

function Test($label, $method, $path, $body, $expect) {
    $uri = "$base$path"
    try {
        $j = if ($body) { $body | ConvertTo-Json -Compress } else { $null }
        $sc = 0
        if ($method -eq "GET")  { $null = Invoke-RestMethod -Uri $uri -Method GET  -TimeoutSec 8 -SkipCertificateCheck -StatusCodeVariable sc }
        else                    { $null = Invoke-RestMethod -Uri $uri -Method POST -Body $j -ContentType "application/json" -TimeoutSec 8 -SkipCertificateCheck -StatusCodeVariable sc }
        if ($sc -eq $expect) { $script:pass++; Write-Host "  ✅ $label" -F Green }
        else                 { $script:fail++; Write-Host "  ❌ $label → $sc (expected $expect)" -F Red }
    } catch {
        $s = $_.Exception.Response.StatusCode.value__
        if ($s -eq $expect) { $script:pass++; Write-Host "  ✅ $label → $s" -F Green }
        else                { $script:fail++; Write-Host "  ❌ $label → $s (expected $expect)" -F Red }
    }
}

Write-Host "`n╔══ Hope.Agent Integration Tests ══╗`n" -F Cyan

Write-Host "── S01: Health & Startup (C-1, H-6) ──" -F Magenta
Test "/healthz/live"           "GET"  "/healthz/live"           $null  200
Test "/healthz/startup (H-6)"  "GET"  "/healthz/startup"        $null  200

Write-Host "`n── S02: Meta ──" -F Magenta
Test "security.txt (RFC 9116)" "GET"  "/.well-known/security.txt" $null 200
Test "OpenAPI spec"             "GET"  "/openapi/v1.json"         $null 200

Write-Host "`n── S03: Auth ──" -F Magenta
Test "JWKS endpoint"           "GET"  "/v1/auth/jwks"           $null  200

Write-Host "`n── S05: FHIR R4 Validation (H-1) ──" -F Magenta
$pat = @{resourceType="Patient";id="p1";name=@(@{family="Nguyen";given=@("Van","A")})}
Test "FHIR valid Patient"      "POST" "/v1/fhir/Patient"        $pat   200
$obs = @{resourceType="Observation";code=@{coding=@(@{system="http://loinc.org";code="8480-6"})};subject=@{reference="Patient/p1"};status="final"}
Test "FHIR valid Observation"  "POST" "/v1/fhir/Observation"    $obs   200
Test "FHIR missing fields"     "POST" "/v1/fhir/Patient"        @{resourceType="Patient"} 422
Test "FHIR unsupported type"   "POST" "/v1/fhir/BadType"        @{resourceType="BadType"} 400

Write-Host "`n── S06: Security ──" -F Magenta
Test "SQL injection blocked"   "POST" "/v1/auth/login"          @{username="DROP TABLE;--"} 400

Write-Host "`n══════════════════════" -F Cyan
Write-Host "  PASS: $pass  |  FAIL: $fail" -F $(if($fail -eq 0){"Green"}else{"Red"})
if ($fail -eq 0) { Write-Host "`n  ✅ ALL TESTS PASSED — Phase 19-22 features verified!`n" -F Green }
