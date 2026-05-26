"""Application layer: use cases.

Each use case is a thin orchestration object that receives ports via the
constructor, applies domain rules, and returns a result. No I/O leaks
directly into a use case — everything goes through a port.
"""

from __future__ import annotations

import time
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from .domain import (Adapter, ChampionNotFound, EvaluationResult, Job,
                     JobNotFound, JobStatus, JobType, TrainingResult,
                     adapter_from_row, decide_promotion, utc_now_iso)
from .ports import (AdapterRepository, AgentNotifier, ChampionEvaluator,
                    Clock, EvaluationRepository, InferenceRegistry,
                    JobExecutor, JobRepository, ModelTrainer,
                    TrainingDataFetcher, TrainingRunRepository)


# ── DTOs ─────────────────────────────────────────────────────────────────────

@dataclass(slots=True)
class SubmitJobInput:
    job_type: JobType
    specialty: str | None
    since: str | None
    max_records: int


@dataclass(slots=True)
class TrainingPolicy:
    """All hyperparameters / safety knobs the orchestrator needs."""
    base_model: str
    load_in_4bit: bool
    max_seq_length: int
    lora_rank: int
    lora_alpha: int
    lora_dropout: float
    dpo_epochs: int
    dpo_lr: float
    dpo_batch: int
    dpo_grad_accum: int
    dpo_beta: float
    max_runtime_seconds: float
    nan_check_every: int


@dataclass(slots=True)
class EvaluationPolicy:
    judge_url: str
    judge_model: str
    promote_win_rate: float
    min_samples: int
    wilson_min: float = 0.0   # 0 = disabled; 0.50 = require stat significance


@dataclass(slots=True)
class StoragePolicy:
    data_dir: Path
    adapters_dir: Path

    def adapter_dir(self, tag: str) -> Path:
        return self.adapters_dir / tag

    def dataset_path(self, job_id: str) -> Path:
        return self.data_dir / f"dpo_{job_id}.jsonl"

    def golden_suite_path(self, specialty: str | None) -> Path:
        return self.data_dir / "suites" / f"golden_{specialty or 'general'}.jsonl"


# ── Helpers ──────────────────────────────────────────────────────────────────

def new_job_id() -> str:
    return f"job_{int(time.time())}_{uuid.uuid4().hex[:8]}"


def new_eval_id() -> str:
    return f"eval_{int(time.time())}_{uuid.uuid4().hex[:8]}"


def _holdout_split(src: Path, dst: Path, fraction: float = 0.1) -> None:
    if not src.exists():
        return
    with open(src, "r", encoding="utf-8") as f:
        lines = [ln for ln in f if ln.strip()]
    if len(lines) < 20:
        dst.write_text("", encoding="utf-8")
        return
    n_val = max(1, int(len(lines) * fraction))
    val, train = lines[:n_val], lines[n_val:]
    src.write_text("".join(train), encoding="utf-8")
    dst.write_text("".join(val), encoding="utf-8")


# ── Use cases ────────────────────────────────────────────────────────────────

class SubmitJobUseCase:
    def __init__(self, jobs: JobRepository, base_model: str, clock: Clock):
        self._jobs = jobs
        self._base_model = base_model
        self._clock = clock

    def execute(self, cmd: SubmitJobInput) -> Job:
        job = Job(
            id=new_job_id(),
            job_type=cmd.job_type,
            base_model=self._base_model,
            specialty=cmd.specialty,
            status=JobStatus.PENDING,
            created_at=self._clock.utcnow_iso(),
        )
        self._jobs.create(job)
        return job


class GetJobUseCase:
    def __init__(self, jobs: JobRepository):
        self._jobs = jobs

    def execute(self, job_id: str) -> Job:
        job = self._jobs.get(job_id)
        if job is None:
            raise JobNotFound(f"job '{job_id}' not found")
        return job


class ListJobsUseCase:
    def __init__(self, jobs: JobRepository):
        self._jobs = jobs

    def execute(self, take: int = 50) -> list[Job]:
        return self._jobs.list(take)


class CancelJobUseCase:
    def __init__(self, jobs: JobRepository, clock: Clock):
        self._jobs = jobs
        self._clock = clock

    def execute(self, job_id: str) -> Job:
        job = self._jobs.get(job_id)
        if job is None:
            raise JobNotFound(f"job '{job_id}' not found")
        if job.status.is_terminal:
            return job  # idempotent
        self._jobs.update(job_id, status=JobStatus.CANCELLED.value,
                          finished_at=self._clock.utcnow_iso())
        return self._jobs.get(job_id)  # type: ignore[return-value]


