"""Application layer: ports (Protocol interfaces).

These define the contracts that infrastructure must satisfy. Use cases
depend ONLY on these abstractions, never on concrete implementations.

This is the heart of the Dependency Inversion Principle.
"""

from __future__ import annotations

from pathlib import Path
from typing import Any, Awaitable, Callable, Protocol, runtime_checkable

from .domain import (Adapter, EvaluationResult, Job, JobStatus, JobType,
                     TrainingResult)


# ── Persistence ports ────────────────────────────────────────────────────────

@runtime_checkable
class JobRepository(Protocol):
    def create(self, job: Job) -> None: ...
    def get(self, job_id: str) -> Job | None: ...
    def list(self, take: int = 50) -> list[Job]: ...
    def active(self) -> list[Job]: ...
    def update(self, job_id: str, **fields: Any) -> None: ...


@runtime_checkable
class AdapterRepository(Protocol):
    def add(self, adapter: Adapter) -> None: ...
    def get(self, tag: str) -> Adapter | None: ...

    def list(self, specialty: str | None = None,
             take: int = 20) -> list[Adapter]: ...
    def get_champion(self, specialty: str | None,
                     job_type: JobType) -> Adapter | None: ...

    def promote(self, tag: str, elo_delta: float) -> None: ...


@runtime_checkable
class EvaluationRepository(Protocol):
    def record(self, *, eval_id: str, job_id: str, candidate_tag: str,
               champion_tag: str | None, suite: str,
               result: EvaluationResult) -> None: ...

    def recent(self, take: int = 20) -> list[dict]: ...


@runtime_checkable
class TrainingRunRepository(Protocol):
    def record(self, *, run_id: str, job_id: str, phase: str,
               result: TrainingResult, succeeded: bool,
               error_detail: str | None) -> None: ...


# ── External-service ports ───────────────────────────────────────────────────

@runtime_checkable
class TrainingDataFetcher(Protocol):
    """Pulls preference / SFT data from the main Hope.Agent API."""

    async def download_dpo(self, *, since: str | None, until: str | None,
                           specialty: str | None, max_records: int | None,
                           output: Path) -> int: ...

    async def preference_count(self, since: str | None) -> int: ...


@runtime_checkable
class ModelTrainer(Protocol):
    """Executes a single training run (LoRA/QLoRA SFT or DPO).

    Implementations may be blocking. Use-cases must offload to an executor.
    """

    def train_dpo(self, *, base_model: str, train_file: Path, val_file: Path,
                  output_dir: Path, resume_from: Path | None,
                  epochs: int, learning_rate: float, batch_size: int,
                  grad_accum: int, max_seq_length: int,
                  lora_rank: int, lora_alpha: int, lora_dropout: float,
                  load_in_4bit: bool, dpo_beta: float,
                  max_runtime_seconds: float, nan_check_every: int,
                  progress_cb: Callable[[int, dict], None] | None,
                  ) -> TrainingResult: ...


@runtime_checkable
class ChampionEvaluator(Protocol):
    """Compares a candidate adapter against the current champion via an LLM judge."""

    def evaluate(self, *, base_model: str,
                 candidate_path: Path, champion_path: Path | None,
                 suite_path: Path, judge_url: str, judge_model: str,
                 promote_win_rate: float, min_samples: int,
                 ) -> EvaluationResult: ...


@runtime_checkable
class InferenceRegistry(Protocol):
    """Registers a promoted adapter with the inference server (Ollama)."""

    def register(self, *, tag: str, base_model_tag: str,
                 adapter_path: str) -> None: ...


@runtime_checkable
class AgentNotifier(Protocol):
    """Notifies the main Hope.Agent .NET service of a new champion."""

    def announce_champion(self, *, tag: str, specialty: str | None,
                          elo: float) -> None: ...


# ── Utility ports ────────────────────────────────────────────────────────────

@runtime_checkable
class Clock(Protocol):
    def utcnow_iso(self) -> str: ...


@runtime_checkable
class JobExecutor(Protocol):
    """Runs blocking work off the asyncio event loop."""

    async def run(self, fn: Callable[[], Any]) -> Any: ...


# Progress callback type for the orchestrator.
ProgressCallback = Callable[[int, dict], None]
