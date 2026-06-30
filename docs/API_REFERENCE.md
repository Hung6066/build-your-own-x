# Hope.Agent — API Reference Toàn Diện

> **Tài liệu số 3/3** — Bảng chi tiết tất cả Endpoint: Input, Output, Authentication, Rate Limit  
> **Ngày**: 2026-06-03 | **Base URL**: `http://localhost:5000` (Gateway) / `http://localhost:5080` (API)

---

## 1. Authentication Overview

| Scheme          | Header                               | Sử dụng                              |
| --------------- | ------------------------------------ | ------------------------------------ |
| **JWT Bearer**  | `Authorization: Bearer <token>`      | Tất cả endpoint (trừ public)         |
| **API Key**     | `X-Api-Key: <key>`                   | Chỉ MCP endpoint (song song với JWT) |
| **HMAC-SHA256** | `X-Hope-Signature-256: sha256=<hex>` | Webhook từ HIS/EMR                   |

**Token Lifecycle**:
| Token | Lifetime | Rotation |
|-------|----------|----------|
| Access Token | 5 phút (configurable) | Không (stateless JWT) |
| Refresh Token | 7 ngày (configurable) | Single-use + family revocation |

**JWT Claims**:

```json
{
  "sub": "user-guid",
  "client_id": "service-account-name",
  "roles": ["admin", "clinician"],
  "scope": "hope-agent:mcp hope-agent:docs",
  "iss": "hope.agent",
  "aud": "hope.agent.api",
  "exp": 1717000000
}
```

**Rate Limit Headers** (trên mọi response):

```
X-RateLimit-Limit: 120
X-RateLimit-Remaining: 118
X-RateLimit-Reset: 1717000060
Retry-After: 30
```

---

## 2. Endpoint Catalog

### 2.1 Agent — `/v1/agent`

| Method | Path             | Auth | Rate Limit                                 | Body Size | Idempotency |
| ------ | ---------------- | ---- | ------------------------------------------ | --------- | ----------- |
| POST   | `/v1/agent/chat` | JWT  | agent-concurrency (3 concurrent + 5 queue) | 64 KB     | Không       |

**Input**: `AgentChatRequest`

```json
{
  "message": "Bệnh nhân Nguyễn Văn A cần đặt lịch khám tim mạch",
  "conversationId": "guid (optional)",
  "context": { "key": "value" }
}
```

**Validation**:

- `message`: Required, 1-8000 chars, Unicode letters/numbers/punctuation only
- Input sanitization: Chặn `DROP TABLE`, `DELETE FROM`, `'; --`

**Output**: `AgentResponse`

```json
{
  "conversationId": "550e8400-e29b-41d4-a716-446655440000",
  "reply": "Đã đặt lịch khám tim mạch cho bệnh nhân...",
  "toolExecutions": [
    {
      "tool": "PatientLookup",
      "argumentsJson": "{\"mrn\":\"...\"}",
      "result": "...",
      "duration": "00:00:00.500",
      "success": true
    }
  ],
  "promptTokens": 1500,
  "completionTokens": 300,
  "provider": "openai",
  "model": "gpt-4o-mini",
  "duration": "00:00:02.500",
  "costUsd": 0.000345
}
```

---

### 2.2 Auth — `/v1/auth`

| Method | Path                     | Auth | Rate Limit               | Mô tả                           |
| ------ | ------------------------ | ---- | ------------------------ | ------------------------------- |
| POST   | `/v1/auth/login`         | None | auth-login (10/min/IP)   | Đổi client credential lấy token |
| POST   | `/v1/auth/refresh`       | None | auth-refresh (60/min/IP) | Refresh access token            |
| POST   | `/v1/auth/revoke`        | None | auth-refresh (60/min/IP) | Thu hồi refresh token           |
| GET    | `/.well-known/jwks.json` | None | Không                    | JSON Web Key Set (RS256 only)   |

**POST /login Input**:

```json
{
  "clientId": "hope-clinic-app",
  "secret": "at-least-16-characters-secret"
}
```

