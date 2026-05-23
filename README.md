# Hope.Agent — Enterprise Healthcare AI Agent Platform

Production-grade, multi-provider AI agent platform on **.NET 9** with Clean Architecture, designed for clinical operations. Phase 1 MVP of the [agent_plan.md](agent_plan.md) roadmap.

## Stack at a glance

| Layer         | Tech                                                               |
| ------------- | ------------------------------------------------------------------ |
| API edge      | YARP reverse proxy + JWT + rate limiter                            |
| Agent runtime | Custom orchestrator (planner → tool-call → reflect loop)           |
| LLM           | Multi-provider: OpenAI / Qwen (vLLM) / Anthropic / Gemini / Ollama |
| Memory        | PostgreSQL (episodic) + Qdrant (vector) + Redis (cache)            |
| Eventing      | Kafka (idempotent producer, zstd)                                  |
| Observability | OpenTelemetry → Jaeger + Prometheus + Grafana                      |
| Auth          | JWT Bearer (HS256)                                                 |
| Runtime       | AOT-ready slim builder, server GC, tiered PGO                      |

## Solution layout

```
src/
  Hope.Agent.Shared         — Result, Error, IClock
  Hope.Agent.Domain         — Conversation, MemoryRecord, AuditEvent
  Hope.Agent.Application    — Abstractions: ILLM, IMemoryStore, IAgentTool, IAgentRuntime
  Hope.Agent.Infrastructure — EF Core (Npgsql) + Redis + Qdrant + Kafka
  Hope.Agent.LLMGateway     — Multi-provider chat & embeddings + router
  Hope.Agent.Tools          — Built-in healthcare tools (patient_lookup, schedule, insurance, guidelines)
  Hope.Agent.AgentRuntime   — Orchestrator: tool-call loop, memory retrieval, audit
  Hope.Agent.Api            — Minimal API: /v1/agent/chat, /v1/agent/stream
  Hope.Agent.Gateway        — YARP edge with JWT + rate limit
deployments/
  docker-compose.yml        — Full dev stack
  Dockerfile                — Multi-stage .NET 9 build
  otel-collector.yaml
  prometheus.yml
```

## Quick start (local dev)

```powershell
# 1. Restore + build
dotnet restore
dotnet build

# 2. Spin up infra + services
cd deployments
copy .env.example .env   # then put your provider keys in .env
docker compose up -d --build

# 3. Apply EF migrations (first run)
cd ..
dotnet ef migrations add Initial --project src/Hope.Agent.Infrastructure --startup-project src/Hope.Agent.Api
dotnet ef database update --project src/Hope.Agent.Infrastructure --startup-project src/Hope.Agent.Api
```

| Endpoint         | URL                                   |
| ---------------- | ------------------------------------- |
| Gateway (public) | http://localhost:5000                 |
| API (direct)     | http://localhost:5080                 |
| OpenAPI          | http://localhost:5080/openapi/v1.json |
| Jaeger           | http://localhost:16686                |
| Prometheus       | http://localhost:9090                 |
| Grafana          | http://localhost:3000 (admin/admin)   |
| Qdrant           | http://localhost:6333/dashboard       |

## Call the agent

```bash
# Mint a dev token, then:
curl -X POST http://localhost:5000/v1/agent/chat \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"message":"Đặt lịch khám tim mạch cho bệnh nhân MRN-001 sáng mai"}'
```

## Switching LLM provider

In `.env`:

```
LLM_PROVIDER=qwen          # or openai | anthropic | gemini | ollama
OPENAI_API_KEY=sk-...
ANTHROPIC_API_KEY=sk-ant-...
GEMINI_API_KEY=...
QWEN_BASE_URL=http://vllm:8000/v1
```

The `ILLMRouter` resolves the named provider; all providers implement OpenAI-style tool-calling so the agent loop is provider-agnostic.

## Architecture highlights

- **Performance**: `WebApplication.CreateSlimBuilder`, server GC, tiered PGO, `DbContextPool`, central package management, HTTP/2 between gateway↔api, PowerOfTwoChoices load balancing.
- **Resilience**: Microsoft.Extensions.Http.Resilience standard handler (retry + circuit breaker + timeout) on every LLM client.
- **Observability**: full OTLP traces + metrics + Serilog logs; tool spans (`tool.{name}`) and orchestrator span (`agent.run`).
- **Security**: JWT validated at gateway _and_ API; PHI-aware system prompt; audit-log every run via `IAuditSink`.
- **Memory**: episodic write-back after every conversation; vector search at the start of every run.
- **Extensibility**: drop a new `IAgentTool` into DI and it auto-registers via the registry.

## Next phases (see agent_plan.md)

- **Phase 2** — RAG pipeline for clinical guidelines (ingestion, chunking, hybrid retrieval, rerank).
- **Phase 3** — Multi-agent orchestration via Kafka topics + agent supervisor.
- **Phase 4** — Temporal workflows for long-running clinical journeys.
- **Phase 5** — Kubernetes + vLLM cluster + full SLO monitoring.
