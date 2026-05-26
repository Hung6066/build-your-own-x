"""FastAPI application factory.

Lifespan responsibilities:
    1. Configure logging.
    2. Build the composition root: settings → SQLite → repos → infra adapters → use cases.
    3. Startup recovery: mark any active jobs from a previous run as `failed`.
    4. Start the continuous-learning scheduler.
    5. On shutdown: stop scheduler, wait `shutdown_grace_seconds` for the
       in-flight job (if any) to finish, then close.
"""

from __future__ import annotations

import asyncio
from contextlib import asynccontextmanager

from fastapi import FastAPI

from .. import __version__
from ..domain import JobStatus
from ..infra.agent_http import AgentHttpClient
from ..infra.config import Settings, get_settings
from ..infra.evaluator_llm import LlmJudgeEvaluator
from ..infra.logging import configure_logging, get_logger
from ..infra.ollama_http import AgentHttpNotifier, OllamaInferenceRegistry
from ..infra.persistence import (SqliteAdapterRepository, SqliteConnection,
                                 SqliteEvaluationRepository,
                                 SqliteJobRepository,
                                 SqliteTrainingRunRepository)
from ..infra.trainer_hf import HuggingFaceTrainer
from ..infra.utils import AsyncioExecutor, SystemClock
from ..use_cases import (CancelJobUseCase, EvaluationPolicy, GetChampionUseCase,
                         GetJobUseCase, ListAdaptersUseCase, ListJobsUseCase,
                         PromoteChampionUseCase, RunDpoCycleUseCase,
                         StoragePolicy, SubmitJobUseCase, TrainingPolicy)
from .errors import install_exception_handlers
from .middleware import CorrelationIdMiddleware, MetricsMiddleware
from .routers import adapters, evaluations, health, jobs
from .scheduling import ContinuousLearningScheduler

log = get_logger("api.app")


def _build_policies(settings: Settings):
    tp = TrainingPolicy(
        base_model=settings.base_model,
        load_in_4bit=settings.load_in_4bit,
        max_seq_length=settings.max_seq_length,
        lora_rank=settings.lora_rank,
        lora_alpha=settings.lora_alpha,
        lora_dropout=settings.lora_dropout,
        dpo_epochs=settings.dpo_epochs,
        dpo_lr=settings.dpo_lr,
        dpo_batch=settings.dpo_batch,
        dpo_grad_accum=settings.dpo_grad_accum,
        dpo_beta=settings.dpo_beta,
        max_runtime_seconds=settings.max_runtime_hours * 3600,
        nan_check_every=settings.nan_check_every_n_steps,
    )
    ep = EvaluationPolicy(
        judge_url=settings.eval_judge_url,
        judge_model=settings.eval_judge_model,
        promote_win_rate=settings.eval_win_rate_promote,
        min_samples=settings.eval_min_samples,
        wilson_min=settings.eval_wilson_min,
    )
    sp = StoragePolicy(
        data_dir=settings.data_dir,
        adapters_dir=settings.adapters_dir,
    )
    return tp, ep, sp