**POST /login Output**:

```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIs...",
  "refreshToken": "opaque-token-string",
  "expiresIn": 300,
  "tokenType": "Bearer"
}
```

**POST /refresh Input**:

```json
{
  "refreshToken": "opaque-token-string"
}
```

**POST /revoke Input**:

```json
{
  "refreshToken": "opaque-token-string"
}
```

**Output**: 204 No Content (always — prevents token oracle)

**Security**:

- Constant-time secret comparison (chống timing attack)
- SHA-256 hashed secrets trong config
- Deterministic UserId từ ClientId (SHA-256 → UUID v4)
- Single-use refresh token + family revocation (Auth0/Stripe pattern)

---

### 2.3 Workflows — `/v1/workflows`

| Method | Path                                   | Auth | Rate Limit | Body Size | Idempotency |
| ------ | -------------------------------------- | ---- | ---------- | --------- | ----------- |
| POST   | `/v1/workflows/admissions`             | JWT  | Global     | 64 KB     | ✅          |
| POST   | `/v1/workflows/triage`                 | JWT  | Global     | 64 KB     | ✅          |
| POST   | `/v1/workflows/scheduling`             | JWT  | Global     | 64 KB     | ✅          |
| POST   | `/v1/workflows/reminders`              | JWT  | Global     | 64 KB     | ✅          |
| POST   | `/v1/workflows/reminders/{id}/confirm` | JWT  | Global     | 64 KB     | ✅          |
| POST   | `/v1/workflows/audit`                  | JWT  | Global     | 64 KB     | ✅          |
| POST   | `/v1/workflows/{id}/signal`            | JWT  | Global     | 64 KB     | ✅          |
| POST   | `/v1/workflows/{id}/cancel`            | JWT  | Global     | 64 KB     | ✅          |
| GET    | `/v1/workflows/{id}`                   | JWT  | Global     | —         | —           |

**POST /admissions Input**:

```json
{
  "patientId": "guid",
  "reason": "Đau ngực cấp tính",
  "insuranceProvider": "BHYT-TP.HCM",
  "preferredDoctorId": "DR-001",
  "priority": 3,
  "workflowId": "optional-custom-id"
}
```

**POST /triage Input**:

```json
{
  "patientId": "guid",
  "symptoms": "Đau ngực, khó thở, huyết áp cao",
  "location": "Khoa Cấp Cứu - Tầng 1",
  "workflowId": "optional-custom-id"
}
```

**POST /scheduling Input**:

```json
{
  "patientId": "guid",
  "chiefComplaint": "Khám tim mạch định kỳ",
  "urgency": "normal",
  "preferredDoctorId": "DR-001",
  "preferredTime": "2026-06-10T08:00:00+07:00",
  "insuranceCardNumber": "BHYT-123456",
  "workflowId": null
}
```

**POST /reminders Input**:

```json
{
  "patientId": "guid",
  "medicationName": "Aspirin 81mg",
  "dosage": "1 viên",
  "frequency": "Sáng sau ăn",
  "durationDays": 30,
  "startAt": "2026-06-04T07:00:00+07:00",
  "preferredChannel": "zalo",
  "adherenceRiskScore": 30,
  "workflowId": null
}
```

**POST /reminders/{id}/confirm Input**:

```json
{
  "confirmed": true,
  "note": "Đã uống thuốc sáng nay"
}
```

**POST /audit Input**:

```json
{
  "reportType": "monthly-compliance",
  "periodStart": "2026-05-01T00:00:00+07:00",
  "periodEnd": "2026-05-31T23:59:59+07:00",
  "exportFormat": "pdf"
}
```

**POST /{id}/signal Input**:

```json
{
  "step": "verify_insurance",
  "approved": true,
  "reason": "Đã xác nhận BHYT còn hiệu lực"
}
```

**GET /{id} Output**:

```json
{
  "workflowId": "patient-admission-abc123",
  "runId": "run-xyz789",
  "status": "Running",
  "currentStep": "assign_bed",
  "startedAt": "2026-06-03T10:00:00Z",
  "lastHeartbeatAt": "2026-06-03T10:05:00Z"
}
```

