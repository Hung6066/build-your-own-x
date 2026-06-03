#!/bin/bash
# Hope.Agent Docker Compose Helper Script
# Usage: ./docker-compose.sh [start|stop|restart|logs|ps|clean|rebuild]

set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
ENV_FILE="${SCRIPT_DIR}/.env"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

log_info() {
    echo -e "${BLUE}ℹ ${1}${NC}"
}

log_success() {
    echo -e "${GREEN}✓ ${1}${NC}"
}

log_warning() {
    echo -e "${YELLOW}⚠ ${1}${NC}"
}

log_error() {
    echo -e "${RED}✗ ${1}${NC}"
}

# Check if .env exists
if [ ! -f "$ENV_FILE" ]; then
    log_warning ".env file not found. Creating from .env.example..."
    if [ -f "${SCRIPT_DIR}/.env.example" ]; then
        cp "${SCRIPT_DIR}/.env.example" "$ENV_FILE"
        log_success ".env created. Please edit it to add LLM API keys."
    else
        log_error ".env.example not found"
        exit 1
    fi
fi

case "${1:-start}" in
    start)
        log_info "Starting Hope.Agent services..."
        docker-compose up -d
        log_success "Services started. Wait 30-60s for health checks to pass."
        echo ""
        log_info "Useful URLs:"
        echo "  API:         http://localhost:8080"
        echo "  API Docs:    http://localhost:8080/openapi/v1"
        echo "  Health:      http://localhost:8080/healthz/live"
        echo "  Temporal UI: http://localhost:8081"
        echo ""
        log_info "View logs: docker-compose logs -f hope-agent-api"
        ;;

    stop)
        log_info "Stopping Hope.Agent services..."
        docker-compose stop
        log_success "Services stopped"
        ;;

    restart)
        log_info "Restarting Hope.Agent services..."
        docker-compose restart
        log_success "Services restarted"
        ;;

    logs)
        SERVICE="${2:-hope-agent-api}"
        docker-compose logs -f "$SERVICE"
        ;;

    ps)
        log_info "Service status:"
        docker-compose ps
        ;;

    down)
        log_warning "Removing containers (volumes preserved)..."
        docker-compose down
        log_success "Services removed"
        ;;

    clean)
        log_warning "WARNING: This will delete ALL data (volumes, databases, cache)!"
        read -p "Type 'yes' to confirm: " confirm
        if [ "$confirm" = "yes" ]; then
            docker-compose down -v
            log_success "All containers and volumes removed"
        else
            log_info "Cancelled"
        fi
        ;;

    rebuild)
        SERVICE="${2:-hope-agent-api}"
        log_info "Rebuilding $SERVICE image..."
        docker-compose build "$SERVICE"
        docker-compose up -d "$SERVICE"
        log_success "$SERVICE rebuilt and restarted"
        ;;

    db-shell)
        log_info "Connecting to PostgreSQL..."
        docker exec -it hope-agent-postgres \
            psql -U postgres -d hope_agent
        ;;

    redis-shell)
        log_info "Connecting to Redis..."
        docker exec -it hope-agent-redis \
            redis-cli -a redis123
        ;;

    kafka-topics)
        log_info "Listing Kafka topics..."
        docker exec hope-agent-kafka \
            kafka-topics --bootstrap-server localhost:9092 --list
        ;;

    kafka-consume)
        TOPIC="${2:-agent.notifications}"
        log_info "Consuming from topic: $TOPIC"
        docker exec -it hope-agent-kafka \
            kafka-console-consumer \
                --bootstrap-server localhost:9092 \
                --topic "$TOPIC" \
                --from-beginning
        ;;

    *)
        cat << EOF
${BLUE}Hope.Agent Docker Compose Helper${NC}

Usage: $0 [COMMAND] [OPTIONS]

Commands:
  start              Start all services (default)
  stop               Stop services (preserve data)
  restart            Restart services
  down               Stop and remove containers (preserve data)
  clean              Remove containers AND volumes (deletes all data!)
  
  logs [SERVICE]     View service logs (default: hope-agent-api)
  ps                 Show service status
  rebuild [SERVICE]  Rebuild and restart service (default: hope-agent-api)
  
  db-shell           Connect to PostgreSQL
  redis-shell        Connect to Redis
  kafka-topics       List Kafka topics
  kafka-consume [TOPIC]  Consume messages from Kafka topic

Examples:
  $0 start
  $0 logs
  $0 rebuild
  $0 db-shell
  $0 kafka-consume agent.notifications

EOF
        exit 1
        ;;
esac