@asynccontextmanager
async def lifespan(app: FastAPI):
    settings = get_settings()
    configure_logging(settings.log_level, settings.log_json, settings.logs_dir)
    log.info("Service starting", extra={"version": __version__})

    # ── Composition root ─────────────────────────────────────────────────
    db = SqliteConnection(settings.db_path)
    job_repo = SqliteJobRepository(db)
    adapter_repo = SqliteAdapterRepository(db)
    eval_repo = SqliteEvaluationRepository(db)
    run_repo = SqliteTrainingRunRepository(db)

    fetcher = AgentHttpClient(
        settings.agent_api_url, settings.agent_api_token,
        retry_attempts=settings.http_retry_attempts,
        retry_backoff=settings.http_retry_backoff_seconds,
    )
    trainer = HuggingFaceTrainer()
    evaluator = LlmJudgeEvaluator()
    registry = OllamaInferenceRegistry(
        settings.ollama_url, attempts=settings.http_retry_attempts,
        backoff=settings.http_retry_backoff_seconds,
    )
    notifier = AgentHttpNotifier(
        settings.agent_api_url, settings.agent_api_token,
        attempts=settings.http_retry_attempts,
        backoff=settings.http_retry_backoff_seconds,
    )
    clock = SystemClock()
    executor = AsyncioExecutor()
    tp, ep, sp = _build_policies(settings)

    # ── Use cases ────────────────────────────────────────────────────────
    uc_submit = SubmitJobUseCase(job_repo, settings.base_model, clock)
    uc_get_job = GetJobUseCase(job_repo)
    uc_list_jobs = ListJobsUseCase(job_repo)
    uc_cancel = CancelJobUseCase(job_repo, clock)
    uc_list_adapters = ListAdaptersUseCase(adapter_repo)
    uc_get_champ = GetChampionUseCase(adapter_repo)
    uc_promote = PromoteChampionUseCase(
        adapter_repo, registry, notifier, settings.base_model,
        auto_register=settings.ollama_auto_register,
    )
    uc_cycle = RunDpoCycleUseCase(
        jobs=job_repo, adapters=adapter_repo, evaluations=eval_repo,
        runs=run_repo, fetcher=fetcher, trainer=trainer,
        evaluator=evaluator, registry=registry, notifier=notifier,
        executor=executor, clock=clock,
        training_policy=tp, eval_policy=ep, storage=sp,
        ollama_auto_register=settings.ollama_auto_register,
        logger=get_logger("cycle"),
    )

    job_lock = asyncio.Semaphore(settings.max_concurrent_jobs)

    # ── Startup recovery (Scenario S7) ───────────────────────────────────
    stale = job_repo.active()
    for j in stale:
        log.warning("Recovering stale job from previous run",
                    extra={"job_id": j.id, "prior_status": j.status.value})
        job_repo.update(
            j.id, status=JobStatus.FAILED.value,
            error_detail="service_restart",
            finished_at=clock.utcnow_iso(),
        )

    # ── Scheduler ────────────────────────────────────────────────────────
    scheduler = ContinuousLearningScheduler(
        settings=settings, fetcher=fetcher,
        submit_uc=uc_submit, cycle_uc=uc_cycle, job_lock=job_lock,
    )
    scheduler.start()

    # ── Stash on app.state ───────────────────────────────────────────────
    s = app.state
    s.settings = settings
    s.db = db
    s.job_repo = job_repo
    s.adapter_repo = adapter_repo
    s.eval_repo = eval_repo
    s.run_repo = run_repo
    s.job_lock = job_lock
    s.scheduler = scheduler
    s.uc_submit_job = uc_submit
    s.uc_get_job = uc_get_job
    s.uc_list_jobs = uc_list_jobs
    s.uc_cancel_job = uc_cancel
    s.uc_list_adapters = uc_list_adapters
    s.uc_get_champion = uc_get_champ
    s.uc_promote = uc_promote
    s.uc_run_cycle = uc_cycle

    try:
        yield
    finally:
        log.info("Service shutting down")
        scheduler.stop()
        # Graceful: wait up to N seconds for the lock to drain.
        grace = settings.shutdown_grace_seconds
        try:
            await asyncio.wait_for(_drain_lock(job_lock,
                                               settings.max_concurrent_jobs),
                                   timeout=grace)
            log.info("Graceful shutdown complete")
        except asyncio.TimeoutError:
            log.warning("Shutdown timed out — in-flight job will be killed",
                        extra={"grace_seconds": grace})


async def _drain_lock(lock: asyncio.Semaphore, capacity: int) -> None:
    """Wait until all permits are free by acquiring + releasing them all."""
    permits = []
    for _ in range(capacity):
        await lock.acquire()
        permits.append(True)
    for _ in permits:
        lock.release()


def create_app() -> FastAPI:
    app = FastAPI(
        title="Hope.Agent Training Service",
        version=__version__,
        lifespan=lifespan,
    )

    # Order: correlation first (so all later logs have it), then metrics.
    app.add_middleware(MetricsMiddleware)
    app.add_middleware(CorrelationIdMiddleware)

    install_exception_handlers(app)

    app.include_router(health)
    app.include_router(jobs)
    app.include_router(adapters)
    app.include_router(evaluations)

    return app