---

### 2.4 RAG — `/v1/rag`

| Method | Path                     | Auth | Body Size | Idempotency |
| ------ | ------------------------ | ---- | --------- | ----------- |
| POST   | `/v1/rag/documents`      | JWT  | 512 KB    | ✅          |
| GET    | `/v1/rag/documents/{id}` | JWT  | —         | —           |
| POST   | `/v1/rag/search`         | JWT  | 64 KB     | ✅          |

**POST /documents Input**:

```json
{
  "title": "Hướng dẫn điều trị tăng huyết áp 2025",
  "content": "Toàn văn tài liệu...",
  "collection": "clinical_guidelines",
  "source": "manual",
  "url": "https://moh.gov.vn/guidelines/hypertension-2025",
  "metadata": { "specialty": "cardiology", "version": "3.2" },
  "async": false
}
```

**POST /documents Output** (sync):

```json
{
  "documentId": "guid",
  "chunks": 15,
  "collection": "clinical_guidelines",
  "status": "ingested"
}
```

**POST /search Input**:

```json
{
  "query": "phác đồ điều trị tăng huyết áp giai đoạn 2",
  "collection": "clinical_guidelines",
  "topK": 8,
  "finalK": 4,
  "metadataFilter": { "specialty": "cardiology" },
  "rerank": true
}
```

**POST /search Output**:

```json
[
  {
    "documentId": "guid",
    "chunkIndex": 3,
    "content": "Đoạn văn bản liên quan...",
    "score": 0.92,
    "metadata": { "specialty": "cardiology" }
  }
]
```

---

### 2.5 Memory — `/v1/memory`

| Method | Path                | Auth                               | Body Size | Idempotency |
| ------ | ------------------- | ---------------------------------- | --------- | ----------- |
| POST   | `/v1/memory/upsert` | JWT + TenantAccess + PatientAccess | 64 KB     | ✅          |
| POST   | `/v1/memory/search` | JWT + TenantAccess + PatientAccess | 64 KB     | ✅          |

**POST /upsert Input**:

```json
{
  "content": "Bệnh nhân dị ứng với Penicillin",
  "kind": "Semantic",
  "userId": null,
  "conversationId": "guid",
  "source": "clinician_note",
  "importance": 0.8
}
```

**POST /search Input**:

```json
{
  "query": "dị ứng thuốc",
  "topK": 5,
  "kind": "Semantic",
  "userId": null
}
```

---

### 2.6 Multi-Agent — `/v1/multi-agent`

| Method | Path                       | Auth | Body Size | Idempotency |
| ------ | -------------------------- | ---- | --------- | ----------- |
| POST   | `/v1/multi-agent/dispatch` | JWT  | 64 KB     | ✅          |

**Input**:

```json
{
  "intent": "scheduling",
  "input": "Đặt lịch khám cho bệnh nhân Nguyễn Văn A",
  "context": { "patientId": "guid" },
  "conversationId": null,
  "priority": 5
}
```

**Output**: `SubagentDispatchResult`

```json
{
  "taskId": "guid",
  "status": "Completed",
  "results": [
    {
      "profile": "scheduling",
      "answer": "Đã đặt lịch khám vào 10/06/2026 lúc 08:00",
      "durationMs": 2500
    }
  ]
}
```

---

### 2.7 Knowledge Graph — `/v1/kg`

| Method | Path                                | Auth |
| ------ | ----------------------------------- | ---- |
| GET    | `/v1/kg/entities?q={query}&take=20` | JWT  |
| GET    | `/v1/kg/neighbors/{id}?depth=1`     | JWT  |

---

### 2.8 Learning & Evaluation — `/v1/learning`

