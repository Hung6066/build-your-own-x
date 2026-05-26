"""Continuous-learning scheduler — depends on `RunDpoCycleUseCase`.

Each tick:
  1. Counts new preferences since last run via the data fetcher port.
  2. If under threshold, no-op.
  3. Otherwise submits a job via `SubmitJobUseCase` and runs the cycle
     under the global concurrency lock.
"""

from __future__ import annotations

import asyncio
from datetime import datetime, timezone

from apscheduler.schedulers.asyncio import AsyncIOScheduler
from apscheduler.triggers.cron import CronTrigger

from ..infra.config import Settings
from ..infra.correlation import set_correlation_id
from ..infra.logging import get_logger
from ..ports import TrainingDataFetcher
from ..use_cases import (RunDpoCycleUseCase, SubmitJobInput, SubmitJobUseCase)
from ..domain import JobType

log = get_logger("scheduler")


class ContinuousLearningScheduler:
    def __init__(self, *,
                 settings: Settings,
                 fetcher: TrainingDataFetcher,
                 submit_uc: SubmitJobUseCase,
                 cycle_uc: RunDpoCycleUseCase,
                 job_lock: asyncio.Semaphore):
        self._settings = settings
        self._fetcher = fetcher
        self._submit = submit_uc
        self._cycle = cycle_uc
        self._lock = job_lock
        self._scheduler = AsyncIOScheduler()
        self._last_run_iso: str | None = None

    def start(self) -> None:
        if not self._settings.auto_train_enabled:
            log.info("Continuous learning disabled")
            return
        trigger = CronTrigger.from_crontab(self._settings.auto_train_cron)
        self._scheduler.add_job(self._tick, trigger, id="cl-tick",
                                replace_existing=True, max_instances=1)
        self._scheduler.start()
        log.info("Continuous learning scheduler started",
                 extra={"cron": self._settings.auto_train_cron,
                        "min_new_pairs": self._settings.auto_train_min_new_pairs})

    def stop(self) -> None:
        if self._scheduler.running:
            self._scheduler.shutdown(wait=False)

    async def _tick(self) -> None:
        set_correlation_id()  # fresh correlation per tick
        log.info("Continuous learning tick")
        try:
            count = await self._fetcher.preference_count(self._last_run_iso)
        except Exception as exc:  # noqa: BLE001
            log.warning("Could not query preference count",
                        extra={"err": str(exc)})
            return

        if count < self._settings.auto_train_min_new_pairs:
            log.info("Not enough new data — skipping",
                     extra={"new_pairs": count,
                            "threshold": self._settings.auto_train_min_new_pairs})
            return

        async with self._lock:
            job = self._submit.execute(SubmitJobInput(
                job_type=JobType.DPO, specialty=None,
                since=self._last_run_iso, max_records=5000,
            ))
            since = self._last_run_iso
            self._last_run_iso = datetime.now(timezone.utc).isoformat()
            log.info("Auto-train kicked off",
                     extra={"job_id": job.id, "new_pairs": count})
            try:
                await self._cycle.execute(
                    job_id=job.id, specialty=None,
                    since=since, max_records=5000,
                )
            except Exception:
                log.exception("Auto-train cycle errored")
