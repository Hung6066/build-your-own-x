# Hope.Agent Scale Runbook

## Production Topology

```text
Gateway / Load Balancer
  -> Hope.Agent.Api replicas
      - HTTP/MCP/Webhooks/Auth
      - Runtime:EnableHostedServices=false

Kafka / Temporal / Postgres ledger
  -> Hope.Agent.Worker replicas
      - AutonomousActionWorker
      - AutonomyDailyReviewWorker
      - EvaluationHarnessHostedService
      - MemoryMaintenanceHostedService
      - AdversarialAutoPromoter
      - ScheduledAgentTaskRunner
      - SessionInsightHostedService
      - SkillSelfImprovementHostedService
      - MCP tool discovery

Postgres: ledger, audit, workflow state
Redis: cache, idempotency, locks
Qdrant: vector memory and RAG
Neo4j: knowledge graph
Object storage: report/export artifacts
Prometheus/Grafana/Alertmanager or OTLP backend
```

## Runtime Split

Production API:

```json
"Runtime": {
  "EnableHostedServices": false,
  "ApiAcceptsBackgroundJobs": false
}
```

Worker:

```json
"Runtime": {
  "EnableHostedServices": true,
  "ApiAcceptsBackgroundJobs": false
}
```

Run worker locally:

```powershell
dotnet run --project src\Hope.Agent.Worker\Hope.Agent.Worker.csproj
```

Docker Compose:

```powershell
docker compose up --scale hope-agent-worker=2
docker compose -f deployments\docker-compose.yml up --scale worker=2
```

Kubernetes:

```powershell
kubectl apply -f deployments\k8s
kubectl scale deployment/hope-agent-api --replicas=3 -n hope-agent
kubectl scale deployment/hope-agent-worker --replicas=2 -n hope-agent
```

The API deployment explicitly sets `Runtime__EnableHostedServices=false` and
`Temporal__EnableWorker=false`; `hope-agent-worker` is the only production
process expected to run queues, scheduled reviews, and background autonomy.

## Scale Control Endpoints

- `GET /v1/dashboard/scale`
- `GET /v1/dashboard/cost`
- `GET /v1/dashboard/agent-registry`
- `GET /v1/harness/status`
- `GET /v1/harness/governance`
- `GET /v1/autonomy/level5/readiness`

## P0 Checklist

- API and worker can scale independently.
- Hosted services are disabled in production API.
- Docker Compose and Kubernetes manifests include separate worker services.
- Queue/backlog metrics are visible in `/v1/dashboard/scale`.
- Tool execution goes through sandbox executor.
- Production tool RBAC defaults deny unknown tools.
- Approval SLA/escalation policy is machine-readable.

## P1 Checklist

- Agent registry is machine-readable.
- Context manifest and version fingerprint are present in `audit_logs`.
- Evaluation metrics expose task success, hallucination, tool accuracy, faithfulness, latency, and cost.
- Alert rules are configured in `AgentOps:AlertRules`.

## P2 Checklist

- Load test script exists:

```powershell
.\tests\hope-scale-load-test.ps1 -Concurrency 20 -RequestsPerWorker 10
```

- Dashboard endpoints expose cost/backlog/readiness.
- Workflow DAG/debug endpoint exists:

```http
GET /v1/harness/workflows/debug/{workflow}
```