class ListAdaptersUseCase:
    def __init__(self, adapters: AdapterRepository):
        self._adapters = adapters

    def execute(self, specialty: str | None = None, take: int = 20) -> list[Adapter]:
        return self._adapters.list(specialty, take)


class GetChampionUseCase:
    def __init__(self, adapters: AdapterRepository):
        self._adapters = adapters

    def execute(self, specialty: str | None, job_type: JobType) -> Adapter:
        champ = self._adapters.get_champion(specialty, job_type)
        if champ is None:
            raise ChampionNotFound(
                f"no champion for specialty={specialty}, job_type={job_type.value}"
            )
        return champ


class PromoteChampionUseCase:
    """Manual override — typically used to roll back to a previous adapter."""

    def __init__(self, adapters: AdapterRepository,
                 registry: InferenceRegistry,
                 notifier: AgentNotifier,
                 base_model: str,
                 auto_register: bool = True):
        self._adapters = adapters
        self._registry = registry
        self._notifier = notifier
        self._base_model = base_model
        self._auto_register = auto_register

    def execute(self, tag: str) -> Adapter:
        # elo_delta = 0 — manual promotion doesn't change rating
        self._adapters.promote(tag, 0.0)
        adapter = self._adapters.get(tag)
        if adapter is None:
            raise ChampionNotFound(
                f"adapter '{tag}' not found after promotion")

        # Best-effort side-effects
        if self._auto_register:
            try:
                self._registry.register(tag=tag, base_model_tag=self._base_model,
                                        adapter_path=adapter.path)
            except Exception:  # noqa: BLE001
                pass
        try:
            self._notifier.announce_champion(tag=tag, specialty=adapter.specialty,
                                             elo=adapter.elo_rating)
        except Exception:  # noqa: BLE001
            pass
        return adapter


# ── The closed-loop training cycle ───────────────────────────────────────────

