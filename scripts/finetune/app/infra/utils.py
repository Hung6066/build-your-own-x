"""Misc small adapters: Clock, asyncio executor."""

from __future__ import annotations

import asyncio
from datetime import datetime, timezone
from typing import Any, Callable


class SystemClock:
    def utcnow_iso(self) -> str:
        return datetime.now(timezone.utc).isoformat()


class AsyncioExecutor:
    """Runs blocking work in the default thread-pool executor."""

    async def run(self, fn: Callable[[], Any]) -> Any:
        loop = asyncio.get_running_loop()
        return await loop.run_in_executor(None, fn)
