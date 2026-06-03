# Hope.Agent Docker Compose Setup

Complete containerized environment for Hope.Agent healthcare AI platform with all dependencies.

## 🏗 Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                     Hope.Agent API (port 8080)                  │
│                 .NET 9 Healthcare AI Service                    │
└──────────────┬──────────────┬──────────────┬──────────────┬─────┘
               │              │              │              │
        ┌──────▼──┐    ┌──────▼──┐   ┌──────▼──┐   ┌──────▼──┐
        │ Postgres │    │  Redis  │   │  Kafka  │   │ Temporal │
        │ Database │    │  Cache  │   │ Events  │   │Workflows │
        │(5432)    │    │(6379)   │   │(9092)   │   │ (7233)   │
        └──────────┘    └─────────┘   └──────────┘   └──────────┘
                                           │
                                      ┌────▼────┐
                                      │Zookeeper│
                                      │ (2181)  │
                                      └─────────┘

        ┌──────────────────┐       ┌──────────────┐
        │  Qdrant (6333)   │       │Temporal UI   │
        │ Vector Database  │       │ (8081)       │
        │  RAG/Embeddings  │       │              │
        └──────────────────┘       └──────────────┘
```

## 📦 Services

| Service | Port | Purpose | Status Check |
|---------|------|---------|--------------|
| **postgres** | 5432 | Primary database (conversations, users, audit logs) | `pg_isready` |
| **redis** | 6379 | Distributed cache, refresh tokens, idempotency | `redis-cli ping` |
| **zookeeper** | 2181 | Kafka coordination | `echo ruok \| nc` |
| **kafka** | 9092 | Event streaming (async workflows) | Broker API versions |
| **temporal** | 7233 | Durable workflow orchestration | Cluster health |
| **temporal-ui** | 8081 | Temporal Web UI (workflows, activities) | HTTP 200 |
| **qdrant** | 6333 | Vector database for embeddings & RAG | `/health` endpoint |
| **hope-agent-api** | 8080 | Main API service | `/healthz/live` |

## 🚀 Quick Start

### 1. **Clone and Setup**

```bash
cd d:\Pr.Project\Hope.Agent
cp .env.example .env
```

### 2. **Configure LLM Providers** (Optional)

Edit `.env` and add at least one LLM provider API key:

```bash
# OpenAI
OPENAI_API_KEY=sk-...

# Or Anthropic
ANTHROPIC_API_KEY=sk-ant-...

# Or Gemini
GEMINI_API_KEY=AIz...
```

### 3. **Start All Services**

```bash
# Start all containers
docker-compose up -d

# View logs
docker-compose logs -f hope-agent-api

# Stop all
docker-compose down
```

**First run:** Database migrations run automatically on API startup (~30s).

## 🌐 Access Services

| Service | URL | Notes |
|---------|-----|-------|
| API Docs | http://localhost:8080/openapi/v1 | Swagger UI (requires `scope=hope-agent:docs`) |
| Health Check | http://localhost:8080/healthz/live | API readiness probe |
| Temporal UI | http://localhost:8081 | Workflow monitoring |
| Kafka Broker | localhost:9092 | For external clients |
| Redis CLI | `redis-cli -h localhost -a redis123` | Direct DB access |
| pgAdmin | - | Run separately: `docker run -p 5050:80 dpage/pgadmin4` |

## 📝 Common Commands

### Build & Start

```bash
# Build images (required on first run)
docker-compose build

# Start services in background
docker-compose up -d

# View logs
docker-compose logs -f

# View specific service
docker-compose logs -f hope-agent-api

# Show running containers
docker-compose ps
```

### Stop & Clean

```bash
# Stop containers (preserve volumes)
docker-compose stop

# Stop and remove containers
docker-compose down

# Remove volumes (WARNING: deletes all data)
docker-compose down -v

# Remove specific volume
docker volume rm hope-agent_postgres_data
```

### Restart

```bash
# Restart all services
docker-compose restart

# Restart specific service
docker-compose restart hope-agent-api
```

### Rebuild API After Code Changes

```bash
# Rebuild only API image
docker-compose build hope-agent-api