class RunDpoCycleUseCase:
    """Orchestrates data → train → evaluate → promote.

    All side-effects go through ports. The use case itself contains only
    domain logic + flow control.
    """

    def __init__(self, *,
                 jobs: JobRepository,
                 adapters: AdapterRepository,
                 evaluations: EvaluationRepository,
                 runs: TrainingRunRepository,
                 fetcher: TrainingDataFetcher,
                 trainer: ModelTrainer,
                 evaluator: ChampionEvaluator,
                 registry: InferenceRegistry,
                 notifier: AgentNotifier,
                 executor: JobExecutor,
                 clock: Clock,
                 training_policy: TrainingPolicy,
                 eval_policy: EvaluationPolicy,
                 storage: StoragePolicy,
                 ollama_auto_register: bool = True,
                 logger: Any = None):
        self._jobs = jobs
        self._adapters = adapters
        self._evals = evaluations
        self._runs = runs
        self._fetcher = fetcher
        self._trainer = trainer
        self._evaluator = evaluator
        self._registry = registry
        self._notifier = notifier
        self._executor = executor
        self._clock = clock
        self._tp = training_policy
        self._ep = eval_policy
        self._sp = storage
        self._ollama_auto = ollama_auto_register
        self._log = logger

    def _info(self, msg: str, **extra: Any) -> None:
        if self._log:
            self._log.info(msg, extra=extra or None)

    def _warn(self, msg: str, **extra: Any) -> None:
        if self._log:
            self._log.warning(msg, extra=extra or None)

    def _exc(self, msg: str, **extra: Any) -> None:
        if self._log:
            self._log.exception(msg, extra=extra or None)

    async def execute(self, *, job_id: str, specialty: str | None,
                      since: str | None, max_records: int) -> None:
        try:
            self._jobs.update(job_id, status=JobStatus.PREPARING.value,
                              started_at=self._clock.utcnow_iso())

            # 1. Fetch data
            data_path = self._sp.dataset_path(job_id)
            records = await self._fetcher.download_dpo(
                since=since, until=None, specialty=specialty,
                max_records=max_records, output=data_path,
            )
            if records == 0:
                self._jobs.update(job_id, status=JobStatus.COMPLETED.value,
                                  error_detail="no_new_data",
                                  finished_at=self._clock.utcnow_iso())
                self._info("No new DPO records — skipping", job_id=job_id)
                return

            self._jobs.update(job_id, record_count=records)

            val_path = data_path.with_suffix(".val.jsonl")
            _holdout_split(data_path, val_path, fraction=0.1)

            # 2. Resume from champion
            champion = self._adapters.get_champion(specialty, JobType.DPO)
            resume_path = Path(champion.path) if champion else None

            # 3. Train
            self._jobs.update(job_id, status=JobStatus.TRAINING.value)
            output_tag = f"hope-dpo-{specialty or 'general'}-{int(time.time())}"
            output_dir = self._sp.adapter_dir(output_tag)

            def _progress(step: int, logs: dict) -> None:
                self._jobs.update(job_id, progress={"step": step, **logs})

            def _train() -> TrainingResult:
                return self._trainer.train_dpo(
                    base_model=self._tp.base_model,
                    train_file=data_path,
                    val_file=val_path,
                    output_dir=output_dir,
                    resume_from=resume_path,
                    epochs=self._tp.dpo_epochs,
                    learning_rate=self._tp.dpo_lr,
                    batch_size=self._tp.dpo_batch,
                    grad_accum=self._tp.dpo_grad_accum,
                    max_seq_length=self._tp.max_seq_length,
                    lora_rank=self._tp.lora_rank,
                    lora_alpha=self._tp.lora_alpha,
                    lora_dropout=self._tp.lora_dropout,
                    load_in_4bit=self._tp.load_in_4bit,
                    dpo_beta=self._tp.dpo_beta,
                    max_runtime_seconds=self._tp.max_runtime_seconds,
                    nan_check_every=self._tp.nan_check_every,
                    progress_cb=_progress,
                )

            result = await self._executor.run(_train)

            self._runs.record(
                run_id=f"run_{job_id}", job_id=job_id, phase="dpo",
                result=result, succeeded=True, error_detail=None,
            )

            self._adapters.add(Adapter(
                tag=output_tag, job_id=job_id, job_type=JobType.DPO,
                specialty=specialty, base_model=self._tp.base_model,
                parent_tag=champion.tag if champion else None,
                path=str(output_dir),
                created_at=self._clock.utcnow_iso(),
            ))

            # 4. Evaluate
            self._jobs.update(job_id, status=JobStatus.EVALUATING.value,
                              output_model_tag=output_tag)
            suite_path = self._sp.golden_suite_path(specialty)

            eval_result: EvaluationResult | None = None
            if not suite_path.exists():
                self._warn("No golden suite — auto-promoting first adapter",
                           path=str(suite_path))
                should_promote = champion is None
            else:
                def _eval() -> EvaluationResult:
                    return self._evaluator.evaluate(
                        base_model=self._tp.base_model,
                        candidate_path=output_dir,
                        champion_path=Path(
                            champion.path) if champion else None,
                        suite_path=suite_path,
                        judge_url=self._ep.judge_url,
                        judge_model=self._ep.judge_model,
                        promote_win_rate=self._ep.promote_win_rate,
                        min_samples=self._ep.min_samples,
                    )

                eval_result = await self._executor.run(_eval)

                decision = decide_promotion(
                    eval_result,
                    threshold=self._ep.promote_win_rate,
                    min_samples=self._ep.min_samples,
                    wilson_min=self._ep.wilson_min,
                )
                # Override evaluator's `promote` with the domain policy
                should_promote = decision.promote

                self._evals.record(
                    eval_id=new_eval_id(),
                    job_id=job_id,
                    candidate_tag=output_tag,
                    champion_tag=champion.tag if champion else None,
                    suite=suite_path.name,
                    result=eval_result,
                )
                self._jobs.update(
                    job_id,
                    win_rate=eval_result.win_rate,
                    elo_score=1000.0 + eval_result.elo_delta,
                )

            # 5. Promote
            if should_promote:
                elo_delta = eval_result.elo_delta if eval_result else 0.0
                self._adapters.promote(output_tag, elo_delta)
                self._jobs.update(job_id, status=JobStatus.PROMOTED.value)

                if self._ollama_auto:
                    try:
                        self._registry.register(
                            tag=output_tag,
                            base_model_tag=self._tp.base_model,
                            adapter_path=str(output_dir),
                        )
                    except Exception as exc:  # noqa: BLE001
                        self._warn("Ollama registration failed", err=str(exc))
                try:
                    self._notifier.announce_champion(
                        tag=output_tag, specialty=specialty,
                        elo=1000.0 +
                        (eval_result.elo_delta if eval_result else 0.0),
                    )
                except Exception as exc:  # noqa: BLE001
                    self._warn("Champion announce failed", err=str(exc))

                self._info("Promoted new champion", tag=output_tag)
            else:
                self._jobs.update(job_id, status=JobStatus.COMPLETED.value,
                                  error_detail="did_not_beat_champion")
                self._info("Candidate did NOT beat champion", tag=output_tag)

            self._jobs.update(job_id, finished_at=self._clock.utcnow_iso())

        except Exception as exc:
            self._exc("DPO cycle failed", job_id=job_id)
            self._jobs.update(job_id, status=JobStatus.FAILED.value,
                              error_detail=str(exc)[:1000],
                              finished_at=self._clock.utcnow_iso())
            raise