| Method | Path                                                  | Auth |
| ------ | ----------------------------------------------------- | ---- |
| POST   | `/v1/learning/feedback`                               | JWT  |
| GET    | `/v1/learning/feedback/{conversationId}`              | JWT  |
| GET    | `/v1/learning/eval/runs`                              | JWT  |
| POST   | `/v1/learning/eval/run?suite=default`                 | JWT  |
| GET    | `/v1/learning/eval/trend?suite=default&days=30`       | JWT  |
| GET    | `/v1/learning/eval/leaderboard?suite=default&take=20` | JWT  |
| POST   | `/v1/learning/eval/tournament?suite=default`          | JWT  |
| GET    | `/v1/learning/eval/cases?suite=default`               | JWT  |
| POST   | `/v1/learning/eval/cases`                             | JWT  |
| DELETE | `/v1/learning/eval/cases/{id}`                        | JWT  |

**POST /feedback Input**:

```json
{
  "conversationId": "guid",
  "rating": 1,
  "comment": "Chẩn đoán chính xác",
  "provider": "openai",
  "model": "gpt-4o-mini",
  "intent": "scheduling"
}
```

(Rating: 1=thumbs up, -1=thumbs down)

**POST /eval/cases Input**:

```json
{
  "suite": "clinical-qa",
  "name": "Tăng huyết áp giai đoạn 2",
  "userMessage": "Bệnh nhân 55 tuổi, HA 160/100, tiền sử...",
  "referenceAnswer": "Cần kê Amlodipine 5mg...",
  "tags": ["cardiology", "hypertension"]
}
```

---

### 2.9 Shadow A/B — `/v1/learning`

| Method | Path                                   | Auth |
| ------ | -------------------------------------- | ---- |
| POST   | `/v1/learning/challengers`             | JWT  |
| GET    | `/v1/learning/challengers/{intent}`    | JWT  |
| GET    | `/v1/learning/shadow/{intent}?take=50` | JWT  |

**POST /challengers Input**:

```json
{
  "intent": "scheduling",
  "challengerProvider": "ollama",
  "trafficFraction": 0.1,
  "minSamples": 50,
  "promotionWinRate": 0.55
}
```

---

### 2.10 Security — `/v1/security`

| Method | Path                                        | Auth |
| ------ | ------------------------------------------- | ---- |
| GET    | `/v1/security/approvals/pending?take=100`   | JWT  |
| GET    | `/v1/security/approvals?from=&to=&take=100` | JWT  |
| POST   | `/v1/security/approvals/{id}/approve`       | JWT  |
| POST   | `/v1/security/approvals/{id}/deny`          | JWT  |
| GET    | `/v1/security/adversarial?take=100`         | JWT  |
| POST   | `/v1/security/adversarial/{id}/promote`     | JWT  |
| POST   | `/v1/security/adversarial/{id}/demote`      | JWT  |

**POST /approvals/{id}/approve Input**:

```json
{
  "reason": "Đã xác minh, cho phép thực thi"
}
```

---

### 2.11 Channels — `/v1/channels`

| Method | Path                        | Auth                      | Body Size |
| ------ | --------------------------- | ------------------------- | --------- |
| POST   | `/v1/channels/zalo/webhook` | HMAC (X-ZEvent-Signature) | 128 KB    |
| POST   | `/v1/channels/slack/events` | HMAC (X-Slack-Signature)  | 128 KB    |

**Zalo Webhook Verification**:

- Header: `X-ZEvent-Signature: sha256=<hmac-of-body>`
- Secret: `Zalo:AppSecret` từ config
- Whitelist: `Zalo:AllowedSenderIds`

**Slack Events Verification**:

- Header: `X-Slack-Request-Timestamp` + `X-Slack-Signature`
- Version string: `v0:{timestamp}:{body}`
- Timestamp skew: ±300s (configurable)
- URL verification: Trả về `challenge` string nếu `type=url_verification`
- Anti-loop: Bỏ qua messages có `bot_id` hoặc `subtype`

---

### 2.12 Webhooks — `/v1/webhooks`

| Method | Path                  | Auth                        | Body Size |
| ------ | --------------------- | --------------------------- | --------- |
| POST   | `/v1/webhooks/events` | HMAC (X-Hope-Signature-256) | 256 KB    |

