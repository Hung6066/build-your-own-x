"""Domain layer — pure entities, value objects, and exceptions.

NO dependencies on any other layer. Anything imported here must be
either stdlib or a primitive type. This guarantees the domain is
trivially testable and portable.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import Enum
from typing import Any


# ── Value Objects ────────────────────────────────────────────────────────────

class JobStatus(str, Enum):
    PENDING = "pending"
    PREPARING = "preparing"
    TRAINING = "training"
    EVALUATING = "evaluating"
    PROMOTED = "promoted"
    COMPLETED = "completed"
    FAILED = "failed"
    CANCELLED = "cancelled"

    @property
    def is_terminal(self) -> bool:
        return self in (
            JobStatus.PROMOTED, JobStatus.COMPLETED,
            JobStatus.FAILED, JobStatus.CANCELLED,
        )

    @property
    def is_active(self) -> bool:
        return self in (
            JobStatus.PENDING, JobStatus.PREPARING,
            JobStatus.TRAINING, JobStatus.EVALUATING,
        )


class JobType(str, Enum):
    DPO = "dpo"
    SFT = "sft"


# ── Entities ─────────────────────────────────────────────────────────────────

@dataclass(slots=True)
class Job:
    id: str
    job_type: JobType
    base_model: str
    status: JobStatus = JobStatus.PENDING
    specialty: str | None = None
    output_tag: str | None = None
    record_count: int = 0
    win_rate: float | None = None
    elo_score: float | None = None
    error_detail: str | None = None
    progress: str | None = None
    data_hash: str | None = None
    created_at: str = ""
    started_at: str | None = None
    finished_at: str | None = None

    def transition(self, new_status: JobStatus) -> None:
        """Validate state transition. Terminal states are absorbing."""
        if self.status.is_terminal:
            raise InvalidJobStateTransition(
                f"Cannot transition from terminal status {self.status} to {new_status}"
            )
        self.status = new_status


@dataclass(slots=True)
class Adapter:
    tag: str
    job_id: str
    job_type: JobType
    base_model: str
    path: str
    specialty: str | None = None
    parent_tag: str | None = None
    elo_rating: float = 1000.0
    is_champion: bool = False
    promoted_at: str | None = None
    retired_at: str | None = None
    created_at: str = ""


@dataclass(slots=True)
class EvaluationResult:
    """Outcome of a champion-vs-challenger comparison."""
    total: int
    candidate_wins: int
    ties: int
    champion_wins: int
    win_rate: float
    elo_delta: float
    promote: bool
    details: list[dict] = field(default_factory=list)

    @property
    def scored(self) -> int:
        return self.candidate_wins + self.champion_wins


@dataclass(slots=True)
class TrainingResult:
    output_dir: str
    final_loss: float | None
    final_eval_loss: float | None
    steps: int
    samples: int
    duration_sec: float
    metrics: list[dict] = field(default_factory=list)


@dataclass(slots=True)
class PromotionDecision:
    """Computed by the promotion-gate policy."""
    promote: bool
    reason: str
    win_rate: float
    scored: int
    wilson_lower: float = 0.0


# ── Domain policies ──────────────────────────────────────────────────────────

def wilson_lower_bound(wins: int, n: int, z: float = 1.96) -> float:
    """Wilson score confidence interval lower bound (95 % CI by default).

    Returns the pessimistic estimate of the true win-rate. Using this
    prevents promoting a model that got lucky on a small sample.
    """
    if n == 0:
        return 0.0
    p = wins / n
    denom = 1 + z * z / n
    centre = p + z * z / (2 * n)
    spread = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n))
    return (centre - spread) / denom


def decide_promotion(eval_result: EvaluationResult, *,
                     threshold: float,
                     min_samples: int,
                     wilson_min: float = 0.0) -> PromotionDecision:
    """Pure policy: does this evaluation justify promotion?

    Args:
        threshold:    Minimum observed win-rate  (e.g. 0.55 vs local champion,
                      0.58 vs cloud baseline).
        min_samples:  Minimum number of scored pairs (candidate_wins + champion_wins).
        wilson_min:   Minimum Wilson-CI lower bound (0 = disabled).  Set to 0.50
                      when comparing against the cloud baseline to require that
                      the local model is *statistically* better, not just luckier.
    """
    wlb = wilson_lower_bound(eval_result.candidate_wins, eval_result.scored)

    if eval_result.scored < min_samples:
        return PromotionDecision(
            promote=False,
            reason=f"insufficient_samples ({eval_result.scored} < {min_samples})",
            win_rate=eval_result.win_rate, scored=eval_result.scored,
            wilson_lower=wlb,
        )
    if eval_result.win_rate < threshold:
        return PromotionDecision(
            promote=False,
            reason=f"win_rate_below_threshold ({eval_result.win_rate:.3f} < {threshold})",
            win_rate=eval_result.win_rate, scored=eval_result.scored,
            wilson_lower=wlb,
        )
    if wilson_min > 0 and wlb < wilson_min:
        return PromotionDecision(
            promote=False,
            reason=f"wilson_ci_too_wide (lower={wlb:.3f} < {wilson_min})",
            win_rate=eval_result.win_rate, scored=eval_result.scored,
            wilson_lower=wlb,
        )
    return PromotionDecision(
        promote=True, reason="ok",
        win_rate=eval_result.win_rate, scored=eval_result.scored,
        wilson_lower=wlb,
    )


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


# ── Exceptions ───────────────────────────────────────────────────────────────

class DomainError(Exception):
    """Base for all domain-level errors. Mapped to HTTP 4xx by the API layer."""
    http_status: int = 400


class JobNotFound(DomainError):
    http_status = 404


class AdapterNotFound(DomainError):
    http_status = 404


class ChampionNotFound(DomainError):
    http_status = 404


class InvalidJobStateTransition(DomainError):
    http_status = 409


class NoTrainingData(DomainError):
    """Raised when a cycle would train on zero records."""
    http_status = 422


class TrainingFailed(DomainError):
    """Wraps any trainer-side failure (NaN, OOM, etc.)."""
    http_status = 500


# Convenience: map a raw dict (from a repo row) into a Job entity.
def job_from_row(row: dict[str, Any]) -> Job:
    return Job(
        id=row["id"],
        job_type=JobType(row["job_type"]),
        base_model=row["base_model"],
        status=JobStatus(row["status"]),
        specialty=row.get("specialty"),
        output_tag=row.get("output_tag") or row.get("output_model_tag"),
        record_count=row.get("record_count") or 0,
        win_rate=row.get("win_rate"),
        elo_score=row.get("elo_score"),
        error_detail=row.get("error_detail"),
        progress=row.get("progress"),
        data_hash=row.get("data_hash"),
        created_at=row.get("created_at") or "",
        started_at=row.get("started_at"),
        finished_at=row.get("finished_at"),
    )


def adapter_from_row(row: dict[str, Any]) -> Adapter:
    return Adapter(
        tag=row["tag"],
        job_id=row["job_id"],
        job_type=JobType(row["job_type"]),
        base_model=row["base_model"],
        path=row["path"],
        specialty=row.get("specialty"),
        parent_tag=row.get("parent_tag"),
        elo_rating=row.get("elo_rating") or 1000.0,
        is_champion=bool(row.get("is_champion")),
        promoted_at=row.get("promoted_at"),
        retired_at=row.get("retired_at"),
        created_at=row.get("created_at") or "",
    )
