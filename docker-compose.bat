@echo off
REM Hope.Agent Docker Compose Helper Script (Windows)
REM Usage: docker-compose.bat [start|stop|restart|logs|ps|clean|rebuild]

setlocal enabledelayedexpansion

set SCRIPT_DIR=%~dp0
set ENV_FILE=%SCRIPT_DIR%.env
set COMPOSE=docker-compose

if not exist "%ENV_FILE%" (
    echo [INFO] .env file not found. Creating from .env.example...
    if exist "%SCRIPT_DIR%.env.example" (
        copy "%SCRIPT_DIR%.env.example" "%ENV_FILE%"
        echo [SUCCESS] .env created. Please edit it to add LLM API keys.
    ) else (
        echo [ERROR] .env.example not found
        exit /b 1
    )
)

if "%1"=="" goto start
if /i "%1"=="start" goto start
if /i "%1"=="stop" goto stop
if /i "%1"=="restart" goto restart
if /i "%1"=="logs" goto logs
if /i "%1"=="ps" goto ps
if /i "%1"=="down" goto down
if /i "%1"=="clean" goto clean
if /i "%1"=="rebuild" goto rebuild
if /i "%1"=="db-shell" goto db_shell
if /i "%1"=="redis-shell" goto redis_shell
if /i "%1"=="kafka-topics" goto kafka_topics
if /i "%1"=="kafka-consume" goto kafka_consume
goto help

:start
echo [INFO] Starting Hope.Agent services...
%COMPOSE% up -d
echo.
echo [SUCCESS] Services started. Wait 30-60s for health checks to pass.
echo.
echo [INFO] Useful URLs:
echo   API:         http://localhost:8080
echo   API Docs:    http://localhost:8080/openapi/v1
echo   Health:      http://localhost:8080/healthz/live
echo   Temporal UI: http://localhost:8081
echo.
echo [INFO] View logs: docker-compose logs -f hope-agent-api
exit /b 0

:stop
echo [INFO] Stopping Hope.Agent services...
%COMPOSE% stop
echo [SUCCESS] Services stopped
exit /b 0

:restart
echo [INFO] Restarting Hope.Agent services...
%COMPOSE% restart
echo [SUCCESS] Services restarted
exit /b 0

:logs
set SERVICE=hope-agent-api
if not "%2"=="" set SERVICE=%2
%COMPOSE% logs -f %SERVICE%
exit /b 0

:ps
echo [INFO] Service status:
%COMPOSE% ps
exit /b 0

:down
echo [INFO] Removing containers (volumes preserved)...
%COMPOSE% down
echo [SUCCESS] Services removed
exit /b 0

:clean
echo [WARNING] WARNING: This will delete ALL data (volumes, databases, cache)!
set /p confirm="Type 'yes' to confirm: "
if /i "%confirm%"=="yes" (
    %COMPOSE% down -v
    echo [SUCCESS] All containers and volumes removed
) else (
    echo [INFO] Cancelled
)
exit /b 0

:rebuild
set SERVICE=hope-agent-api
if not "%2"=="" set SERVICE=%2
echo [INFO] Rebuilding %SERVICE% image...
%COMPOSE% build %SERVICE%
%COMPOSE% up -d %SERVICE%
echo [SUCCESS] %SERVICE% rebuilt and restarted
exit /b 0

:db_shell
echo [INFO] Connecting to PostgreSQL...
docker exec -it hope-agent-postgres ^
    psql -U postgres -d hope_agent
exit /b 0

:redis_shell
echo [INFO] Connecting to Redis...
docker exec -it hope-agent-redis ^
    redis-cli -a redis123
exit /b 0

:kafka_topics
echo [INFO] Listing Kafka topics...
docker exec hope-agent-kafka ^
    kafka-topics --bootstrap-server localhost:9092 --list
exit /b 0

:kafka_consume
set TOPIC=agent.notifications
if not "%2"=="" set TOPIC=%2
echo [INFO] Consuming from topic: %TOPIC%
docker exec -it hope-agent-kafka ^
    kafka-console-consumer ^
        --bootstrap-server localhost:9092 ^
        --topic %TOPIC% ^
        --from-beginning
exit /b 0

:help
echo.
echo Hope.Agent Docker Compose Helper (Windows)
echo.
echo Usage: %0 [COMMAND] [OPTIONS]
echo.
echo Commands:
echo   start              Start all services (default)
echo   stop               Stop services (preserve data)
echo   restart            Restart services
echo   down               Stop and remove containers (preserve data)
echo   clean              Remove containers AND volumes (deletes all data!)
echo.
echo   logs [SERVICE]     View service logs (default: hope-agent-api)
echo   ps                 Show service status
echo   rebuild [SERVICE]  Rebuild and restart service (default: hope-agent-api)
echo.
echo   db-shell           Connect to PostgreSQL
echo   redis-shell        Connect to Redis
echo   kafka-topics       List Kafka topics
echo   kafka-consume [TOPIC]  Consume messages from Kafka topic
echo.
echo Examples:
echo   %0 start
echo   %0 logs
echo   %0 rebuild
echo   %0 db-shell
echo   %0 kafka-consume agent.notifications
echo.
exit /b 1