**Security Layers**:

1. **Timestamp Check** (±30s): `X-Hope-Timestamp: <unix-seconds>`
2. **HMAC Validation**: SHA-256(`{timestamp}.{body}`, secret)
3. **Nonce Dedup**: Redis SET NX với TTL = 2× timestamp tolerance

**Supported Events**:
| Event | Workflow Triggered |
|-------|-------------------|
| `patient.emergency_admission` | `StartEmergencyTriageAsync` |
| `patient.admission` | `StartPatientAdmissionAsync` |

**Input**:

```json
{
  "event": "patient.admission",
  "payload": {
    "patient_id": "guid",
    "reason": "Đau ngực cấp",
    "insurance": "BHYT-TP.HCM",
    "doctor_id": "DR-001",
    "priority": "3"
  }
}
```

---

### 2.13 Training — `/v1/training`

| Method | Path                                                        | Auth |
| ------ | ----------------------------------------------------------- | ---- |
| POST   | `/v1/training/export`                                       | JWT  |
| POST   | `/v1/training/export/dpo`                                   | JWT  |
| POST   | `/v1/training/preference`                                   | JWT  |
| GET    | `/v1/training/preference?since=&until=&specialty=&take=100` | JWT  |
| GET    | `/v1/training/preference/count?since=`                      | JWT  |
| POST   | `/v1/training/jobs`                                         | JWT  |
| GET    | `/v1/training/jobs?take=20`                                 | JWT  |
| GET    | `/v1/training/jobs/{id}`                                    | JWT  |
| POST   | `/v1/training/jobs/{id}/refresh`                            | JWT  |
| DELETE | `/v1/training/jobs/{id}`                                    | JWT  |
| POST   | `/v1/training/champion`                                     | JWT  |

**POST /export Input**:

```json
{
  "since": "2026-05-01T00:00:00Z",
  "until": "2026-06-01T00:00:00Z",
  "userId": null,
  "maxConversations": 10000,
  "minTurns": 2,
  "redactPhi": true
}
```

**Output**: `application/x-ndjson` stream (JSONL file download)

**POST /preference Input**:

```json
{
  "conversationId": "guid",
  "prompt": "Bệnh nhân có triệu chứng X...",
  "chosenResponse": "Chẩn đoán A...",
  "rejectedResponse": "Chẩn đoán B...",
  "chosenProvider": "openai",
  "rejectedProvider": "ollama",
  "rationale": "Chẩn đoán A chính xác hơn dựa trên guideline",
  "specialty": "cardiology"
}
```

**POST /jobs Input**:

```json
{
  "jobType": "Dpo",
  "baseModel": "Qwen2.5-7B-Instruct",
  "outputModelTag": "hope-clinical-v2",
  "dataSince": "2026-03-01T00:00:00Z",
  "maxRecords": 5000
}
```

**POST /champion Input** (Python training callback):

```json
{
  "tag": "hope-clinical-v2",
  "specialty": "cardiology",
  "elo": 1250
}
```

---

### 2.14 Subagents — `/v1/subagents`

| Method | Path                    | Auth |
| ------ | ----------------------- | ---- |
| POST   | `/v1/subagents/fan-out` | JWT  |

**Input**:

```json
{
  "userId": "guid",
  "question": "Tổng hợp thông tin bệnh nhân",
  "specs": [
    { "profile": "scheduling", "systemPromptHint": "Ưu tiên bác sĩ tim mạch" },
    { "profile": "insurance", "systemPromptHint": null }
  ],
  "correlationId": "optional"
}
```

---

### 2.15 Voice — `/v1/voice`

| Method | Path                   | Auth | Body Size                           |
| ------ | ---------------------- | ---- | ----------------------------------- |
| POST   | `/v1/voice/transcribe` | JWT  | multipart/form-data (no hard limit) |
| POST   | `/v1/voice/synthesize` | JWT  | 64 KB                               |

**POST /transcribe** (multipart/form-data):

