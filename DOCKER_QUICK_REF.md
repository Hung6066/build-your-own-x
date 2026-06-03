# Docker Compose Quick Reference

Fast answers to common questions.

## 🚀 Getting Started

```bash
# First time setup
cp .env.example .env
# Edit .env to add LLM API keys (optional for dev)

# Start everything
docker-compose up -d

# Check status
docker-compose ps

# View logs
docker-compose logs -f
```

**Wait ~60s for all health checks to pass.**

## 🌐 Access Services

| What | URL/Command |
|------|-------------|
| API Docs | http://localhost:8080/openapi/v1 |
| API Health | http://localhost:8080/healthz/live |
| Temporal Workflows | http://localhost:8081 |
| PostgreSQL | `psql -h localhost -U postgres -d hope_agent` |
| Redis | `redis-cli -h localhost -a redis123` |
| Kafka Broker | `localhost:9092` |

## 🔄 Day-to-Day Commands

### Restart After Code Changes

```bash
# Rebuild API only
docker-compose up -d --build hope-agent-api

# View startup logs
docker-compose logs -f hope-agent-api
```

### View Logs

```bash
# All services
docker-compose logs -f

# Specific service
docker-compose logs -f hope-agent-api
docker-compose logs -f postgres
docker-compose logs -f kafka

# Last 50 lines
docker-compose logs -f hope-agent-api --tail=50
```

### Database Access

```bash
# PostgreSQL shell
docker exec -it hope-agent-postgres psql -U postgres -d hope_agent

# Quick queries
docker exec hope-agent-postgres psql -U postgres -d hope_agent \
  -c "SELECT id, user_id FROM conversations LIMIT 5;"

# Database size
docker exec hope-agent-postgres psql -U postgres -d hope_agent \
  -c "SELECT pg_size_pretty(pg_database_size('hope_agent'));"
```

### Redis Access

```bash
# Redis CLI
docker exec -it hope-agent-redis redis-cli -a redis123

# Get cache size
KEYS *
DBSIZE
MEMORY STATS

# Check refresh tokens
KEYS rt:*
GET rt:<token>
```

### Kafka Access

```bash
# List topics
docker exec hope-agent-kafka kafka-topics \
  --bootstrap-server localhost:9092 --list

# Consume messages (real-time)
docker exec -it hope-agent-kafka kafka-console-consumer \
  --bootstrap-server localhost:9092 \
  --topic agent.notifications \
  --from-beginning

# Produce test message
docker exec -it hope-agent-kafka kafka-console-producer \
  --bootstrap-server localhost:9092 \
  --topic agent.notifications
```

### Temporal Workflows

Enable in `.env`:

```env
TEMPORAL__ENABLEWORKER=true
```

Then:

```bash
# Restart with worker enabled
docker-compose restart hope-agent-api

# View workflows
open http://localhost:8081
```

## 🧹 Cleanup

```bash
# Stop containers (data preserved)
docker-compose stop

# Stop and remove containers (data preserved)
docker-compose down

# Remove everything including data (⚠️ DESTRUCTIVE)
docker-compose down -v

# Prune unused volumes
docker volume prune

# Prune unused images
docker image prune -a
```

## 🐛 Troubleshooting

### Connection Refused to API

```bash
# Check if API is healthy
curl http://localhost:8080/healthz/live

# Check logs
docker-compose logs hope-agent-api

# Rebuild and restart
docker-compose up -d --build hope-agent-api
```

### PostgreSQL Connection Errors

```bash
# Check if PostgreSQL is running
docker-compose ps postgres

# Check logs
docker-compose logs postgres

# Restart
docker-compose restart postgres
```

### Kafka Connection Warnings

Normal on startup; Kafka retries automatically. If it persists >5 min, restart:

```bash
docker-compose restart kafka zookeeper
```

### Temporal Connection Errors

Temporal is disabled by default (`EnableWorker=false`). To enable:

1. Edit `.env`: `TEMPORAL__ENABLEWORKER=true`
2. Restart: `docker-compose up -d hope-agent-api`

### Out of Disk Space

```bash
# Clean Docker system
docker system prune -a --volumes

# Or remove specific volumes
docker volume rm hope-agent_postgres_data
docker volume rm hope-agent_redis_data
```

## 📊 Monitoring

### Check Service Health

```bash
# All services
docker-compose ps

# Specific service
docker inspect hope-agent-postgres | grep -A 5 '"Health"'
```

### Resource Usage

```bash
# Real-time CPU/memory
docker stats

# Detailed stats per service
docker-compose exec postgres top -b -n 1
```

### Database Stats

```bash
docker exec hope-agent-postgres psql -U postgres -d hope_agent -c \
  "SELECT schemaname, tablename, pg_size_pretty(pg_total_relation_size(schemaname||'.'||tablename)) as size FROM pg_tables ORDER BY pg_total_relation_size(schemaname||'.'||tablename) DESC LIMIT 10;"
```

## 🔧 Advanced

### Run Shell Inside Container

```bash
# bash
docker exec -it hope-agent-api bash

# sh (for alpine images)
docker exec -it hope-agent-postgres sh
```

### View Network Details

```bash
# List networks
docker network ls

# Inspect network
docker network inspect hope_agent_hope-agent-net
```

### Change Environment Variables

Edit `.env` and restart:

```bash
docker-compose down
docker-compose up -d
```

### Use External Services

For Kubernetes/external PostgreSQL, update connection strings in `.env`:

```env
CONNECTIONSTRINGS__DEFAULT=Server=my-external-postgres.com;...
CONNECTIONSTRINGS__REDIS=my-external-redis.com:6379
KAFKA__BOOTSTRAPSERVERS=my-external-kafka:9092
```

## 📝 Common Issues & Solutions

| Issue | Solution |
|-------|----------|
| API won't start | Check `docker-compose logs hope-agent-api` |
| Can't connect to database | Run `docker-compose ps` and check if postgres is up |
| Kafka warnings | Normal on startup; wait 2-3 min |
| Port already in use | Change port in docker-compose.yml (e.g., `8081:8080`) |
| Out of memory | Increase Docker Desktop memory limit |
| Volumes not syncing | Use bind mounts in docker-compose.yml |

## 🔗 More Info

- **Full docs**: [DOCKER_COMPOSE.md](DOCKER_COMPOSE.md)
- **Configuration**: [.env](.env)
- **Architecture**: [docs/architecture.md](docs/architecture.md)
