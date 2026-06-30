# Hope.Agent — Integration Test Suite

Kiểm thử toàn bộ tính năng Phase 19-22 end-to-end.

## Cấu trúc

```
tests/
├── hope-test.ps1            # Test runner chính (PowerShell)
├── scenarios/
│   └── test-scenarios.json  # 12 kịch bản test với expected status
└── data/
    └── sample-data.json     # Dữ liệu mẫu (bệnh nhân, ICD-10, FHIR, prompts)
```

## Chạy nhanh

```powershell
# 1. Start API (terminal 1)
dotnet run --project src/Hope.Agent.Api

# 2. Chạy toàn bộ test suite (terminal 2)
.\tests\hope-test.ps1

# Chạy 1 scenario cụ thể
.\tests\hope-test.ps1 -Scenario S05

# Verbose mode
.\tests\hope-test.ps1 -Verbose

# Custom URL
.\tests\hope-test.ps1 -BaseUrl http://localhost:5000
```

## 12 Kịch bản test

| # | Scenario | Phase | Nội dung |
|---|----------|-------|----------|
| S01 | Health Probes | C-1, H-6 | `/healthz`, `/healthz/live`, `/healthz/ready`, `/healthz/startup` |
| S02 | Meta Endpoints | — | `security.txt`, OpenAPI spec |
| S03 | Authentication | H-2 | Login, refresh, JWKS, DPoP |
| S04 | Agent Chat | C-3,4,5,7, H-7 | PatientLookup, IcdSearch, multi-tool parallel, idempotency |
| S05 | FHIR Validation | H-1 | Patient, Observation validation + error cases |
| S06 | Security Shields | H-3 | Prompt injection, SQL injection, output shield |
| S07 | Rate Limiting | — | Auth brute-force → 429 |
| S08 | Diagnostics | — | `/v1/diagnostics`, `/v1/dashboard/sla` |

## Cấu hình test users

Thêm vào `appsettings.Development.json`:

```json
{
  "Auth": {
    "ServiceAccounts": [
      {
        "Username": "doctor-nguyen",
        "Password": "Hope@2026!",
        "Roles": ["doctor"],
        "TenantId": "550e8400-e29b-41d4-a716-446655440000"
      },
      {
        "Username": "admin-hoang",
        "Password": "HopeAdmin@2026!",
        "Roles": ["admin", "system"],
        "TenantId": "550e8400-e29b-41d4-a716-446655440000"
      }
    ]
  }
}
```

## Kiểm tra từng Phase thủ công

```powershell
# Phase 19 (P0) — Backup, GDPR, Billing, Cache
curl http://localhost:5080/healthz/ready        # C-1: readiness probe
curl http://localhost:5080/v1/agent/chat -H "Authorization: Bearer $TOKEN" -d '{"message":"Tìm bệnh nhân MRN-001"}'  # C-4 cache

# Phase 20 (P1) — Parallel tools, Streaming, Registry, Fallback, SIEM
curl http://localhost:5080/v1/agent/chat -d '{"message":"Tra ICD-10, kiểm tra bảo hiểm, lên lịch khám"}'  # C-5 parallel

# Phase 21 (P2) — FHIR, SSO, Eval
curl -X POST http://localhost:5080/v1/fhir/Patient -d '{"resourceType":"Patient","id":"p1","name":[{"family":"Nguyen"}]}'  # H-1

# Phase 22 (P3) — Canary deployment
kubectl get virtualservice -n hope-agent
```
