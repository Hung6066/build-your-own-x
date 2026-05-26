"""Pydantic DTOs exposed at the HTTP boundary."""

from __future__ import annotations

from typing import Literal

from pydantic import BaseModel, Field

from ..domain import Adapter, Job, JobType


class SubmitJobRequest(BaseModel):
    job_type: Literal["dpo"] = "dpo"
    specialty: str | None = None
    since: str | None = None
    max_records: int = Field(default=5000, ge=1, le=50_000)


class JobOut(BaseModel):
    id: str
    job_type: str
    base_model: str
    output_tag: str | None = None
    status: str
    specialty: str | None = None
    record_count: int = 0
    win_rate: float | None = None
    elo_score: float | None = None
    error_detail: str | None = None
    progress: str | None = None
    created_at: str
    started_at: str | None = None
    finished_at: str | None = None

    @classmethod
    def from_entity(cls, j: Job) -> "JobOut":
        return cls(
            id=j.id, job_type=j.job_type.value, base_model=j.base_model,
            output_tag=j.output_tag, status=j.status.value, specialty=j.specialty,
            record_count=j.record_count, win_rate=j.win_rate,
            elo_score=j.elo_score, error_detail=j.error_detail,
            progress=j.progress, created_at=j.created_at,
            started_at=j.started_at, finished_at=j.finished_at,
        )


class AdapterOut(BaseModel):
    tag: str
    job_id: str
    job_type: str
    specialty: str | None = None
    base_model: str
    parent_tag: str | None = None
    path: str
    elo_rating: float
    is_champion: bool
    promoted_at: str | None = None
    retired_at: str | None = None
    created_at: str

    @classmethod
    def from_entity(cls, a: Adapter) -> "AdapterOut":
        return cls(
            tag=a.tag, job_id=a.job_id, job_type=a.job_type.value,
            specialty=a.specialty, base_model=a.base_model,
            parent_tag=a.parent_tag, path=a.path,
            elo_rating=a.elo_rating, is_champion=a.is_champion,
            promoted_at=a.promoted_at, retired_at=a.retired_at,
            created_at=a.created_at,
        )