# Rebuild and restart
docker-compose up -d --build hope-agent-api
```

## 🔧 Configuration

### Environment Variables

All variables are in `.env`:

- **Database**: `DB_NAME`, `DB_USER`, `DB_PASSWORD`
- **Redis**: `REDIS_PASSWORD`
- **Qdrant**: `QDRANT_API_KEY`
- **LLM**: `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `GEMINI_API_KEY`
- **Temporal**: `TEMPORAL__ENABLEWORKER` (set to `true` for durable workflows)

### Database Migrations

Migrations run automatically on API startup. To manually run:

```bash
# Inside API container
docker exec hope-agent-api dotnet Hope.Agent.Api.dll migrate
```

### Kafka Topics

Auto-created on startup:
- `agent.notifications` — real-time user notifications
- `agent.task.created` — workflow task events
- `agent.task.completed` — task completion events
- `agent.role.completed` — agent role completion

View topics:

```bash
docker exec hope-agent-kafka kafka-topics --bootstrap-server localhost:9092 --list
```

### Temporal Workflows

Enable worker in `.env`:

```env
TEMPORAL__ENABLEWORKER=true
```

Workflows available:
- Patient Admission
- Emergency Triage
- Appointment Scheduling
- Medication Reminder
- Audit Report

Monitor at: http://localhost:8081/workflows

## 🐛 Troubleshooting

### API Won't Start

**Problem:** Connection timeouts to PostgreSQL/Redis/Kafka

```
Unhandled exception: System.InvalidOperationException: 
  Unable to connect to database
```

**Solution:**
```bash
# Check service health
docker-compose ps

# Check specific service logs
docker-compose logs postgres
docker-compose logs redis

# Restart services
docker-compose restart
```

### Kafka Connect Failures

**Problem:** Kafka warnings in API logs

```
%3|...|FAIL|rdkafka#consumer-1| ... Connect to ... failed
```

**Solution:** Kafka is still initializing. This is normal on first startup; retries with backoff and eventually connects.

### Temporal Server Unavailable

**Problem:** `Connection failed: tcp connect error`

**Solution:** Temporal disables by default (`EnableWorker=false`). To enable:

```env
TEMPORAL__ENABLEWORKER=true
```

Then:
```bash
docker-compose up -d temporal
docker-compose restart hope-agent-api
```

### Database Locked

```bash
# Force recreate containers and volumes
docker-compose down -v
docker-compose up -d
```

### Out of Disk Space

```bash
# Clean up unused volumes
docker volume prune

# Clean up images
docker image prune -a
```

## 📊 Monitoring

### View Database

```bash
# PostgreSQL shell
docker exec -it hope-agent-postgres psql -U postgres -d hope_agent

# List tables
\dt

# Query conversations
SELECT id, user_id, message_count FROM conversations LIMIT 5;
```

### View Cache

```bash
# Redis shell
docker exec -it hope-agent-redis redis-cli -a redis123

# List keys
KEYS *

# Inspect refresh tokens
KEYS rt:*
GET rt:abc123
```

### View Events

```bash
# Kafka consumer group
docker exec hope-agent-kafka kafka-consumer-groups \
  --bootstrap-server localhost:9092 \
  --list

# Read messages
docker exec hope-agent-kafka kafka-console-consumer \
  --bootstrap-server localhost:9092 \
  --topic agent.notifications \
  --from-beginning
```

## 🔐 Security

### Production Hardening

1. **Change all passwords** in `.env`:
   ```env
   DB_PASSWORD=<strong-random>
   REDIS_PASSWORD=<strong-random>
   QDRANT_API_KEY=<strong-random>
   ```

2. **Use managed services** (RDS, ElastiCache, Aiven Kafka)

3. **Enable SSL/TLS** for PostgreSQL and Redis

4. **Restrict network access**:
   ```bash
   # Only expose API, not databases
   # Use network policies: docker network create --driver bridge
   ```

5. **Rotate LLM API keys** regularly

## 📦 Production Deployment

For Kubernetes / production use the provided Helm charts (see `deployments/helm/`).

For Docker Swarm, use stack files:

```bash
docker stack deploy -c docker-compose.yml hope-agent
```

## 🆘 Getting Help

**Logs:** `docker-compose logs -f`

**Health checks:** `docker-compose ps`

**Database state:** Connect with `psql` or `redis-cli`

**Temporal workflows:** http://localhost:8081

## 📜 License

Same as Hope.Agent main project.