- Field `file`: Audio file (WAV, MP3, WebM)
- Field `language`: Optional (e.g., `vi`, `en`)
- Output: `{ "text": "Bệnh nhân đau ngực...", "language": "vi", "durationMs": 1234 }`

**POST /synthesize Input**:

```json
{
  "text": "Xin chào, bạn có cần đặt lịch khám không?",
  "voice": "vi-VN-Neural2-A"
}
```

**Output**: `audio/mpeg` binary (MP3 file)

---

### 2.16 Research — `/v1/research`

| Method | Path           | Auth |
| ------ | -------------- | ---- |
| POST   | `/v1/research` | JWT  |

**Input**:

```json
{
  "query": "Latest clinical trials for hypertension treatment 2025",
  "mode": "max",
  "maxResults": 10
}
```

- Fast mode: Gemini 2.5 Flash (~10s)
- Max mode: Gemini 2.5 Pro + 3-phase plan→search→synthesise (~60s)
- Requires `LLM:Gemini:ApiKey`

---

### 2.17 Dashboard — `/v1/dashboard`

| Method | Path                                          | Auth |
| ------ | --------------------------------------------- | ---- |
| GET    | `/v1/dashboard/overview`                      | JWT  |
| GET    | `/v1/dashboard/conversations?userId=&take=50` | JWT  |
| GET    | `/v1/dashboard/conversations/{id}`            | JWT  |
| GET    | `/v1/dashboard/audit?take=100`                | JWT  |
| GET    | `/v1/dashboard/skills?take=50`                | JWT  |

**GET /overview Output**:

```json
{
  "window_days": 7,
  "conversations_7d": 1234,
  "messages_7d": 5678,
  "pending_approvals": 5,
  "learned_skills": 42,
  "active_adversarial_patterns": 15
}
```

---

### 2.18 Insights — `/v1/insights`

| Method | Path                                                | Auth |
| ------ | --------------------------------------------------- | ---- |
| GET    | `/v1/insights?userId=guid&days=7`                   | JWT  |
| GET    | `/v1/insights/search?userId=guid&q=keyword&take=20` | JWT  |
| POST   | `/v1/insights/generate`                             | JWT  |

**POST /generate Input**:

```json
{
  "userId": "guid",
  "periodStart": "2026-05-01T00:00:00Z",
  "periodEnd": "2026-06-01T00:00:00Z"
}
```

---

### 2.19 Kanban — `/v1/kanban`

| Method | Path                                                          | Auth |
| ------ | ------------------------------------------------------------- | ---- |
| GET    | `/v1/kanban?userId=&column=&patientRef=&assignedTo=&take=100` | JWT  |
| GET    | `/v1/kanban/{id}`                                             | JWT  |
| POST   | `/v1/kanban`                                                  | JWT  |
| PATCH  | `/v1/kanban/{id}`                                             | JWT  |
| DELETE | `/v1/kanban/{id}`                                             | JWT  |

**POST /kanban Input**:

```json
{
  "title": "Xét nghiệm máu cho bệnh nhân A",
  "description": "CBC, HbA1c, Lipid panel",
  "userId": "guid",
  "conversationId": null,
  "patientRef": "PAT-001",
  "column": "Todo",
  "priority": "High",
  "dueAt": "2026-06-05T17:00:00+07:00",
  "assignedTo": "nurse-001",
  "tags": "lab,urgent"
}
```

**PATCH /kanban/{id} Input** (tất cả field optional):

```json
{
  "column": "InProgress",
  "assignedTo": "nurse-002"
}
```

---

### 2.20 Migration — `/v1/migrate`

| Method | Path          | Auth | Body                |
| ------ | ------------- | ---- | ------------------- |
| POST   | `/v1/migrate` | JWT  | multipart/form-data |

**Form Fields**:
| Field | Type | Required | Mô tả |
|-------|------|----------|-------|
| `source` | string | ✅ | `DialogflowFaq`, `Rasa`, `GenericFaq` |
| `file` | file | ✅ | JSON/ZIP export từ source |
| `dryRun` | string | ❌ | `"true"` để preview |
| `intent` | string | ❌ | Gán intent mặc định |

