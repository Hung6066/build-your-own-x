"""FastAPI dependency providers — wire ports into use cases on demand.

Dependencies cache singletons on `app.state` (populated in lifespan).
"""

from __future__ import annotations

from fastapi import Request

from ..use_cases import (CancelJobUseCase, GetChampionUseCase, GetJobUseCase,
                         ListAdaptersUseCase, ListJobsUseCase,
                         PromoteChampionUseCase, SubmitJobUseCase)


def _state(request: Request):
    return request.app.state


def get_settings_dep(request: Request):
    return _state(request).settings


def get_job_repo(request: Request):
    return _state(request).job_repo


def get_adapter_repo(request: Request):
    return _state(request).adapter_repo


def get_evaluation_repo(request: Request):
    return _state(request).eval_repo


def submit_job_use_case(request: Request) -> SubmitJobUseCase:
    return _state(request).uc_submit_job


def get_job_use_case(request: Request) -> GetJobUseCase:
    return _state(request).uc_get_job


def list_jobs_use_case(request: Request) -> ListJobsUseCase:
    return _state(request).uc_list_jobs


def cancel_job_use_case(request: Request) -> CancelJobUseCase:
    return _state(request).uc_cancel_job


def list_adapters_use_case(request: Request) -> ListAdaptersUseCase:
    return _state(request).uc_list_adapters


def get_champion_use_case(request: Request) -> GetChampionUseCase:
    return _state(request).uc_get_champion


def promote_champion_use_case(request: Request) -> PromoteChampionUseCase:
    return _state(request).uc_promote
