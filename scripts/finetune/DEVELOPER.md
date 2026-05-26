# Training Service — Developer Guide

> **Audience**: Engineers extending, debugging, or operating the Hope.Agent
> fine-tuning service.  
> **Service location**: `scripts/finetune/`  
> **Entry point**: `main.py` → `app/api/app.py` → uvicorn  
> **Architecture**: Clean Architecture (Domain / Application / Infrastructure / Interfaces)

---

## Table of Contents

1. [High-Level Architecture](#1-high-level-architecture)
2. [Module Map](#2-module-map)
3. [Data Flow — End-to-End](#3-data-flow--end-to-end)
   - 3.1 Manual job via REST
   - 3.2 Automatic cycle via scheduler
4. [Layer Reference](#4-layer-reference)
   - 4.1 Domain (`app/domain.py`)
   - 4.2 Ports (`app/ports.py`)
   - 4.3 Use Cases (`app/use_cases.py`)
   - 4.4 Infrastructure: Config, Logging, Correlation
   - 4.5 Infrastructure: Persistence (SQLite)
   - 4.6 Infrastructure: HTTP Clients (retries)
   - 4.7 Infrastructure: ML Adapters (trainer, evaluator)
   - 4.8 Interfaces: FastAPI app, routers, scheduler
5. [Database Schema](#5-database-schema)
6. [Job Status State Machine](#6-job-status-state-machine)
7. [Champion Promotion Logic](#7-champion-promotion-logic)
8. [Elo Rating System](#8-elo-rating-system)
9. [REST API Reference](#9-rest-api-reference)
10. [Configuration Reference](#10-configuration-reference)
11. [Extension Points](#11-extension-points)
12. [Local Development Setup](#12-local-development-setup)
13. [Testing](#13-testing)
14. [Observability](#14-observability)
15. [Common Failure Modes](#15-common-failure-modes)
16. [Scenario Analysis — Tình Huống Thực Tế](#16-scenario-analysis--tình-huống-thực-tế)
17. [Chi Tiết Thuật Toán](#17-chi-tiết-thuật-toán)

- 17.1 DPO Training Loop
- 17.2 Champion-vs-Challenger Evaluation
- 17.3 NaN Guard
- 17.4 Continuous Learning Scheduler
- 17.5 Dataset Hashing
- 17.6 ORPO Training Loop

18. [Phân Tích Toán Học Chi Tiết](#18-phân-tích-toán-học-chi-tiết)

- 18.1 DPO Loss Function
- 18.2 LoRA — Low-Rank Adaptation
- 18.3 QLoRA — Quantized LoRA
- 18.4 Elo Rating System
- 18.5 Wilson Score CI
- 18.6 Gradient Accumulation
- 18.7 Cosine LR Schedule
- 18.8 Holdout Split
- 18.9 RSLoRA — Rank-Stabilized LoRA
- 18.10 ORPO — Odds-Ratio Preference Optimization
- 18.11 IPO — Identity Preference Optimization
- 18.12 NEFTune — Noisy Embedding Fine-Tuning
- 18.13 Sequence Packing

---

## 1. High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                       Hope.Agent Main API (.NET)                    │
│   /v1/training/export/dpo   /v1/training/preference/count           │
│   /v1/training/champion/announce                                    │
└────────────────────┬───────────────────────────┬────────────────────┘
                     │ pull data                  │ notify on promotion
                     ▼                            │
┌────────────────────────────────────────────────┴────────────────────┐
│               Training Service  (this codebase)                     │
│                                                                     │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │  Interfaces (app/api/)                                       │   │
│  │  ┌──────────┐   ┌──────────────────────────────────────────┐ │   │
│  │  │Scheduler │   │ FastAPI  routers + middleware + lifespan │ │   │
│  │  │(APSched) │   └─────────────────┬────────────────────────┘ │   │
│  │  └──────────┘                     │                          │   │
│  └───────────────────────────────────┼──────────────────────────┘   │
│                                      │ calls                        │
│  ┌───────────────────────────────────▼──────────────────────────┐   │
│  │  Application (app/use_cases.py + app/ports.py)               │   │
│  │  SubmitJob · GetJob · CancelJob · RunDpoCycleUseCase         │   │
│  │  ListAdapters · GetChampion · PromoteChampion                │   │
│  └───────────────────────────────────┬──────────────────────────┘   │
│                                      │ implements ports             │
│  ┌───────────────────────────────────▼──────────────────────────┐   │
│  │  Infrastructure (app/infra/)                                 │   │
│  │  ┌──────────────┐  ┌────────────────┐  ┌──────────────────┐ │   │
│  │  │  persistence │  │  agent_http    │  │  ollama_http     │ │   │
│  │  │  SQLite repos│  │  (+ tenacity)  │  │  (+ tenacity)    │ │   │
│  │  └──────────────┘  └────────────────┘  └──────────────────┘ │   │
│  │  ┌──────────────┐  ┌────────────────┐  ┌──────────────────┐ │   │
│  │  │  trainer_hf  │  │ evaluator_llm  │  │ config/logging   │ │   │
│  │  │  (wraps      │  │ (wraps         │  │ /metrics/        │ │   │
│  │  │  trainer.py) │  │ evaluate.py)   │  │ correlation      │ │   │
│  │  └──────────────┘  └────────────────┘  └──────────────────┘ │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                     │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │  Domain (app/domain.py)                                      │   │
│  │  Job · Adapter · EvaluationResult · PromotionDecision        │   │
│  │  JobStatus · decide_promotion() · wilson_lower_bound()       │   │
│  └──────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────┘
                     │ POST /api/create          │ adapter weights
                     ▼                           ▼
              ┌─────────────┐        ┌────────────────────┐
              │   Ollama    │        │  /adapters/<tag>/  │
              │ (inference) │        │  (filesystem)      │
              └─────────────┘        └────────────────────┘
```

The service is **stateless across restarts** — all job state lives in SQLite
(`registry.db`). The model weights live on disk under `adapters/`.

---

## 2. Module Map

```
scripts/finetune/
│
├── main.py                    Entry point (uvicorn factory)
├── trainer.py                 HuggingFace LoRA/QLoRA training (blocking)
├── evaluate.py                LLM-judge evaluation (blocking)
├── callbacks.py               HuggingFace TrainerCallback subclasses
├── prepare_dpo.py             Standalone CLI: convert raw export → DPO JSONL
├── prepare_sft.py             Standalone CLI: convert raw export → SFT JSONL
├── train_lora.py              Standalone CLI: run training without the service
│
├── app/                       Clean Architecture package
│   ├── domain.py               LAYER 1 — entities, value objects, exceptions,
│   │                           pure policies (no external deps)
│   ├── ports.py                LAYER 2 (interface) — Protocol abstractions
│   ├── use_cases.py            LAYER 2 (application) — orchestration logic
│   │
│   ├── infra/                  LAYER 3 — concrete port implementations
│   │   ├── config.py           Settings (pydantic-settings, env-driven)
│   │   ├── logging.py          Structured JSON logging + correlation-ID
│   │   ├── correlation.py      ContextVar for X-Correlation-Id propagation
│   │   ├── metrics.py          Prometheus counters / histograms
│   │   ├── persistence.py      SQLiteConnection + 4 repository classes
│   │   ├── agent_http.py       AgentHttpClient (tenacity retries)
│   │   ├── ollama_http.py      OllamaInferenceRegistry + AgentHttpNotifier
│   │   ├── trainer_hf.py       HuggingFaceTrainer (wraps trainer.py)
│   │   ├── evaluator_llm.py    LlmJudgeEvaluator (wraps evaluate.py)
│   │   └── utils.py            SystemClock, AsyncioExecutor
│   │
│   └── api/                    LAYER 4 — FastAPI + APScheduler
│       ├── app.py              create_app() factory + lifespan
│       ├── routers.py          HTTP route handlers (thin, no logic)
│       ├── schemas.py          Pydantic request/response DTOs
│       ├── deps.py             FastAPI Depends providers
│       ├── security.py         require_api_key dependency
│       ├── middleware.py       CorrelationIdMiddleware, MetricsMiddleware
│       ├── errors.py           DomainError → HTTP status mapping
│       └── scheduling.py       ContinuousLearningScheduler (APScheduler)
│
├── Dockerfile                CUDA 12.4 image
├── docker-compose.yml        GPU deployment
├── requirements.txt          Python dependencies
├── .env.example              Environment template
└── tests/
    ├── conftest.py
    ├── test_clean_arch.py     Domain policy, SQLite repos, Wilson CI
    ├── test_agent_http.py     HTTP client retries (mocked transport)
    ├── test_registry.py      Champion promotion + evaluation recording
    ├── test_hashing.py       SHA-256 stability
    └── test_elo.py            Elo math correctness
```

**Dependency Rule** (Clean Architecture): inner layers never import outer layers.

```
domain ← use_cases ← infra ← api
              ↑
            ports (Protocols)
```

`trainer.py`, `evaluate.py`, `callbacks.py` are implementation files loaded
dynamically by `trainer_hf.py` and `evaluator_llm.py` via `sys.path` injection.
They are not imported anywhere else in the `app/` package.

---

## 3. Data Flow — End-to-End

### 3.1 Manual job via REST

```
Developer / Hope.Agent
  │
  │  POST /jobs  {"job_type":"dpo","specialty":"cardio","max_records":3000}
  │  Header: X-Api-Key: <key>
  ▼
app/api/routers.py :: submit_job()
  │  1. Validates X-Api-Key via require_api_key dependency
  │  2. Calls SubmitJobUseCase.execute(SubmitJobInput)
  │     → generates job_id, writes to JobRepository (status="pending")
  │  3. Schedules RunDpoCycleUseCase as a BackgroundTask
  │  4. Returns 200 JobOut immediately  ← response to caller
  │
  └─► asyncio.Semaphore (max_concurrent_jobs=1)
         │  acquires slot — prevents parallel GPU use
         ▼
      RunDpoCycleUseCase.execute(job_id)
         │
         │  status → "preparing"
         ├─► TrainingDataFetcher.download_dpo()       [port: agent_http.py]
         │     │  POST /v1/training/export/dpo  (to Hope.Agent .NET)
         │     │  streams NDJSON → saves to  data/dpo_<job_id>.jsonl
         │     │  tenacity retries on transport errors and 5xx
         │     └─ returns record_count
         │
         │  Compute SHA-256 hash + config_hash
         │  Update jobs.data_hash, record_count
         │
         │  _holdout_split(): first 10% of lines → dpo_<id>.val.jsonl
         │
         │  AdapterRepository.get_champion(specialty, "dpo")
         │    → champion adapter (or None if first ever run)
         │
         │  status → "training"
         ├─► ModelTrainer.train_dpo()                 [port: trainer_hf.py]
         │     │  Runs in ThreadPoolExecutor (non-blocking event loop)
         │     │
         │     ├── _seed_everything(seed=42)
         │     ├── _load_model_and_tokenizer(base_model)
         │     │     BitsAndBytesConfig if load_in_4bit=True
         │     ├── _apply_lora(model, cfg)
         │     │     If resume_from is set → PeftModel.from_pretrained()
         │     │     Else → get_peft_model() with new LoraConfig
         │     │
         │     ├── DPOTrainer(
         │     │     callbacks=[NaNGuardCallback, TimeBudgetCallback, MetricSink])
         │     ├── trainer.train()
         │     │     NaNGuardCallback: checks loss every 50 steps
         │     │       → sets should_training_stop after 3 bad steps
         │     │     TimeBudgetCallback: checks wall time each step
         │     │       → stops if elapsed > max_runtime_hours
         │     │     MetricSink: accumulates logs in memory
         │     ├── trainer.save_model(output_dir)
         │     └── returns TrainingResult
         │
         │  TrainingRunRepository.save(training_run)
         │  AdapterRepository.add(new_adapter, parent_tag=champion.tag)
         │
         │  status → "evaluating"
         ├─► ChampionEvaluator.evaluate()             [port: evaluator_llm.py]
         │     │  Runs in ThreadPoolExecutor
         │     │
         │     │  Load golden suite from data/suites/golden_<specialty>.jsonl
         │     │  If champion_path is None → auto-promote (cold start)
         │     │
         │     ├── _generate(candidate_path, base_model, items)
         │     │     Loads base model → applies candidate adapter → generates
         │     │     Unloads from GPU, clears cache
         │     │
         │     ├── _generate(champion_path, base_model, items)
         │     │     Same for current champion
         │     │
         │     └── for each item:
         │           Alternate order (even idx → A=candidate, odd → A=champion)
         │           _judge(prompt, answer_a, answer_b, ollama)
         │             POST /api/generate to local judge model
         │             Returns "A" | "B" | "TIE"
         │           Accumulate wins, compute Elo update per comparison
         │
         │  win_rate = (candidate_wins + 0.5*ties) / total
         │  EvaluationRepository.save(evaluation_result)
         │
         │  decide_promotion(eval_result, threshold, min_samples, wilson_min)
         │    └─ Three gates (all must pass):
         │         1. scored ≥ eval_min_samples
         │         2. win_rate ≥ eval_win_rate_promote
         │         3. wilson_lower_bound(wins, scored) ≥ eval_wilson_min
         │
         ├─ [if promote=True]
         │   AdapterRepository.promote(tag)
         │     Retires previous champion (is_champion=0, retired_at=now)
         │     Sets new adapter is_champion=1, elo_rating += elo_delta
         │
         │   InferenceRegistry.register(tag, base_model, path)  [ollama_http.py]
         │     POST /api/create → Ollama (with Modelfile) + tenacity retries
         │
         │   AgentNotifier.announce_champion(tag, specialty, elo) [ollama_http.py]
         │     POST /v1/training/champion/announce → Hope.Agent .NET + retries
         │
         │   status → "promoted"
         │
         └─ [if promote=False]
              status → "completed"  (error_detail="did_not_beat_champion")
              Champion unchanged
```

### 3.2 Automatic cycle via scheduler

```
FastAPI lifespan startup (app/api/app.py create_app)
  ├── configure_logging()
  ├── Startup recovery: JobRepository.active()
  │     → mark each active job as FAILED (error_detail="service_restart")
  └── ContinuousLearningScheduler.start()
        APScheduler registers cron job ("0 3 * * *" by default)

03:00 UTC every day:
  ContinuousLearningScheduler._tick()
    │
    ├── TrainingDataFetcher.preference_count(since=last_run_iso)
    │     GET /v1/training/preference/count?since=<iso>
    │
    ├── count < auto_train_min_new_pairs (200)?
    │     → log "not enough data", return
    │
    └── count >= threshold
          acquire job_lock  ← same asyncio.Semaphore as REST endpoint
          SubmitJobUseCase.execute(...)  (status="pending")
          record last_run_iso = now()
          RunDpoCycleUseCase.execute(...)   ← identical to manual path
```

---

## 4. Layer Reference

### 4.1 Domain (`app/domain.py`)

**Purpose**: Pure business rules — no framework deps, no I/O, fully unit-testable.

**Entities & Value Objects**:

```python
@dataclass Job          # Training request + lifecycle state
@dataclass Adapter      # LoRA adapter on disk (champion or retired)
@dataclass EvaluationResult   # champion vs challenger comparison
@dataclass TrainingResult     # training metrics from a completed run
@dataclass PromotionDecision  # promote bool + reason + wilson_lower
```

**Enums**:

```python
class JobStatus(str, Enum):
    PENDING, PREPARING, TRAINING, EVALUATING,
    PROMOTED, COMPLETED, FAILED, CANCELLED
    # .is_terminal → True for PROMOTED/COMPLETED/FAILED/CANCELLED
    # .is_active   → True for PENDING/PREPARING/TRAINING/EVALUATING

class JobType(str, Enum):
    DPO, SFT
```

**Promotion policy functions**:

```python
def wilson_lower_bound(wins: int, n: int, z: float = 1.96) -> float:
    # Wilson score confidence interval lower bound (95% CI)
    # Returns 0.0 when n == 0 (edge case guard)

def decide_promotion(
    eval_result: EvaluationResult,
    *,
    threshold: float,
    min_samples: int,
    wilson_min: float = 0.0,
) -> PromotionDecision:
    # Three-gate check:
    #   1. eval_result.scored >= min_samples
    #   2. eval_result.win_rate >= threshold
    #   3. wilson_lower_bound(wins, scored) >= wilson_min
```

**Exception hierarchy**:

```
DomainError(Exception)
  ├── JobNotFound(404)
  ├── AdapterNotFound(404)
  ├── ChampionNotFound(404)
  ├── InvalidJobStateTransition(409)
  ├── NoTrainingData(422)
  └── TrainingFailed(500)
```

---

### 4.2 Ports (`app/ports.py`)

**Purpose**: Protocol interfaces that decouple the application layer from
infrastructure. Nine `typing.Protocol` classes:

| Protocol                | Responsibility                                             |
| ----------------------- | ---------------------------------------------------------- |
| `JobRepository`         | CRUD for Job entities + `active()` query                   |
| `AdapterRepository`     | CRUD for Adapter entities + `get_champion()` + `promote()` |
| `EvaluationRepository`  | Save evaluation results                                    |
| `TrainingRunRepository` | Save training metrics                                      |
| `TrainingDataFetcher`   | `download_dpo()`, `preference_count()`                     |
| `ModelTrainer`          | `train_dpo()` → TrainingResult                             |
| `ChampionEvaluator`     | `evaluate()` → EvaluationResult                            |
| `InferenceRegistry`     | `register()` — push adapter to Ollama                      |
| `AgentNotifier`         | `announce_champion()` — notify Hope.Agent API              |

Using `typing.Protocol` (structural subtyping) means infrastructure classes
satisfy ports implicitly — no `implements` declaration needed. Tests can pass
in simple stub objects.

---

### 4.3 Use Cases (`app/use_cases.py`)

**Purpose**: Business orchestration. Depends only on ports (never on infra
classes directly). Eight use cases:

| Use Case                 | Description                                           |
| ------------------------ | ----------------------------------------------------- |
| `SubmitJobUseCase`       | Validate input, create Job, persist via JobRepository |
| `GetJobUseCase`          | Fetch single Job by ID                                |
| `ListJobsUseCase`        | List recent jobs (take N)                             |
| `CancelJobUseCase`       | Transition active job → CANCELLED                     |
| `ListAdaptersUseCase`    | List adapters (with optional specialty filter)        |
| `GetChampionUseCase`     | Return current champion for a specialty/type bucket   |
| `PromoteChampionUseCase` | Manual promotion override                             |
| `RunDpoCycleUseCase`     | Full data→train→evaluate→promote pipeline             |

**DTOs defined in `use_cases.py`**:

```python
@dataclass SubmitJobInput
@dataclass TrainingPolicy   # hyperparameters
@dataclass EvaluationPolicy # threshold, min_samples, wilson_min, cloud settings
@dataclass StoragePolicy    # dir paths
```

---

### 4.4 Infrastructure: Config, Logging, Correlation (`app/infra/`)

**`config.py`** — Pydantic `BaseSettings`, env prefix `HOPE_FT_`:

```python
from app.infra.config import get_settings
s = get_settings()   # cached singleton (functools.lru_cache)
```

`get_settings()` also calls `ensure_dirs()` which creates `workdir/`,
`data_dir/`, `adapters_dir/`, `logs_dir/` if missing.

**`logging.py`** — Structured JSON output, one object per line:

```python
from app.infra.logging import configure_logging, get_logger

configure_logging(level="INFO")
log = get_logger(__name__)
log.info("Training started", extra={"job_id": "job_123", "records": 500})
```

Output includes `correlation_id` field automatically from the ContextVar:

```json
{
  "ts": "2026-05-26T03:00:01.234Z",
  "level": "INFO",
  "logger": "use_cases",
  "correlation_id": "req-abc123",
  "msg": "Training started",
  "job_id": "job_123"
}
```

**`correlation.py`** — `ContextVar[str]("correlation_id", default="-")`.
`CorrelationIdMiddleware` in `app/api/middleware.py` reads or generates the
`X-Correlation-Id` header and sets the ContextVar for every request.
All outgoing HTTP calls in `agent_http.py` and `ollama_http.py` propagate it.

---

### 4.5 Infrastructure: Persistence (`app/infra/persistence.py`)

**`SqliteConnection`** — shared handle with `threading.RLock`, WAL mode.

Four repository classes — each implements the corresponding port Protocol:

| Class                         | Port                    | Key methods                                     |
| ----------------------------- | ----------------------- | ----------------------------------------------- |
| `SqliteJobRepository`         | `JobRepository`         | `create`, `get`, `list`, `update`, `active`     |
| `SqliteAdapterRepository`     | `AdapterRepository`     | `add`, `get`, `get_champion`, `promote`, `list` |
| `SqliteEvaluationRepository`  | `EvaluationRepository`  | `save`, `list`                                  |
| `SqliteTrainingRunRepository` | `TrainingRunRepository` | `save`, `list`                                  |

All methods return domain entity objects (not raw dicts).

---

### 4.6 Infrastructure: HTTP Clients (`app/infra/agent_http.py`, `ollama_http.py`)

**`AgentHttpClient`** (`agent_http.py`) — implements `TrainingDataFetcher`:

```python
client = AgentHttpClient(base_url="http://localhost:5000", token="bearer-token",
                         retry_attempts=3, retry_backoff=1.5)
records = await client.download_dpo(since=..., specialty=..., output=Path(...))
count   = await client.preference_count(since=...)
```

Both methods decorated with:

```python
@retry(stop=stop_after_attempt(3),
       wait=wait_exponential(multiplier=1.5, min=1, max=10),
       retry=retry_if_exception_type(_RETRYABLE))
```

`_RETRYABLE` = `(httpx.TransportError, httpx.TimeoutException)` plus HTTP 5xx.

`hash_jsonl(path)` and `config_hash(dict)` utility functions also live here.

**`OllamaInferenceRegistry`** (`ollama_http.py`) — implements `InferenceRegistry`:

- `register()`: POST `/api/create` with Modelfile (FROM + ADAPTER + PARAMETER + SYSTEM)

**`AgentHttpNotifier`** (`ollama_http.py`) — implements `AgentNotifier`:

- `announce_champion()`: POST `/v1/training/champion/announce`

Both use tenacity retries. Propagate `X-Correlation-Id` header.

---

### 4.7 Infrastructure: ML Adapters (`app/infra/trainer_hf.py`, `evaluator_llm.py`)

**`HuggingFaceTrainer`** (`trainer_hf.py`) — implements `ModelTrainer`:

```python
result: TrainingResult = await trainer.train_dpo(cfg)
# Internally: loop.run_in_executor(None, _train_sync)
# Adds scripts/finetune/ to sys.path, imports trainer.run_training()
```

**`LlmJudgeEvaluator`** (`evaluator_llm.py`) — implements `ChampionEvaluator`:

```python
result: EvaluationResult = await evaluator.evaluate(candidate, champion, ...)
# Internally: loop.run_in_executor(None, _evaluate_sync)
# Adds scripts/finetune/ to sys.path, imports evaluate.evaluate()
```

Both adapters run the blocking ML operations in the default ThreadPoolExecutor
so the asyncio event loop is never blocked.

---

### 4.8 Interfaces: FastAPI app, routers, scheduler (`app/api/`)

**`app.py`** — `create_app() -> FastAPI` factory:

Lifespan startup sequence:

1. `configure_logging()` — JSON to stdout
2. Build `SqliteConnection` → repositories → infra adapters → use cases (DI root)
3. **Startup recovery**: `job_repo.active()` → mark each as `FAILED`
   with `error_detail="service_restart"` (prevents stale "training" status)
4. `ContinuousLearningScheduler.start()`
5. Shutdown: `scheduler.stop()` → `_drain_lock(job_lock, ...)` with
   `asyncio.wait_for(..., timeout=shutdown_grace_seconds)`

**`routers.py`** — four APIRouters: `health`, `jobs`, `adapters`, `evaluations`.
All use `Depends()` for use-case injection — no `app.state` access.

**`middleware.py`**:

- `CorrelationIdMiddleware` — reads/generates `X-Correlation-Id`, echoes in response
- `MetricsMiddleware` — increments Prometheus `REQUESTS` counter + `LATENCY` histogram

**`security.py`** — `require_api_key` FastAPI dependency (header `X-Api-Key`).

**`errors.py`** — maps `DomainError` subclasses to HTTP status codes.

**`scheduling.py`** — `ContinuousLearningScheduler` wraps APScheduler
`AsyncIOScheduler` with `CronTrigger`. Shares the same `asyncio.Semaphore`
as the REST endpoint — auto and manual jobs can never overlap.

---

## 5. Database Schema

```sql
-- A training request. Lifecycle tracked by status column.
CREATE TABLE jobs (
    id              TEXT PRIMARY KEY,     -- "job_<epoch>_<hex8>"
    job_type        TEXT NOT NULL,        -- "sft" | "dpo"
    base_model      TEXT NOT NULL,        -- "Qwen/Qwen3-8B"
    output_tag      TEXT,                 -- adapter tag once training completes
    specialty       TEXT,                 -- NULL = general, else e.g. "cardio"
    status          TEXT NOT NULL,        -- see state machine §6
    progress        TEXT,                 -- JSON snapshot of latest trainer logs
    output_model_tag TEXT,
    elo_score       REAL,                 -- 1000 + elo_delta from evaluation
    win_rate        REAL,                 -- fraction of eval comparisons won
    error_detail    TEXT,
    data_hash       TEXT,                 -- SHA-256 of training dataset
    config_hash     TEXT,
    record_count    INTEGER DEFAULT 0,
    created_at      TEXT NOT NULL,        -- ISO-8601 UTC
    started_at      TEXT,
    finished_at     TEXT
);

-- LoRA adapter weights on disk. One row per training run output.
-- is_champion=1 → currently serving in Ollama.
CREATE TABLE adapters (
    tag             TEXT PRIMARY KEY,     -- "hope-dpo-cardio-<epoch>"
    job_id          TEXT NOT NULL,
    job_type        TEXT NOT NULL,
    specialty       TEXT,
    base_model      TEXT NOT NULL,
    parent_tag      TEXT,                 -- adapter this was resumed from
    path            TEXT NOT NULL,        -- absolute filesystem path
    elo_rating      REAL DEFAULT 1000.0,
    is_champion     INTEGER DEFAULT 0,    -- 0|1 boolean
    promoted_at     TEXT,
    retired_at      TEXT,
    created_at      TEXT NOT NULL
);

-- One row per champion-vs-challenger evaluation.
CREATE TABLE evaluations (
    id              TEXT PRIMARY KEY,     -- "eval_<epoch>_<hex8>"
    job_id          TEXT NOT NULL,
    candidate_tag   TEXT NOT NULL,
    champion_tag    TEXT,                 -- NULL on cold start
    suite           TEXT NOT NULL,        -- filename of golden suite
    total           INTEGER NOT NULL,
    candidate_wins  INTEGER NOT NULL,
    ties            INTEGER NOT NULL,
    champion_wins   INTEGER NOT NULL,
    win_rate        REAL NOT NULL,        -- (candidate_wins + 0.5*ties) / total
    elo_delta       REAL NOT NULL,
    promoted        INTEGER NOT NULL,     -- 0|1
    detail_json     TEXT,                 -- first 20 per-item verdicts
    created_at      TEXT NOT NULL
);

-- Per-phase training metrics (loss curves etc.).
CREATE TABLE training_runs (
    id              TEXT PRIMARY KEY,
    job_id          TEXT NOT NULL,
    phase           TEXT NOT NULL,        -- "sft" | "dpo"
    metrics_json    TEXT,                 -- last 50 step logs
    final_loss      REAL,
    final_eval_loss REAL,
    samples         INTEGER,
    steps           INTEGER,
    duration_sec    REAL,
    succeeded       INTEGER NOT NULL,
    error_detail    TEXT,
    created_at      TEXT NOT NULL
);
```

---

## 6. Job Status State Machine

```
                          ┌──────────┐
                          │ pending  │  created by API or scheduler
                          └────┬─────┘
                               │ orchestrator starts
                               ▼
                          ┌──────────┐
                          │preparing │  fetching data from Hope.Agent
                          └────┬─────┘
                    ┌──────────┘
             0 records│          ≥1 records
                    ▼           ▼
               ┌──────────┐ ┌──────────┐
               │completed │ │ training │  LoRA/QLoRA running
               │(no data) │ └────┬─────┘
               └──────────┘      │ training done
                                 ▼
                          ┌──────────────┐
                          │  evaluating  │  champion vs challenger
                          └──────┬───────┘
                   ┌─────────────┘
          promoted │               did not beat
                   ▼               champion ▼
             ┌──────────┐       ┌──────────────┐
             │ promoted │       │  completed   │
             └──────────┘       │(not promoted)│
                                └──────────────┘

    Any state except completed/promoted/cancelled:
                    ▼ (on exception)
               ┌──────────┐
               │  failed  │ error_detail contains exception message
               └──────────┘

    Any active state:
                    ▼ (DELETE /jobs/{id})
               ┌────────────┐
               │ cancelled  │ best-effort, training thread may not stop
               └────────────┘
```

---

## 7. Champion Promotion Logic

Promotion is decided by `decide_promotion()` in `app/domain.py`, called from
`RunDpoCycleUseCase`. Three gates must **all** pass:

```python
# app/domain.py :: decide_promotion()
win_rate     = (candidate_wins + 0.5 * ties) / total_items
wlb          = wilson_lower_bound(candidate_wins, scored_items)  # 95% CI lower bound
promote      = (scored_items  >= eval_min_samples          # gate 1: sample size
             and win_rate     >= eval_win_rate_promote     # gate 2: win rate (default 0.55)
             and wlb          >= eval_wilson_min)          # gate 3: statistical significance

# AdapterRepository.promote(tag):
# 1. Find current champion for (specialty, job_type)
# 2. Set is_champion=0, retired_at=now  on old champion
# 3. Set is_champion=1, promoted_at=now on new adapter
#    elo_rating += elo_delta
```

**Wilson Score CI lower bound** (gate 3):

$$\hat{p}_{low} = \frac{\hat{p} + \dfrac{z^2}{2n} - z\sqrt{\dfrac{\hat{p}(1-\hat{p})}{n} + \dfrac{z^2}{4n^2}}}{1 + \dfrac{z^2}{n}}$$

Where $\hat{p}$ = win_rate, $n$ = scored items (non-ties), $z = 1.96$ (95% CI).

Setting `HOPE_FT_EVAL_WILSON_MIN=0.50` means the 95% CI lower bound must be ≥ 50%
before promotion — statistical confirmation that the challenger is genuinely better
rather than a lucky run on a small evaluation suite.

**Why 55% win-rate and not 50%?**  
Ties count as 0.5 wins for the candidate. A 50% threshold would promote even
when the models are statistically indistinguishable. 55% requires a clearer
signal that the challenger is genuinely better.

**Cold start** (no prior champion): `evaluate()` returns `promote=True`
immediately. The first adapter always becomes champion without comparison.

**No golden suite file**: same as cold start — auto-promote. Create the golden
suite file at `$HOPE_FT_DATA_DIR/suites/golden_<specialty>.jsonl` to enable
comparative evaluation.

---

## 8. Elo Rating System

Each decided comparison (non-tie) updates both adapters using the standard
Elo formula with K=32:

$$E_a = \frac{1}{1 + 10^{(R_b - R_a) / 400}}$$

$$R'_a = R_a + K \cdot (S_a - E_a)$$

Where $S_a = 1$ (candidate won), $0$ (champion won), or $0.5$ (tie).

Both adapters start at 1000. The `elo_delta` stored in the evaluation and
applied to the adapter's `elo_rating` is `candidate_elo_after - 1000`.

The `elo_rating` in the `adapters` table accumulates across all evaluations
the adapter has participated in — it is a lifetime performance score.

---

## 9. REST API Reference

All endpoints (except `/healthz`, `/readyz`, `/metrics`) require:

```
X-Api-Key: <HOPE_FT_API_KEY>
```

### POST `/jobs`

Submit a fine-tuning cycle.

**Request body** (`application/json`):

```json
{
  "job_type": "dpo", // "dpo" only for now
  "specialty": "cardio", // null = general pool
  "since": "2026-01-01T00:00:00Z", // null = all history
  "max_records": 3000 // 1–50000, default 5000
}
```

**Response** `200 JobOut` — the freshly created job (status will be "pending").  
The actual training runs asynchronously. Poll `GET /jobs/{id}` for updates.

---

### GET `/jobs?take=50`

Returns array of `JobOut` ordered by `created_at DESC`.

---

### GET `/jobs/{id}`

```json
{
  "id": "job_1748217600_a3f2b891",
  "job_type": "dpo",
  "base_model": "Qwen/Qwen3-8B",
  "output_tag": "hope-dpo-cardio-1748220000",
  "status": "promoted",
  "record_count": 1250,
  "win_rate": 0.623,
  "elo_score": 1027.4,
  "error_detail": null,
  "progress": "{\"step\": 480, \"loss\": 0.1823}",
  "created_at": "2026-05-26T03:00:00Z",
  "started_at": "2026-05-26T03:00:01Z",
  "finished_at": "2026-05-26T05:41:33Z"
}
```

---

### DELETE `/jobs/{id}`

Best-effort cancellation. Sets status to "cancelled" in the registry.
The training thread (in the executor) may not stop immediately — it will
abort at the next NaN/time-budget check point.

---

### GET `/adapters?specialty=cardio&take=20`

Returns all adapters sorted by `elo_rating DESC`.

---

### GET `/champion?specialty=cardio&type=dpo`

Returns the single `is_champion=1` adapter row for the given bucket.
`404` if none exists yet.

---

### POST `/champion/{tag}/promote`

Manual promotion override. Sets `is_champion=1` on the given tag and
retires the current champion. Useful for rolling back to a previous adapter:

```bash
curl -X POST http://localhost:8765/champion/hope-dpo-cardio-v1/promote \
  -H "X-Api-Key: $HOPE_FT_API_KEY"
```

---

### GET `/evaluations?take=20`

Recent evaluation results with win rates, Elo deltas, and promote decisions.

---

### GET `/metrics`

Prometheus text format. Key metrics:

| Metric                                 | Type      | Description                     |
| -------------------------------------- | --------- | ------------------------------- |
| `hope_ft_requests_total{route,status}` | Counter   | HTTP requests by route + status |
| `hope_ft_request_seconds{route}`       | Histogram | Request latency                 |
| `hope_ft_active_jobs`                  | Gauge     | Currently running training jobs |
| `hope_ft_promotions_total`             | Counter   | Total successful promotions     |
| `hope_ft_failures_total`               | Counter   | Total failed jobs               |

---

## 10. Configuration Reference

All settings use prefix `HOPE_FT_`. See [.env.example](.env.example) for a
complete template.

| Variable                              | Default                              | Description                                                                                                              |
| ------------------------------------- | ------------------------------------ | ------------------------------------------------------------------------------------------------------------------------ |
| `HOPE_FT_HOST`                        | `0.0.0.0`                            | Bind address                                                                                                             |
| `HOPE_FT_PORT`                        | `8765`                               | HTTP port                                                                                                                |
| `HOPE_FT_API_KEY`                     | `""` (auth off)                      | Required header value on all endpoints                                                                                   |
| `HOPE_FT_AGENT_API_URL`               | `http://localhost:5000`              | Hope.Agent .NET base URL                                                                                                 |
| `HOPE_FT_AGENT_API_TOKEN`             | `""`                                 | Bearer token for Hope.Agent API                                                                                          |
| `HOPE_FT_BASE_MODEL`                  | `Qwen/Qwen3-8B`                      | HuggingFace model ID                                                                                                     |
| `HOPE_FT_LOAD_IN_4BIT`                | `true`                               | QLoRA 4-bit quantisation                                                                                                 |
| `HOPE_FT_MAX_SEQ_LENGTH`              | `2048`                               | Token context window                                                                                                     |
| `HOPE_FT_LORA_RANK`                   | `64`                                 | LoRA rank r                                                                                                              |
| `HOPE_FT_LORA_ALPHA`                  | `16`                                 | LoRA scaling α                                                                                                           |
| `HOPE_FT_DPO_EPOCHS`                  | `1`                                  | DPO training epochs                                                                                                      |
| `HOPE_FT_DPO_LR`                      | `5e-6`                               | DPO learning rate                                                                                                        |
| `HOPE_FT_DPO_BATCH`                   | `1`                                  | Per-device batch size                                                                                                    |
| `HOPE_FT_DPO_GRAD_ACCUM`              | `16`                                 | Gradient accumulation steps                                                                                              |
| `HOPE_FT_DPO_BETA`                    | `0.1`                                | DPO divergence coefficient β                                                                                             |
| `HOPE_FT_EVAL_MIN_SAMPLES`            | `50`                                 | Min decided comparisons to allow promotion                                                                               |
| `HOPE_FT_EVAL_WIN_RATE_PROMOTE`       | `0.55`                               | Win-rate threshold for promotion                                                                                         |
| `HOPE_FT_EVAL_JUDGE_MODEL`            | `qwen2.5:7b-instruct`                | Ollama judge model tag                                                                                                   |
| `HOPE_FT_EVAL_JUDGE_URL`              | `http://localhost:11434`             | Ollama URL for judge                                                                                                     |
| `HOPE_FT_AUTO_TRAIN_ENABLED`          | `false`                              | Enable cron scheduler                                                                                                    |
| `HOPE_FT_AUTO_TRAIN_CRON`             | `0 3 * * *`                          | Cron expression (UTC)                                                                                                    |
| `HOPE_FT_AUTO_TRAIN_MIN_NEW_PAIRS`    | `200`                                | Min new preference pairs to train                                                                                        |
| `HOPE_FT_OLLAMA_URL`                  | `http://localhost:11434`             | Ollama registration URL                                                                                                  |
| `HOPE_FT_OLLAMA_AUTO_REGISTER`        | `true`                               | Register on promotion                                                                                                    |
| `HOPE_FT_MAX_CONCURRENT_JOBS`         | `1`                                  | Max parallel training jobs                                                                                               |
| `HOPE_FT_MAX_RUNTIME_HOURS`           | `12.0`                               | Hard wall-clock limit per job                                                                                            |
| `HOPE_FT_NAN_CHECK_EVERY_N_STEPS`     | `50`                                 | Steps between NaN loss checks                                                                                            |
| `HOPE_FT_DB_PATH`                     | `/var/lib/hope-finetune/registry.db` | SQLite file                                                                                                              |
| `HOPE_FT_DATA_DIR`                    | `/var/lib/hope-finetune/data`        | Downloaded JSONL files                                                                                                   |
| `HOPE_FT_ADAPTERS_DIR`                | `/var/lib/hope-finetune/adapters`    | Adapter weights                                                                                                          |
| `HOPE_FT_LOGS_DIR`                    | `/var/lib/hope-finetune/logs`        | Log file directory                                                                                                       |
| `HOPE_FT_EVAL_WILSON_MIN`             | `0.0`                                | Wilson CI lower bound required for promotion. Set to `0.50` for statistical significance gating (production recommended) |
| `HOPE_FT_EVAL_CLOUD_BASELINE_MODEL`   | `""`                                 | If non-empty (e.g. `gpt-4o-mini`), also compare promoted adapter vs cloud baseline                                       |
| `HOPE_FT_EVAL_CLOUD_WIN_RATE_PROMOTE` | `0.58`                               | Win-rate threshold for cloud baseline comparison                                                                         |
| `HOPE_FT_EVAL_CLOUD_WILSON_MIN`       | `0.50`                               | Wilson CI minimum for cloud baseline comparison                                                                          |
| `HOPE_FT_EVAL_CLOUD_MIN_SAMPLES`      | `100`                                | Minimum samples for cloud baseline comparison                                                                            |
| `HOPE_FT_HTTP_RETRY_ATTEMPTS`         | `3`                                  | Tenacity retry attempts for HTTP calls to Hope.Agent and Ollama                                                          |
| `HOPE_FT_HTTP_RETRY_BACKOFF_SECONDS`  | `1.5`                                | Exponential backoff base (seconds) for HTTP retries                                                                      |
| `HOPE_FT_SHUTDOWN_GRACE_SECONDS`      | `30.0`                               | Seconds to wait for active jobs to finish before forced shutdown                                                         |

---

## 11. Extension Points

### Add SFT training to the cycle

`trainer.py` already supports `mode="sft"`. To add an SFT phase:

1. Add `download_sft()` call in `RunDpoCycleUseCase` (`app/use_cases.py`) before the DPO call.
2. Create a `TrainConfig(mode="sft", ...)` and call `run_training(cfg)`.
3. Use the SFT adapter as `resume_from` for the DPO phase.
4. Register the SFT adapter in the registry with `job_type="sft"`.

### Add a new specialty

No code changes needed. Pass `specialty="nephrology"` in `POST /jobs` and
create the golden suite at `$HOPE_FT_DATA_DIR/suites/golden_nephrology.jsonl`.
Champions are tracked per `(specialty, job_type)` bucket independently.

### Replace the LLM judge

Modify `evaluate.py :: _judge()`. The function contract is:

```python
def _judge(prompt_text: str, answer_a: str, answer_b: str, ...) -> str:
    # Must return "A", "B", or "TIE"
```

Alternative scorers: perplexity-based, ROUGE-L, BERTScore — just implement
the same return signature.

### Add a new API endpoint

1. Add a new use case in `app/use_cases.py` (keep logic out of the route handler).
2. Add it to the DI composition root in `app/api/app.py :: create_app()`.
3. Add a route in `app/api/routers.py`. Inject via `Depends()` — do not
   access `app.state` directly.
4. Add the `Depends(require_api_key)` guard where needed.

### Integrate a different base model

Change `HOPE_FT_BASE_MODEL`. The LoRA target modules list in `trainer.py ::
_apply_lora()` is:

```python
target_modules=["q_proj","k_proj","v_proj","o_proj",
                "gate_proj","up_proj","down_proj"]
```

This covers Llama/Qwen/Mistral architectures. For Phi or Falcon, adjust the
list to match the model's attention projection names.

---

## 12. Local Development Setup

```bash
# 1. Clone / navigate
cd scripts/finetune

# 2. Create virtual environment
python -m venv .venv
# Windows:
.\.venv\Scripts\activate
# Linux/Mac:
source .venv/bin/activate

# 3. Install dependencies (CPU-only for dev — torch without CUDA)
pip install -r requirements.txt

# 4. Create .env for local dev
cp .env.example .env
# Edit .env:
#   HOPE_FT_API_KEY=dev
#   HOPE_FT_LOAD_IN_4BIT=false
#   HOPE_FT_AGENT_API_URL=http://localhost:5000
#   HOPE_FT_AUTO_TRAIN_ENABLED=false
#   HOPE_FT_WORKDIR=./local_state

# 5. Start service
# Option A — using the entry-point script:
python main.py

# Option B — uvicorn directly with auto-reload:
uvicorn app.api.app:create_app --factory --reload --port 8765

# 6. Verify
curl http://localhost:8765/healthz
# → {"status":"ok"}

curl http://localhost:8765/readyz
# → {"status":"ready"}
```

**Submitting a test job** (no GPU needed — it will fail at the training step
if no CUDA device, but all use-case/data-fetch logic runs):

```bash
curl -X POST http://localhost:8765/jobs \
  -H "X-Api-Key: dev" \
  -H "Content-Type: application/json" \
  -d '{"specialty":null,"max_records":100}'

# poll
curl -H "X-Api-Key: dev" http://localhost:8765/jobs
```

**Interactive API docs**: `http://localhost:8765/docs` (Swagger UI).

---

## 13. Testing

```bash
cd scripts/finetune
python -m pytest tests/ -v --no-header
```

`pytest.ini` sets `asyncio_mode = auto` — all async test functions run
without explicit `@pytest.mark.asyncio`.

Test files (18 tests total, all pass without GPU):

| File                 | What it tests                                                       | Tests |
| -------------------- | ------------------------------------------------------------------- | ----- |
| `test_clean_arch.py` | Domain entities, `decide_promotion()`, Wilson CI math, SQLite repos | 10    |
| `test_agent_http.py` | HTTP client tenacity retries (mocked transport)                     | 3     |
| `test_registry.py`   | Champion promotion, evaluation recording (uses entity API)          | 2     |
| `test_hashing.py`    | SHA-256 stability across line reordering, `config_hash` determinism | 2     |
| `test_elo.py`        | Expected score symmetry, winner gains rating, ties are zero-sum     | 1     |

**Adding a test**:

- Tests that only need `app/domain.py`, `app/infra/persistence.py`, or pure
  functions can run without a GPU. Use `tmp_path` for SQLite db paths.
- Tests that call `trainer.py :: run_training()` need a CUDA device — mark
  with `@pytest.mark.gpu` and gate in CI.
- When testing use cases, pass stub objects that satisfy the port `Protocol`
  via structural subtyping (no mock framework needed for simple cases).

---

## 14. Observability

### Structured logs

Every log line is a JSON object. Filter in production:

```bash
# All events for a specific job
docker compose logs hope-finetune | jq 'select(.job_id=="job_1748217600_a3f2b891")'

# All promotions
docker compose logs hope-finetune | jq 'select(.msg | contains("Promoted"))'

# All warnings and above
docker compose logs hope-finetune | jq 'select(.level | IN("WARNING","ERROR","CRITICAL"))'
```

### Prometheus

Scrape `http://hope-finetune:8765/metrics`. Example PromQL:

```promql
# Promotion rate over 7 days
increase(hope_ft_promotions_total[7d])

# Job failure rate
rate(hope_ft_failures_total[1h]) / rate(hope_ft_requests_total{route="/jobs"}[1h])

# Active training (should be 0 or 1)
hope_ft_active_jobs
```

### SQLite inspection

```bash
# Connect to registry
sqlite3 /var/lib/hope-finetune/registry.db

# Recent jobs
SELECT id, status, record_count, win_rate, finished_at
FROM jobs ORDER BY created_at DESC LIMIT 10;

# Current champions
SELECT tag, specialty, elo_rating, promoted_at, path
FROM adapters WHERE is_champion=1;

# Promotion history
SELECT candidate_tag, champion_tag, win_rate, elo_delta, promoted, created_at
FROM evaluations ORDER BY created_at DESC LIMIT 20;
```

---

## 15. Common Failure Modes

| Symptom                                | Likely cause                                               | Fix                                                                                                                 |
| -------------------------------------- | ---------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------- |
| Job stuck at `preparing`               | Hope.Agent API unreachable                                 | Check `HOPE_FT_AGENT_API_URL`, `HOPE_FT_AGENT_API_TOKEN`, network connectivity                                      |
| Job failed: `no_new_data`              | All data already fetched, or `since` filter too narrow     | Leave `since` as `null` to pull all history, or expand the date range                                               |
| Job failed: NaN loss                   | Learning rate too high, bad data batch, fp16 overflow      | Lower `HOPE_FT_DPO_LR` (try `1e-6`), ensure data has no empty responses                                             |
| Job failed: CUDA OOM                   | Not enough VRAM                                            | Set `HOPE_FT_LOAD_IN_4BIT=true`, reduce `HOPE_FT_DPO_BATCH` to 1, reduce `HOPE_FT_MAX_SEQ_LENGTH` to 1024           |
| Evaluation: judge always returns TIE   | Judge model too weak or prompt language mismatch           | Use a stronger judge (`HOPE_FT_EVAL_JUDGE_MODEL`), verify Ollama has the model pulled                               |
| Adapter not showing in Ollama          | `ollama_auto_register=true` but Ollama unreachable         | Check `HOPE_FT_OLLAMA_URL`, then call `POST /champion/{tag}/promote` to retry                                       |
| Scheduler never fires                  | `HOPE_FT_AUTO_TRAIN_ENABLED=false`                         | Set to `true` and restart. Check cron expression with `crontab.guru`                                                |
| Registry schema missing columns        | Old `registry.db` from pre-v15                             | Delete `registry.db` (jobs history lost) or run `ALTER TABLE` manually to add missing columns                       |
| Jobs stuck in `training` after restart | Service restarted mid-job                                  | On startup, lifespan auto-marks active jobs as `FAILED` (error_detail=`service_restart`). Resubmit via `POST /jobs` |
| Retries exhausted on data fetch        | Hope.Agent temporarily unavailable during 3 retry attempts | Increase `HOPE_FT_HTTP_RETRY_ATTEMPTS` or check Hope.Agent health before submitting jobs                            |
| Hope.Agent not routing to new model    | `notify_agent_promotion` failed                            | Check network, then call `POST /champion/{tag}/promote` to re-trigger notification                                  |

---

## 16. Scenario Analysis — Tình Huống Thực Tế

Phần này phân tích các tình huống điển hình mà kỹ sư sẽ gặp trong vận hành thực tế,
kèm cây quyết định và hướng xử lý cụ thể.

---

### 16.1 Cold Start — Lần Đầu Deploy

**Tình huống**: Hệ thống vừa được cài đặt, chưa có bất kỳ adapter nào trong registry.

```
Bác sĩ sử dụng Hope.Agent ──► câu trả lời từ base model
  Bác sĩ rate "not helpful" ──► preference pair được lưu vào DB
  (lặp lại N lần...)

Sau khi đạt min_new_pairs = 200:
  Scheduler kích hoạt hoặc dev gọi POST /jobs
  │
  ├── data_fetcher: tải 200+ preference pairs
  ├── trainer: tạo adapter V1 từ base model (resume_from = None)
  ├── evaluate:
  │     champion_path = None  ──► auto-promote = True (không so sánh)
  └── registry.promote_adapter("hope-dpo-general-v1")
        is_champion = 1
        elo_rating  = 1000  (chưa thi đấu)

Kết quả: Model V1 trở thành champion, bắt đầu phục vụ inference.
```

**Điểm chú ý**: Trong cold start, bất kỳ adapter nào cũng được promote ngay lập tức
vì không có đối thủ để so sánh. Đây là hành vi cố ý — đội dev cần giám sát quality
của V1 thủ công bằng cách kiểm tra golden suite sau khi tạo nó.

---

### 16.2 Thăng Hạng Bình Thường — Challenger Thắng

**Tình huống**: V1 đang là champion. Qua 2 tuần vận hành, có thêm 600 preference pairs mới.

```
Ngày 14, 03:00 UTC — Scheduler tick
  preference_count(since=last_run) = 600 ≥ 200 → tiến hành

Orchestrator chạy DPO cycle:
  trainer:  resume_from = "adapters/hope-dpo-v1"
            ─► LoRA adapter V2 kế thừa weights của V1
            ─► trained trên 540 samples (10% holdout = 60 val)

  evaluate: generate responses cho 100 câu golden suite
    V2 wins = 62,  ties = 8,  V1 wins = 30
    win_rate = (62 + 0.5×8) / 100 = 0.66  ≥  threshold 0.55  ✓
    scored   = 62 + 30 = 92                ≥  min_samples 50  ✓
    wlb      = wilson_lower_bound(62, 92)  ≈  0.56  ≥  wilson_min 0.0  ✓

  decide_promotion → promote=True

  Elo update (K=32):
    E_V2 = 1/(1+10^0)    = 0.500
    V2_new = 1000 + 32×(1-0.5) = 1016   (cho mỗi win)
    ... (tích lũy qua 92 so sánh → elo_delta ≈ +22)

  promote → V2 trở thành champion
           → V1: is_champion=0, retired_at=now
           → Ollama: POST /api/create {name:"hope-dpo-v2"}
           → Hope.Agent: POST /v1/training/champion/announce
```

**Kết quả quan sát được trong logs**:

```json
{"msg":"Eval complete","win_rate":0.66,"elo_delta":22.1,"promote":true}
{"msg":"Promoted new champion","tag":"hope-dpo-general-1748390400"}
```

---

### 16.3 Challenger Thua — Champion Giữ Ngôi

**Tình huống**: Dữ liệu mới chứa nhiều preference pairs nhiễu (bác sĩ click ngẫu nhiên).

```
DPO cycle chạy với 250 pairs nhiễu:
  trainer: loss không giảm rõ ràng (cuối cùng = 0.98, ban đầu = 1.02)

  evaluate: V2 vs V1
    V2 wins = 48,  ties = 12,  V1 wins = 40
    win_rate = (48 + 6) / 100 = 0.54  <  threshold 0.55  ✗

Decision tree:
  win_rate 0.54 < 0.55  ──► KHÔNG promote
  V1 tiếp tục là champion
  V2 được lưu trong registry với is_champion=0
  job.status = "completed" (error_detail = "did_not_beat_champion")

Log:
  {"msg":"Candidate did NOT beat champion — not promoted","tag":"hope-dpo-general-1748476800"}
```

**Lợi ích của cơ chế này**: Dữ liệu kém chất lượng không gây hại cho production model.
Adapter V2 vẫn được lưu trong registry — dev có thể điều tra sau bằng:

```bash
GET /evaluations?take=5
# xem win_rate, elo_delta của từng evaluation
```

---

### 16.4 NaN Loss — Training Tự Dừng

**Tình huống**: Learning rate quá cao hoặc batch chứa tokenization lỗi.

```
DPO training bắt đầu, step 150:
  loss = 0.42 → 0.38 → 0.31 → NaN → NaN → NaN

NaNGuardCallback.on_log():
  step=150: loss=NaN  → bad_count = 1
  step=200: loss=NaN  → bad_count = 2
  step=250: loss=NaN  → bad_count = 3  ≥ max_bad_steps=3
    control.should_training_stop = True

Trainer dừng sớm.
TrainResult.final_loss = None

orchestrator:
  record_run(succeeded=True, final_loss=None)  ← vẫn lưu metrics
  add_adapter(...)  ← adapter được lưu (trạng thái partial)

evaluate: adapter partial thường thua champion  ─► KHÔNG promote
job.status = "completed" (not promoted)

Cây quyết định sửa lỗi:
  Kiểm tra loss curve (training_runs.metrics_json)
  │
  ├── loss tăng từ step 0 → điều chỉnh learning_rate (HOPE_FT_DPO_LR 5e-6 → 1e-6)
  ├── loss = NaN từ step 1 → kiểm tra data format (empty responses, invalid JSON)
  └── loss ổn định ở 0.9+ → data không đủ thông tin, cần thêm preference pairs
```

---

### 16.5 CUDA OOM — Hết VRAM

**Tình huống**: Qwen3-8B + QLoRA + batch_size=4 + seq_length=4096 → OOM.

```
RuntimeError: CUDA out of memory
  ↓
orchestrator catch Exception:
  registry.update_job(status="failed", error_detail="CUDA out of memory...")
  job.status = "failed"

Cây quyết định:

VRAM hiện có?
├── < 12 GB  → Không khả thi với Qwen3-8B, dùng Qwen3-4B
├── 12–16 GB → load_in_4bit=True, batch=1, seq=1024, grad_accum=32
├── 16–24 GB → load_in_4bit=True, batch=2, seq=2048, grad_accum=16
└── ≥ 24 GB  → load_in_4bit=False (bf16), batch=2, seq=4096

Bảng VRAM ước tính cho Qwen3-8B:
  | Mode        | batch | seq  | VRAM  |
  |-------------|-------|------|-------|
  | QLoRA 4-bit |   1   | 1024 | ~10GB |
  | QLoRA 4-bit |   1   | 2048 | ~14GB |
  | QLoRA 4-bit |   2   | 2048 | ~18GB |
  | LoRA bf16   |   1   | 2048 | ~22GB |
  | LoRA bf16   |   2   | 2048 | ~28GB |
```

---

### 16.6 Catastrophic Forgetting — Mô Hình "Quên" Kiến Thức Cũ

**Tình huống**: V3 được trained với DPO_BETA = 0.01 (quá thấp) → overfit vào preference
data, mất khả năng trả lời câu hỏi general.

```
evaluate: V3 vs V2
  Golden suite Q1-Q50: cardiology questions (domain của training data)
    V3 wins = 38, ties = 5, V2 wins = 7 ──► V3 thắng áp đảo
  Golden suite Q51-Q100: general medical (ngoài training domain)
    V3 wins = 10, ties = 8, V2 wins = 32 ──► V3 thua rõ

  Overall win_rate = (48 + 6.5) / 100 = 0.545  <  0.55  ──► KHÔNG promote

Phân tích:
  Vấn đề: DPO_BETA quá thấp → mô hình chạy quá xa khỏi reference policy
  Giải pháp: Tăng HOPE_FT_DPO_BETA từ 0.01 lên 0.1 (default)
  Insight: Nên chia golden suite theo domain để phát hiện vấn đề này.

Golden suite tốt nên bao gồm:
  - 30% câu hỏi trong specialty đang train
  - 40% câu hỏi y khoa general
  - 20% câu hỏi an toàn (phân biệt "không biết" vs "đưa ra sai")
  - 10% câu hỏi biên giới (edge cases)
```

---

### 16.7 Specialty Champion Riêng Biệt

**Tình huống**: Bệnh viện muốn model cardio tốt hơn model general.

```
Trạng thái registry hiện tại:
  adapters:
    hope-dpo-general-v3  (specialty=NULL, is_champion=1, elo=1042)

POST /jobs {"specialty": "cardio", "max_records": 1000}
  │
  ├── data_fetcher: chỉ tải pairs có specialty="cardio"
  ├── trainer: resume_from = None  ← KHÔNG inherit từ general champion
  │            (khác bucket: general vs cardio)
  └── evaluate: champion = get_champion("cardio", "dpo") = None
                → cold start → auto-promote

Sau job:
  adapters:
    hope-dpo-general-v3  (specialty=NULL,    is_champion=1, elo=1042)
    hope-dpo-cardio-v1   (specialty="cardio", is_champion=1, elo=1000)

GET /champion?specialty=cardio → hope-dpo-cardio-v1
GET /champion                  → hope-dpo-general-v3

Hope.Agent AdaptiveRouter:
  câu hỏi tim mạch → route → hope-dpo-cardio-v1
  câu hỏi khác     → route → hope-dpo-general-v3
```

**Lưu ý cho dev**: Để specialty champion kế thừa từ general champion,
cần thêm logic trong `RunDpoCycleUseCase` (`app/use_cases.py`) để fallback `resume_from` về general
champion nếu specialty champion chưa có.

---

## 17. Chi Tiết Thuật Toán

### 17.1 Thuật Toán DPO Training Loop

```
Algorithm 1: run_training (mode = "dpo")
─────────────────────────────────────────────────────────────
Input:  cfg: TrainConfig (base_model, train_file, resume_from, ...)
Output: result: TrainResult (final_loss, steps, duration_sec, metrics)

1.  seed_everything(cfg.seed = 42)
      random.seed(42); numpy.seed(42); torch.manual_seed(42)
      torch.cuda.manual_seed_all(42)
      os.environ["CUBLAS_WORKSPACE_CONFIG"] = ":4096:8"

2.  tokenizer ← AutoTokenizer.from_pretrained(cfg.base_model)
      if tokenizer.pad_token is None:
          tokenizer.pad_token ← tokenizer.eos_token

3.  if cfg.load_in_4bit:
        bnb_config ← BitsAndBytesConfig(
            load_in_4bit=True,
            bnb_4bit_quant_type="nf4",
            bnb_4bit_compute_dtype=bfloat16,
            bnb_4bit_use_double_quant=True
        )
    model ← AutoModelForCausalLM.from_pretrained(
        cfg.base_model, quantization_config=bnb_config,
        device_map="auto", torch_dtype=bfloat16
    )

4.  if cfg.resume_from is not None:
        model ← PeftModel.from_pretrained(
            model, cfg.resume_from, is_trainable=True)
        // Tiếp tục training từ weights của champion
    else:
        lora_config ← LoraConfig(
            r=cfg.lora_rank,             // default 64
            lora_alpha=cfg.lora_alpha,   // default 128 (2 × rank)
            target_modules=["q_proj","k_proj","v_proj","o_proj",
                            "gate_proj","up_proj","down_proj"],
            lora_dropout=cfg.lora_dropout, // default 0.05
            bias="none",
            task_type=CAUSAL_LM,
            use_rslora=cfg.use_rslora,   // default True: alpha/sqrt(r)
            use_dora=cfg.use_dora        // default False
        )
        model ← get_peft_model(model, lora_config)

5.  dataset_train ← load_dataset("json", data_files=cfg.train_file)
    dataset_val   ← load_dataset("json", data_files=cfg.val_file)
    // Mỗi record: {"prompt":..., "chosen":..., "rejected":...}

6.  training_args ← DPOConfig(
        num_train_epochs       = cfg.epochs,
        per_device_train_batch = cfg.batch_size,
        gradient_accumulation_steps = cfg.grad_accum,
        learning_rate          = cfg.dpo_learning_rate, // default 5e-6 (KHÔNG dùng cfg.learning_rate)
        lr_scheduler_type      = "cosine",
        warmup_ratio           = 0.03,
        bf16                   = True,
        gradient_checkpointing = True,
        max_grad_norm          = cfg.max_grad_norm,     // default 0.3
        loss_type              = cfg.dpo_loss_type,     // default "ipo"
        ...
    )

7.  callbacks ← [
        NaNGuardCallback(check_every=cfg.nan_check_every),
        TimeBudgetCallback(max_seconds=cfg.max_runtime_seconds),
        MetricSink()
    ]

8.  trainer ← DPOTrainer(
        model=model, ref_model=None,    // None → model tự đóng vai trò ref (implicit ref)
        beta=cfg.dpo_beta,              // default 0.1
        args=training_args,
        train_dataset=dataset_train,
        eval_dataset=dataset_val,
        processing_class=tokenizer,
        callbacks=callbacks
    )

// Lưu ý quan trọng về DPO learning rate:
// cfg.dpo_learning_rate = 5e-6 (KHÔNG dùng cfg.learning_rate = 2e-4)
// DPO LR cao (>1e-5) → model collapse: xác suất chosen và rejected đều giảm
// DPO LR quá thấp (<1e-7) → gradient quá nhỏ, không học được gì

9.  t_start ← time.time()
    trainer.train()                    // blocking call
    duration ← time.time() - t_start

10. trainer.save_model(cfg.output_dir)
    tokenizer.save_pretrained(cfg.output_dir)

11. return TrainResult(
        output_dir      = cfg.output_dir,
        final_loss      = last valid loss from MetricSink,
        final_eval_loss = last eval_loss from MetricSink,
        steps           = trainer.state.global_step,
        samples         = len(dataset_train),
        duration_sec    = duration,
        metrics         = MetricSink.records[-50:]
    )
```

---

### 17.2 Thuật Toán Champion-vs-Challenger Evaluation

```
Algorithm 2: evaluate (champion vs candidate)
─────────────────────────────────────────────────────────────
Input:  candidate_path, champion_path, suite_path, judge_url/model
Output: EvalResult (win_rate, elo_delta, promote, details)

1.  items ← load_suite(suite_path)     // JSONL, tối đa max_items=200
    if len(items) < min_samples: raise ValueError

2.  if champion_path is None:
        return EvalResult(promote=True, ...)  // Cold start

3.  // Generate responses — chạy tuần tự để tránh OOM
    cand_answers  ← _generate(candidate_path, base_model, items)
    champ_answers ← _generate(champion_path,  base_model, items)

4.  cand_wins = champ_wins = ties = 0
    cand_elo = champ_elo = 1000.0

    for idx, (item, cand_ans, champ_ans) in enumerate(zip(...)):
        prompt_text ← flatten(item["prompt"])

        // Order swap để giảm positional bias
        if idx % 2 == 0:
            verdict ← judge(prompt, A=cand_ans, B=champ_ans)
            winner  ← {A:"candidate", B:"champion", TIE:"tie"}[verdict]
        else:
            verdict ← judge(prompt, A=champ_ans, B=cand_ans)
            winner  ← {A:"champion", B:"candidate", TIE:"tie"}[verdict]

        // Đếm wins
        if winner == "candidate": cand_wins += 1
        elif winner == "champion": champ_wins += 1
        else: ties += 1

        // Cập nhật Elo sau mỗi so sánh
        S_cand ← 1.0 if winner=="candidate" else (0.5 if winner=="tie" else 0.0)
        cand_elo, champ_elo ← elo_update(cand_elo, champ_elo, S_cand, K=32)

5.  win_rate  ← (cand_wins + 0.5 × ties) / len(items)
    elo_delta ← cand_elo - 1000.0
    scored    ← cand_wins + champ_wins
    promote   ← (win_rate ≥ threshold) AND (scored ≥ min_samples)

6.  return EvalResult(
        total=len(items), candidate_wins=cand_wins,
        champion_wins=champ_wins, ties=ties,
        win_rate=win_rate, elo_delta=elo_delta,
        promote=promote, details=[...]
    )
```

---

### 17.3 Thuật Toán NaN Guard

```
Algorithm 3: NaNGuardCallback.on_log
─────────────────────────────────────────────────────────────
State: bad_count = 0, max_bad = 3, check_every = 50

on_log(state, control, logs):
    if state.global_step % check_every ≠ 0:
        return control             // Bỏ qua steps không cần check

    loss ← logs.get("loss")
    if loss is None:
        return control             // Chưa có loss (warmup)

    if NOT isfinite(loss):         // NaN hoặc Inf
        bad_count += 1
        log.WARNING(step, loss, bad_count)
        if bad_count ≥ max_bad:
            log.ERROR("Stopping run due to repeated non-finite loss")
            control.should_training_stop ← True
    else:
        bad_count ← 0             // Reset khi loss hợp lệ trở lại

    return control

// Ghi chú: trainer kiểm tra control.should_training_stop sau mỗi step
// Khi True, trainer dừng vòng lặp chính ngay lập tức
```

---

### 17.4 Thuật Toán Continuous Learning Scheduler

```
Algorithm 4: ContinuousLearningScheduler._tick  (app/api/scheduling.py)
─────────────────────────────────────────────────────────────
State: last_run_iso = None  // Reset khi service restart

_tick() [called by APScheduler theo cron]:
    // Kiểm tra có đủ dữ liệu mới không
    try:
        count ← await fetcher.preference_count(since=last_run_iso)
                // TrainingDataFetcher port → AgentHttpClient (với tenacity retries)
    except Exception:
        log.WARNING(); return     // Bỏ qua tick này, thử lại lần sau

    if count < auto_train_min_new_pairs:
        log.INFO("not enough data"); return

    // Tránh chạy song song với job thủ công
    async with job_lock:          // asyncio.Semaphore(max_concurrent_jobs=1)
        job ← SubmitJobUseCase.execute(...)   // status="pending"

        last_run_iso ← utcnow()  // Cập nhật TRƯỚC khi train
                                  // (nếu train thất bại, lần sau vẫn chỉ
                                  //  lấy data từ thời điểm này trở đi)

        await RunDpoCycleUseCase.execute(
            job_id=job.id,
            since=last_run_iso,   // Chỉ data mới từ last_run trở đi
        )

// Ghi chú về thread safety:
// - _tick() là coroutine async → chạy trong asyncio event loop
// - train_dpo() / evaluate() là blocking → được đẩy sang ThreadPoolExecutor
// - SqliteConnection dùng threading.RLock → safe cho cả hai context
```

---

### 17.5 Thuật Toán Dataset Hashing (SHA-256 ổn định)

```
Algorithm 5: hash_jsonl
─────────────────────────────────────────────────────────────
Input:  path (JSONL file)
Output: hex_digest (64 ký tự SHA-256)

Mục tiêu: Cùng tập records, khác thứ tự dòng → cùng hash

1.  lines ← []
    for line in file:
        line = line.strip()
        if line: lines.append(line)

2.  lines.sort()                  // Sort để độc lập với thứ tự nhận từ API

3.  h ← hashlib.sha256()
    for line in lines:
        h.update(line.encode("utf-8"))

4.  return h.hexdigest()

Ví dụ:
    File A: {"x":1}\n{"x":2}\n  →  hash = "3a7f..."
    File B: {"x":2}\n{"x":1}\n  →  hash = "3a7f..."  (giống nhau!)
    File C: {"x":3}\n{"x":1}\n  →  hash = "9b2c..."  (khác)

Ứng dụng trong orchestrator:
    data_h = hash_jsonl(data_path)
    // Hiện tại hash được lưu vào jobs.data_hash
    // Dev có thể dùng nó để detect "same data, different hyperparams"
    // và quyết định có nên re-train không.
```

---

### 17.6 Thuật Toán ORPO Training Loop

```
Algorithm 6: run_training (mode = "orpo")
─────────────────────────────────────────────────────────────
Input:  cfg: TrainConfig (mode="orpo", train_file chứa {prompt, chosen, rejected})
Output: result: TrainResult

// ORPO = SFT loss + Odds-Ratio preference loss — trong 1 forward pass
// Không cần reference model riêng biệt (khác DPO)

1.  seed_everything(cfg.seed)

2.  model, tokenizer ← _load_model_and_tokenizer(cfg)
    // BitsAndBytesConfig NF4 giống SFT/DPO (nếu load_in_4bit=True)

3.  model ← _apply_lora(model, cfg, cfg.resume_from)
    // LoraConfig với use_rslora=True, use_dora=False (default)

4.  dataset_train ← load_dataset("json", data_files=cfg.train_file)
    dataset_val   ← load_dataset("json", data_files=cfg.val_file)
    // Mỗi record: {"prompt":..., "chosen":..., "rejected":...}
    // (cùng format với DPO)

5.  args ← ORPOConfig(
        learning_rate     = cfg.learning_rate,  // SFT-range (2e-4)
                                                 // vì ORPO bao gồm SFT loss
        beta              = 0.1,                // λ — trọng số odds-ratio term
        max_length        = cfg.max_seq_length,
        max_grad_norm     = cfg.max_grad_norm,  // 0.3
        gradient_checkpointing = True,
        bf16              = True,
        ...
    )
    // Không có dpo_learning_rate riêng — ORPO tự cân bằng hai loss

6.  trainer ← ORPOTrainer(
        model=model,
        args=args,
        train_dataset=dataset_train,
        eval_dataset=dataset_val,
        processing_class=tokenizer,
        callbacks=[NaNGuardCallback, TimeBudgetCallback, MetricSink]
    )

7.  result ← trainer.train()
    // Mỗi step tính:
    //   L_total = L_NLL(chosen) + λ × L_OR(chosen, rejected)
    //   Không cần forward pass qua ref model (tiết kiệm ~50% VRAM)

8.  trainer.save_model(cfg.output_dir)
    return TrainResult(...)

// Khi nào dùng ORPO thay DPO:
// - Khi VRAM giới hạn (ORPO tiết kiệm ~4 GB so với DPO + ref model)
// - Khi dataset SFT và preference được thu thập cùng lúc
// - Khi muốn 1 stage thay vì SFT → DPO 2 stage
```

---

## 18. Phân Tích Toán Học Chi Tiết

### 18.1 Hàm Mục Tiêu DPO (Direct Preference Optimization)

**Bài toán**: Học từ dữ liệu sở thích $(x, y_w, y_l)$ — với $x$ là prompt,
$y_w$ là phản hồi được ưa thích (chosen), $y_l$ là phản hồi bị từ chối (rejected).

**Hàm loss DPO** (Rafailov et al., 2023):

$$\mathcal{L}_{\text{DPO}}(\pi_\theta; \pi_{\text{ref}}) = -\mathbb{E}_{(x,y_w,y_l) \sim \mathcal{D}} \left[ \log \sigma \left( \beta \log \frac{\pi_\theta(y_w \mid x)}{\pi_{\text{ref}}(y_w \mid x)} - \beta \log \frac{\pi_\theta(y_l \mid x)}{\pi_{\text{ref}}(y_l \mid x)} \right) \right]$$

Trong đó:

- $\pi_\theta$ — model đang được train (LoRA adapter)
- $\pi_{\text{ref}}$ — reference model (base model đóng băng)
- $\beta$ — hệ số divergence (default `0.1` trong config)
- $\sigma$ — hàm sigmoid

**Ý nghĩa trực quan**: Loss giảm khi model tăng xác suất sinh ra $y_w$ nhiều hơn
$y_l$, _đồng thời_ không đi quá xa so với reference policy (điều chỉnh bởi $\beta$).

**Biến thể được tính toán trong TRL**:

$$r_\theta(x, y) = \beta \log \frac{\pi_\theta(y \mid x)}{\pi_{\text{ref}}(y \mid x)} = \beta \sum_{t=1}^{T} \log \frac{\pi_\theta(y_t \mid x, y_{<t})}{\pi_{\text{ref}}(y_t \mid x, y_{<t})}$$

Và loss trở thành:

$$\mathcal{L}_{\text{DPO}} = -\mathbb{E}\left[\log \sigma\left(r_\theta(x, y_w) - r_\theta(x, y_l)\right)\right]$$

**Ảnh hưởng của $\beta$**:

| $\beta$ | Ý nghĩa                        | Rủi ro                   |
| ------- | ------------------------------ | ------------------------ |
| 0.01    | Cho phép diverge rất xa từ ref | Catastrophic forgetting  |
| 0.1     | Cân bằng (default)             | Ổn định                  |
| 0.5     | Bám sát ref policy             | Học chậm, cần nhiều data |
| 1.0+    | Gần như không thay đổi         | Training vô hiệu         |

---

### 18.2 LoRA — Low-Rank Adaptation

**Vấn đề**: Fine-tune toàn bộ 8B parameters → 28+ GB VRAM, không khả thi.

**Giải pháp LoRA** (Hu et al., 2022): Thay vì cập nhật weight matrix $W \in \mathbb{R}^{d \times k}$
trực tiếp, ta học **phần cập nhật** $\Delta W$ với rank thấp:

$$\Delta W = BA$$

Trong đó $B \in \mathbb{R}^{d \times r}$, $A \in \mathbb{R}^{r \times k}$, $r \ll \min(d, k)$.

**Forward pass** với LoRA (standard):

$$h = W_0 x + \frac{\alpha}{r} \Delta W x = W_0 x + \frac{\alpha}{r} B A x$$

**Forward pass** với RSLoRA (`use_rslora=True`, default):

$$h = W_0 x + \frac{\alpha}{\sqrt{r}} B A x$$

Với config của service:

- $r = 64$ (`lora_rank`) — rank của ma trận thấp
- $\alpha = 128$ (`lora_alpha`) — hệ số scaling (= 2 × rank)
- Standard LoRA scaling factor: $\frac{\alpha}{r} = \frac{128}{64} = 2.0$
- RSLoRA scaling factor: $\frac{\alpha}{\sqrt{r}} = \frac{128}{\sqrt{64}} = \frac{128}{8} = 16.0$

**Số parameters trainable**:

Qwen3-8B có 28 transformer layers, mỗi layer có 7 projection matrices
(`q_proj, k_proj, v_proj, o_proj, gate_proj, up_proj, down_proj`).

Ước tính cho attention projections (dim=4096):
$$\text{params per matrix} = r \times (d_{in} + d_{out}) = 64 \times (4096 + 4096) = 524,288$$

$$\text{total LoRA params} \approx 28 \times 7 \times 524,288 \approx 103\text{M}$$

So với 8B base params → chỉ **~1.3% parameters** được train.

**Continual learning qua `resume_from`**:

Khi `resume_from` được đặt:
$$B_{\text{new}}, A_{\text{new}} \leftarrow B_{\text{champion}}, A_{\text{champion}}$$

Training tiếp tục từ $\Delta W_{\text{champion}}$, không reset về $B=0, A=\mathcal{N}(0,1)$.
Đây là cơ chế tích lũy kiến thức theo thời gian.

---

### 18.3 QLoRA — Quantized LoRA

**Chuỗi biến đổi** khi `load_in_4bit = True`:

```
Float32 weights (32 GB)
    ↓  double quantize
NF4 weights (4-bit Normal Float, ~4 GB)
    ↓  dequantize to bfloat16 khi compute
bfloat16 activations
    ↓  LoRA adapters (bfloat16, ~0.4 GB)
```

**NF4 quantization**: Chia giá trị weight vào 16 bins theo phân phối chuẩn tối ưu.
Mỗi weight dùng 4 bits thay vì 32 bits → tiết kiệm $8\times$ VRAM.

**Gradient flow qua QLoRA**:

$$\frac{\partial \mathcal{L}}{\partial A} = \frac{\partial \mathcal{L}}{\partial h} \cdot \frac{\partial h}{\partial (BAx)} \cdot \frac{\partial (BAx)}{\partial A}$$

Gradient chỉ cần truyền qua $B, A$ (bfloat16) — không qua $W_0$ (NF4, đóng băng).
Do đó memory footprint tỷ lệ với kích thước LoRA, không phải model đầy đủ.

---

### 18.4 Hệ Thống Elo — Toán Học Chi Tiết

**Xác suất thắng kỳ vọng** (expected score):

$$E_A = \frac{1}{1 + 10^{(R_B - R_A)/400}}$$

Với $R_A = R_B = 1000$ (khởi đầu):
$$E_A = \frac{1}{1 + 10^0} = \frac{1}{2} = 0.5$$

**Cập nhật sau mỗi so sánh** (K=32):

$$R'_A = R_A + K(S_A - E_A)$$
$$R'_B = R_B + K(S_B - E_B)$$

Trong đó $S_A + S_B = 1$ (zero-sum), dẫn đến:
$$R'_A + R'_B = R_A + R_B \quad \text{(tổng rating bảo toàn)}$$

**Tính toán `elo_delta`** trong evaluate.py:

```python
cand_elo = champ_elo = 1000.0

for each comparison:
    S = 1.0 if candidate_won else (0.5 if tie else 0.0)
    cand_elo, champ_elo = elo_update(cand_elo, champ_elo, S)

elo_delta = cand_elo - 1000.0
```

**Ví dụ tính toán** (100 comparisons: 62 wins, 8 ties, 30 losses):

Sau 62 wins thuần (K=32, E≈0.5):
$$\Delta R_{win} = 32 \times (1 - 0.5) = +16 \text{ per win}$$

Sau 8 ties:
$$\Delta R_{tie} = 32 \times (0.5 - 0.5) = 0$$

Sau 30 losses:
$$\Delta R_{loss} = 32 \times (0 - 0.5) = -16 \text{ per loss}$$

$$\text{elo\_delta} \approx 62 \times 16 + 8 \times 0 + 30 \times (-16) = 992 - 480 = +512$$

_Lưu ý_: Thực tế thấp hơn vì $E_A$ thay đổi khi candidate dẫn trước
(expected score tăng khi đang thắng → mỗi win được ít điểm hơn).

**Diễn giải `elo_delta` thực tế**:

| elo_delta | Ý nghĩa                          |
| --------- | -------------------------------- |
| < 0       | Candidate thua champion          |
| 0–15      | Tương đương, không đáng kể       |
| 15–40     | Thắng rõ ràng (win_rate ~55–65%) |
| 40–80     | Thắng áp đảo (win_rate ~65–75%)  |
| > 80      | Thắng vượt trội (win_rate > 75%) |

---

### 18.5 Khoảng Tin Cậy Win-Rate (Wilson Score)

Tại sao threshold 55% và min_samples 50 là hợp lý — và tại sao production nên bật `eval_wilson_min`?

**Phân phối nhị thức**: Mỗi so sánh là Bernoulli($p$) với $p$ là xác suất
candidate thắng (tính ties là 0.5).

**Wilson score lower bound** (95% confidence) — công thức trong `app/domain.py`:

$$\hat{p}_{low} = \frac{\hat{p} + \dfrac{z^2}{2n} - z\sqrt{\dfrac{\hat{p}(1-\hat{p})}{n} + \dfrac{z^2}{4n^2}}}{1 + \dfrac{z^2}{n}}$$

Với $n = 50$, $\hat{p} = 0.55$, $z = 1.96$:

$$\hat{p}_{low} \approx 0.41$$

Giải thích: Với 50 samples và win_rate = 55%, lower bound chỉ là 41% — không đủ
bằng chứng thống kê. Đây là lý do `eval_wilson_min=0.50` giúp chặn các promotion
"may mắn" trên sample nhỏ.

| min_samples | win_rate = 55% | wilson_lower | Promote nếu wilson_min=0.50? |
| ----------- | -------------- | ------------ | ---------------------------- |
| 50          | 0.55           | 0.41         | ❌ Không                     |
| 100         | 0.55           | 0.45         | ❌ Không                     |
| 200         | 0.55           | 0.48         | ❌ Không                     |
| 300         | 0.55           | 0.49         | ❌ Không                     |
| 500         | 0.55           | 0.51         | ✅ Có                        |
| 100         | 0.63           | 0.53         | ✅ Có                        |

**Cài đặt `eval_wilson_min`**:

```bash
# Môi trường dev (tắt gate Wilson — dễ promote để test):
HOPE_FT_EVAL_WILSON_MIN=0.0   # default

# Production y tế (yêu cầu bằng chứng thống kê mạnh):
HOPE_FT_EVAL_WILSON_MIN=0.50
HOPE_FT_EVAL_MIN_SAMPLES=100
HOPE_FT_EVAL_WIN_RATE_PROMOTE=0.58
```

**Kiểm tra logic trong `test_clean_arch.py`**:

```python
# test_promotion_policy_wilson_ci_too_wide
# 10/13 wins (15 samples) → win_rate=0.667, wlb=0.38 < wilson_min=0.50 → NO promote
result = decide_promotion(eval, threshold=0.55, min_samples=10, wilson_min=0.50)
assert result.promote is False

# test_wilson_lower_bound_math
# Verifies formula numerically against known values
assert abs(wilson_lower_bound(55, 100) - 0.455) < 0.01
```

---

### 18.6 Gradient Accumulation — Effective Batch Size

Khi `batch_size=1` và `grad_accum=16`:

**Effective batch size**:
$$B_{\text{eff}} = B_{\text{per\_device}} \times \text{grad\_accum} \times N_{\text{GPU}} = 1 \times 16 \times 1 = 16$$

**Tại sao cần grad_accum**:

- DPO cần tính log-likelihood của cả chosen và rejected trong 1 batch
- Với seq_length=2048, mỗi sample ngốn ~4GB activation memory
- batch_size=1 để vừa VRAM, nhưng gradient noisy
- Tích lũy 16 steps để có gradient ổn định hơn trước khi update

**Mối quan hệ với learning rate**:
Khi tăng $B_{\text{eff}}$, theo Linear Scaling Rule ta nên tăng lr tương ứng:
$$\text{lr}_{\text{eff}} = \text{lr}_{\text{base}} \times \sqrt{B_{\text{eff}}}$$

Với $B_{\text{eff}} = 16$, $\text{lr}_{\text{base}} = 5\times10^{-6}$:
$$\text{lr}_{\text{eff}} = 5\times10^{-6} \times \sqrt{16} = 2\times10^{-5}$$

TRL/HuggingFace Trainer đã tự xử lý điều này nội bộ khi set gradient_accumulation_steps.

---

### 18.7 Cosine Learning Rate Schedule

Trainer dùng `lr_scheduler_type = "cosine"` với `warmup_ratio = 0.1`.

**Giai đoạn warmup** (10% đầu của steps):
$$\text{lr}(t) = \text{lr}_{\max} \times \frac{t}{T_{\text{warm}}}$$

**Giai đoạn cosine decay** (90% còn lại):
$$\text{lr}(t) = \text{lr}_{\min} + \frac{1}{2}(\text{lr}_{\max} - \text{lr}_{\min})\left(1 + \cos\left(\pi \frac{t - T_{\text{warm}}}{T_{\text{total}} - T_{\text{warm}}}\right)\right)$$

Với $\text{lr}_{\min} = 0$, $\text{lr}_{\max} = 5 \times 10^{-6}$, $T_{\text{total}} = 480$ steps:

| Step       | lr                   |
| ---------- | -------------------- |
| 0          | 0                    |
| 48 (10%)   | $5 \times 10^{-6}$   |
| 264 (55%)  | $2.5 \times 10^{-6}$ |
| 480 (100%) | ~0                   |

**Tại sao cosine quan trọng với DPO**:

- Warmup tránh gradient shock ở bước đầu (model chưa thích nghi với data)
- Cosine decay giảm lr mượt mà → converge ổn định, ít overfitting
- So với constant lr: cosine thường giảm validation loss thêm 5–10%

---

### 18.8 Phân Tích Holdout Split

Hàm `_holdout_split(src, dst, fraction=0.1)` trong `app/use_cases.py`:

```
Input:  N dòng JSONL (N ≥ 20)
Output: train = dòng [n_val, N), val = dòng [0, n_val)

n_val = max(1, int(N × 0.1))
```

**Tại sao deterministic (không shuffle)**:

Preference pairs được tạo theo thứ tự thời gian. Các pairs gần đây
(cuối file) phản ánh hành vi bác sĩ mới nhất — nếu shuffle, chúng
có thể vào val set và training set sẽ không thấy pattern mới nhất.

Thay vào đó:

- Val set = 10% **đầu tiên** = data cũ hơn = proxy cho "kiến thức nền"
- Train set = 90% còn lại = bao gồm cả data mới nhất

Điều này tạo ra một **temporal holdout**: val set kiểm tra xem model
có giữ được kiến thức cũ không, trong khi train set dạy nó kiến thức mới.

**Trade-off của fraction**:

| fraction | val_size (N=500) | Ảnh hưởng                                     |
| -------- | ---------------- | --------------------------------------------- |
| 0.05     | 25               | eval noisy, nhưng nhiều data hơn cho training |
| 0.10     | 50               | cân bằng (default)                            |
| 0.20     | 100              | eval ổn định, nhưng mất 20% training data     |

Với N < 20, hàm bỏ qua split (val = rỗng) vì không đủ data cho cả hai mục đích.

---

### 18.9 RSLoRA — Rank-Stabilized LoRA

**Vấn đề với LoRA chuẩn** (Kalajdzic et al., 2023):

Khi tăng rank $r$, LoRA chuẩn dùng scaling $\frac{\alpha}{r}$. Điều này có nghĩa
là **gradient magnitude giảm tỷ lệ với** $\frac{1}{r}$. Khi $r=64$ và $\alpha=128$:

$$\text{LoRA scale} = \frac{128}{64} = 2.0$$

Nhưng khi tăng rank lên $r=128$ mà không đổi alpha, scale giảm còn $1.0$.
Gradient flow yếu hơn → học chậm hơn ở rank cao.

**Giải pháp RSLoRA**: Thay $\frac{\alpha}{r}$ bằng $\frac{\alpha}{\sqrt{r}}$:

$$h = W_0 x + \frac{\alpha}{\sqrt{r}} B A x$$

Với $r=64$, $\alpha=128$:

$$\text{RSLoRA scale} = \frac{128}{\sqrt{64}} = \frac{128}{8} = 16.0$$

**Tại sao $\frac{1}{\sqrt{r}}$ là tối ưu**?

Từ lý thuyết khởi tạo ngẫu nhiên: Ma trận $A \in \mathbb{R}^{r \times k}$ được
khởi tạo theo $\mathcal{N}(0, \sigma^2)$. Frobenius norm của $A$ tỉ lệ với $\sqrt{r}$:

$$\|A\|_F \approx \sigma \sqrt{r \cdot k}$$

Để giữ $\|BAx\|$ ổn định khi $r$ thay đổi, scaling cần bù lại $\sqrt{r}$:

$$\frac{\alpha}{\sqrt{r}} \cdot \|B\| \cdot \|A\| \approx \frac{\alpha}{\sqrt{r}} \cdot C\sqrt{r} = C\alpha \quad \text{(độc lập với } r\text{)}$$

**So sánh thực nghiệm** (trích từ paper gốc):

| Phương pháp       | rank=16  | rank=64    | rank=128   |
| ----------------- | -------- | ---------- | ---------- |
| LoRA (alpha/r)    | baseline | -3.2% perf | -7.1% perf |
| RSLoRA (alpha/√r) | baseline | +0.8% perf | +1.9% perf |

RSLoRA đặc biệt hiệu quả với rank cao (≥ 32), chính xác là trường hợp của
service (`lora_rank=64`). Đây là lý do `use_rslora=True` là default.

**Trong code** (`trainer.py`):

```python
lora = LoraConfig(
    r=64, lora_alpha=128, use_rslora=True,   # alpha/sqrt(r) = 16.0
    ...
)
```

---

### 18.10 ORPO — Odds-Ratio Preference Optimization

**Vấn đề với pipeline SFT → DPO** (Hong et al., 2024):

1. Hai giai đoạn training riêng biệt → tốn thời gian và VRAM
2. DPO cần reference model (frozen copy của model) → +4–8 GB VRAM
3. Alignment signal và SFT signal không được tối ưu cùng lúc

**Hàm loss ORPO** — kết hợp SFT và preference trong 1 forward pass:

$$\mathcal{L}_{\text{ORPO}} = \mathcal{L}_{\text{SFT}} + \lambda \cdot \mathcal{L}_{\text{OR}}$$

**Thành phần SFT** — negative log-likelihood trên response được chọn:

$$\mathcal{L}_{\text{SFT}} = -\mathbb{E}_{(x, y_w)} \left[ \log \pi_\theta(y_w \mid x) \right]$$

**Thành phần Odds-Ratio** — phân biệt chosen vs rejected:

$$\mathcal{L}_{\text{OR}} = -\mathbb{E}_{(x, y_w, y_l)} \left[ \log \sigma \left( \log \frac{\text{odds}_\theta(y_w \mid x)}{\text{odds}_\theta(y_l \mid x)} \right) \right]$$

Trong đó **odds** được định nghĩa:

$$\text{odds}_\theta(y \mid x) = \frac{\pi_\theta(y \mid x)}{1 - \pi_\theta(y \mid x)}$$

**Biến đổi ra dạng tính được**:

Thay $p_w = \pi_\theta(y_w \mid x)$ và $p_l = \pi_\theta(y_l \mid x)$:

$$\mathcal{L}_{\text{OR}} = -\log \sigma \left( \log \frac{p_w (1-p_l)}{p_l (1-p_w)} \right)$$

$$= -\log \sigma \left( \log p_w - \log (1 - p_w) - \log p_l + \log (1 - p_l) \right)$$

**So sánh với DPO**:

| Tiêu chí            | DPO                        | ORPO                        |
| ------------------- | -------------------------- | --------------------------- |
| Reference model     | Cần (frozen copy)          | Không cần                   |
| VRAM overhead       | +4–8 GB                    | 0 GB                        |
| Số giai đoạn        | 2 (SFT → DPO)              | 1                           |
| LR range            | $10^{-7}$ – $10^{-5}$      | $10^{-4}$ – $10^{-3}$       |
| Data format         | {prompt, chosen, rejected} | {prompt, chosen, rejected}  |
| Độ ổn định training | Có thể collapse nếu LR sai | Ổn định hơn (có SFT anchor) |

**Khi nào dùng ORPO**:

- VRAM hạn chế (không đủ cho ref model)
- Dataset ít (< 500 pairs) — SFT anchor giúp tránh overfitting
- Muốn training 1 lần thay vì 2 giai đoạn

---

### 18.11 IPO — Identity Preference Optimization

**Vấn đề với DPO chuẩn** (Azar et al., 2024):

DPO chuẩn dùng sigmoid loss:

$$\mathcal{L}_{\text{DPO}} = -\log \sigma(r_w - r_l)$$

Khi $r_w - r_l \to +\infty$, gradient $\to 0$ — model **bỏ qua** preference signal
sau khi đã phân biệt tốt. Nhưng với dữ liệu y tế **có nhiễu** (bác sĩ đôi khi
gán nhãn không nhất quán), model có thể overfit sớm vào noise.

**Hàm loss IPO** — identity function thay vì sigmoid:

$$\mathcal{L}_{\text{IPO}} = \left( r_w - r_l - \frac{1}{2\beta} \right)^2$$

Trong đó $r_w - r_l$ là log-ratio giống DPO:

$$r_\theta(x,y) = \beta \log \frac{\pi_\theta(y \mid x)}{\pi_{\text{ref}}(y \mid x)}$$

**Phân tích gradient**:

$$\frac{\partial \mathcal{L}_{\text{IPO}}}{\partial (r_w - r_l)} = 2 \left( r_w - r_l - \frac{1}{2\beta} \right)$$

Gradient không bao giờ về 0 (trừ khi $r_w - r_l = \frac{1}{2\beta}$ chính xác).
Model tiếp tục nhận signal ngay cả sau khi phân biệt tốt — điều này quan trọng
khi nhãn có nhiễu và model cần "chống lại" pressure từ noisy samples.

**Nghiệm tối ưu của IPO**:

IPO converge về:
$$r_w - r_l = \frac{1}{2\beta}$$

Nghĩa là model học để duy trì margin ổn định $\frac{1}{2\beta}$ giữa chosen và
rejected — không cố gắng maximize vô hạn. Đây là behavior mong muốn với
dữ liệu medical preference (bác sĩ không hoàn toàn đồng ý 100% với nhau).

**Cấu hình trong service**:

```python
dpo_loss_type: str = "ipo"   # thay vì "sigmoid" (DPO chuẩn)
dpo_beta: float = 0.1        # margin = 1/(2×0.1) = 5.0
```

**So sánh loss functions** được TRL hỗ trợ:

| `loss_type`  | Formula                            | Tốt cho                    |
| ------------ | ---------------------------------- | -------------------------- |
| `"sigmoid"`  | $-\log\sigma(r_w - r_l)$           | Clean labels, nhiều data   |
| `"ipo"`      | $(r_w - r_l - \frac{1}{2\beta})^2$ | Noisy labels, medical data |
| `"hinge"`    | $\max(0, 1 - (r_w - r_l))$         | Hard margin, ít data       |
| `"kto_pair"` | Kahneman-Tversky                   | Asymmetric win/loss        |

---

### 18.12 NEFTune — Noisy Embedding Fine-Tuning

**Nguồn gốc** (Jain et al., 2023): Thêm nhiễu uniform vào embedding vectors
trong quá trình SFT forward pass.

**Công thức**:

Với embedding vector $e \in \mathbb{R}^d$ của sequence độ dài $L$:

$$\tilde{e} = e + \frac{\alpha}{\sqrt{Ld}} \cdot \boldsymbol{\epsilon}, \quad \boldsymbol{\epsilon} \sim \mathcal{U}(-1, 1)^d$$

Trong đó:

- $\alpha = 5.0$ (`neftune_noise_alpha` — default trong service)
- $L$ — sequence length
- $d$ — embedding dimension
- Chuẩn hóa bởi $\sqrt{Ld}$ để biên độ nhiễu độc lập với sequence length

**Tại sao nhiễu giúp generalization**?

NEFTune hoạt động như **data augmentation ở embedding space**:

- Mỗi forward pass thấy embedding hơi khác nhau
- Model học representation **robust** hơn (không bị overfit một vùng embedding hẹp)
- Hiệu quả tương đương dropout nhưng áp dụng ở level thấp hơn

**Kết quả thực nghiệm** (trích paper gốc trên Alpaca-52K):

| Model       | Without NEFTune | With NEFTune (α=5) | Δ    |
| ----------- | --------------- | ------------------ | ---- |
| LLaMA-2-7B  | 29.8 MT-Bench   | 32.3 MT-Bench      | +2.5 |
| LLaMA-2-13B | 33.4 MT-Bench   | 35.2 MT-Bench      | +1.8 |

**Lưu ý triển khai**: NEFTune chỉ được áp dụng trong **training**, không trong
inference. TRL tự handle điều này khi `neftune_noise_alpha` được set trong
`SFTConfig`.

**Phạm vi alpha**:

| alpha | Ý nghĩa                                    |
| ----- | ------------------------------------------ |
| 0     | Tắt (không dùng NEFTune)                   |
| 5     | Khuyến nghị cho hầu hết model (default)    |
| 10–15 | Nhiễu mạnh hơn — dùng với dataset nhỏ      |
| > 15  | Không ổn định, có thể làm chậm convergence |

---

### 18.13 Sequence Packing

**Vấn đề với padding chuẩn**:

Khi `packing=False` (trước đây), mỗi batch được padded đến độ dài sequence
dài nhất trong batch. Với medical data (length phân phối rộng):

```
Batch step i:
  Seq 1: [tok tok tok tok tok tok tok tok PAD PAD PAD PAD]  → 12 tokens
  Seq 2: [tok tok tok                     PAD PAD PAD PAD]  → 12 tokens
  Seq 3: [tok tok tok tok tok             PAD PAD PAD PAD]  → 12 tokens
  Utilization: 16/36 = 44%  (56% padding waste!)
```

**Giải pháp sequence packing** (`packing=True`):

Nhiều sequences ngắn được ghép vào 1 context window:

```
Packed step i (max_length=2048):
  [seq_A ... EOS | seq_B ... EOS | seq_C ... EOS | seq_D ... EOS]
  Utilization: ~95%+  (gần như không có padding)
```

TRL dùng `ConstantLengthDataset` để pack sequences với attention mask
đảm bảo mỗi token chỉ attend đến tokens trong cùng sequence của nó
(không cross-contaminate giữa các sequences trong 1 pack).

**Lợi ích về throughput**:

$$\text{Speedup} \approx \frac{1}{\text{Avg. utilization without packing}}$$

Với medical QA data (average length ~300 tokens, max_length=2048):
$$\text{Avg. utilization} \approx \frac{300}{2048} \approx 15\%$$
$$\text{Speedup} \approx 6\times$$

Thực tế thường đạt **2–4×** sau overhead packing.

**Khi nào TẮT packing** (`pack_sequences=False`):

- Dataset đã đồng đều về length (std deviation < 20% of mean)
- Debug NaN/loss issues (packing che giấu sequence boundaries)
- DPO và ORPO mode — packing chỉ có ý nghĩa với SFT (service tự set khi mode≠sft)

**Gradient checkpointing + packing**:

Packing làm activation memory tăng vì sequences dài hơn. Kết hợp với
`gradient_checkpointing=True` (mặc định) — checkpoint activations mỗi layer
thay vì giữ tất cả — giúp VRAM không tăng đáng kể dù sequence dài hơn.