---

### 2.21 Tools — `/v1/tools`

| Method | Path               | Auth          |
| ------ | ------------------ | ------------- |
| GET    | `/v1/tools`        | None (public) |
| GET    | `/v1/tools/{name}` | None (public) |

**GET /tools Output** (OpenAI function-call schema):

```json
{
  "tools": [
    {
      "type": "function",
      "function": {
        "name": "PatientLookup",
        "description": "Look up patient by MRN or full name",
        "parameters": {
          "type": "object",
          "properties": {
            "mrn": { "type": "string", "description": "Medical Record Number" },
            "name": { "type": "string", "description": "Patient full name" }
          },
          "required": []
        }
      }
    }
  ]
}
```

---

### 2.22 MCP — `/mcp`

| Method | Path   | Auth                                      | Rate Limit   |
| ------ | ------ | ----------------------------------------- | ------------ |
| ALL    | `/mcp` | JWT (scope:`hope-agent:mcp`) hoặc API Key | mcp (30/min) |

Đây là endpoint Model Context Protocol, cho phép external clients (như VS Code, Cursor) khám phá và gọi tools của Hope.Agent.

---

### 2.23 Diagnostics — `/v1/diagnostics`

| Method | Path                               | Auth | Rate Limit |
| ------ | ---------------------------------- | ---- | ---------- |
| GET    | `/v1/diagnostics`                  | JWT  | 20/min     |
| GET    | `/v1/diagnostics/context?profile=` | JWT  | 20/min     |
| GET    | `/v1/diagnostics/context/profiles` | JWT  | 20/min     |

---

## 3. Health Checks

| Method | Path             | Auth | Mô tả                              |
| ------ | ---------------- | ---- | ---------------------------------- |
| GET    | `/healthz`       | None | Liveness (luôn OK nếu app running) |
| GET    | `/healthz/live`  | None | Liveness probe (K8s)               |
| GET    | `/healthz/ready` | None | Readiness probe (Postgres + Redis) |

---

## 4. Meta Endpoints

| Method | Path                        | Auth                                     | Mô tả                       |
| ------ | --------------------------- | ---------------------------------------- | --------------------------- |
| GET    | `/.well-known/security.txt` | None                                     | RFC 9116 — security contact |
| GET    | `/.well-known/jwks.json`    | None                                     | JWK Set (RS256 public keys) |
| GET    | `/openapi/v1.json`          | Dev: None / Prod: `OpenApiAccess` policy | OpenAPI specification       |

---

## 5. Common Response Codes

| Code | Mô tả                               |
| ---- | ----------------------------------- |
| 200  | Success                             |
| 201  | Created                             |
| 202  | Accepted (async workflow started)   |
| 204  | No Content                          |
| 400  | Bad Request (validation error)      |
| 401  | Unauthorized (missing/invalid JWT)  |
| 403  | Forbidden (insufficient role/scope) |
| 404  | Not Found                           |
| 409  | Conflict (idempotency replay)       |
| 422  | Unprocessable Entity                |
| 429  | Too Many Requests (rate limited)    |
| 500  | Internal Server Error               |

**Error Response Format** (`application/problem+json`):

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Bad Request",
  "status": 400,
  "detail": "Message is required.",
  "correlationId": "0HN4OJT5KGQ7H:00000001"
}
```

---

## 6. Idempotency

Các endpoint đánh dấu ✅ hỗ trợ idempotency qua header:

```
Idempotency-Key: <client-generated-uuid>
```

Khi gửi lại request với cùng `Idempotency-Key`, server trả về kết quả đã cached (không thực thi lại). Response sẽ có header:

```
Idempotent-Replayed: true
```

---

## 7. Versioning

Tất cả endpoint đều prefix `/v1/`. API version được enforce bởi `ApiVersionGuardMiddleware`. Các version tương lai sẽ có prefix `/v2/`, `/v3/`, v.v.

---

_Tài liệu được tạo tự động từ source code Hope.Agent — 2026-06-03_
