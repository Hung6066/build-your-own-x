"""HTTP routers — thin layer over use cases."""

from __future__ import annotations

import asyncio
from typing import Literal

from fastapi import APIRouter, BackgroundTasks, Depends, Query, Request
from fastapi.responses import Response
from prometheus_client import CONTENT_TYPE_LATEST, generate_latest

from ..domain import JobType
from ..infra.logging import get_logger
from ..infra.metrics import ACTIVE_JOBS, FAILURES, PROMOTIONS
from ..use_cases import (CancelJobUseCase, GetChampionUseCase, GetJobUseCase,
                         ListAdaptersUseCase, ListJobsUseCase,
                         PromoteChampionUseCase, RunDpoCycleUseCase,
                         SubmitJobInput, SubmitJobUseCase)
from .deps import (cancel_job_use_case, get_champion_use_case,
                   get_job_use_case, list_adapters_use_case,
                   list_jobs_use_case, promote_champion_use_case,
                   submit_job_use_case)
from .schemas import AdapterOut, JobOut, SubmitJobRequest
from .security import require_api_key

log = get_logger("api.routers")


health = APIRouter()


@health.get("/healthz")
async def healthz() -> dict:
    return {"status": "ok"}


@health.get("/readyz")
async def readyz(request: Request) -> dict:
    request.app.state.settings.ensure_dirs()
    return {"status": "ready"}


@health.get("/metrics")
async def metrics() -> Response:
    return Response(generate_latest(), media_type=CONTENT_TYPE_LATEST)


# ── Jobs ─────────────────────────────────────────────────────────────────────

jobs = APIRouter(prefix="/jobs", dependencies=[Depends(require_api_key)])


@jobs.post("", response_model=JobOut)
async def submit_job(body: SubmitJobRequest, bg: BackgroundTasks,
                     request: Request,
                     uc: SubmitJobUseCase = Depends(submit_job_use_case)) -> JobOut:
    job = uc.execute(SubmitJobInput(
        job_type=JobType(body.job_type),
        specialty=body.specialty,
        since=body.since,
        max_records=body.max_records,
    ))

    cycle: RunDpoCycleUseCase = request.app.state.uc_run_cycle
    lock: asyncio.Semaphore = request.app.state.job_lock

    async def _run():
        async with lock:
            ACTIVE_JOBS.inc()
            try:
                await cycle.execute(
                    job_id=job.id, specialty=body.specialty,
                    since=body.since, max_records=body.max_records,
                )
                final = request.app.state.job_repo.get(job.id)
                if final and final.status.value == "promoted":
                    PROMOTIONS.inc()
            except Exception:  # noqa: BLE001
                FAILURES.inc()
                log.exception("Job failed", extra={"job_id": job.id})
            finally:
                ACTIVE_JOBS.dec()

    bg.add_task(_run)
    return JobOut.from_entity(job)


@jobs.get("", response_model=list[JobOut])
async def list_jobs_route(take: int = Query(50, ge=1, le=500),
                          uc: ListJobsUseCase = Depends(list_jobs_use_case)
                          ) -> list[JobOut]:
    return [JobOut.from_entity(j) for j in uc.execute(take)]


@jobs.get("/{job_id}", response_model=JobOut)
async def get_job_route(job_id: str,
                        uc: GetJobUseCase = Depends(get_job_use_case)) -> JobOut:
    return JobOut.from_entity(uc.execute(job_id))


@jobs.delete("/{job_id}", response_model=JobOut)
async def cancel_job_route(job_id: str,
                           uc: CancelJobUseCase = Depends(cancel_job_use_case)
                           ) -> JobOut:
    return JobOut.from_entity(uc.execute(job_id))


# ── Adapters / Champion ──────────────────────────────────────────────────────

adapters = APIRouter(dependencies=[Depends(require_api_key)])


@adapters.get("/adapters", response_model=list[AdapterOut])
async def list_adapters_route(specialty: str | None = None,
                              take: int = Query(20, ge=1, le=200),
                              uc: ListAdaptersUseCase = Depends(
                                  list_adapters_use_case)
                              ) -> list[AdapterOut]:
    return [AdapterOut.from_entity(a) for a in uc.execute(specialty, take)]


@adapters.get("/champion", response_model=AdapterOut)
async def get_champion_route(specialty: str | None = None,
                             job_type: Literal["dpo", "sft"] = "dpo",
                             uc: GetChampionUseCase = Depends(
                                 get_champion_use_case)
                             ) -> AdapterOut:
    return AdapterOut.from_entity(uc.execute(specialty, JobType(job_type)))


@adapters.post("/champion/{tag}/promote", response_model=AdapterOut)
async def promote_champion_route(tag: str,
                                 uc: PromoteChampionUseCase = Depends(
                                     promote_champion_use_case)
                                 ) -> AdapterOut:
    return AdapterOut.from_entity(uc.execute(tag))


# ── Evaluations ──────────────────────────────────────────────────────────────

evaluations = APIRouter(dependencies=[Depends(require_api_key)])


@evaluations.get("/evaluations")
async def recent_evaluations_route(take: int = Query(20, ge=1, le=200),
                                   request: Request = None) -> list[dict]:
    return request.app.state.eval_repo.recent(take)
