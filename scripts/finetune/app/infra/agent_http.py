"""Hope.Agent HTTP client — implements `TrainingDataFetcher` port.

Adds production-grade resilience: tenacity exponential-backoff retries on
transient HTTP errors; correlation-ID header propagation; structured logging
of failures.
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

import httpx
from tenacity import (RetryError, retry, retry_if_exception_type,
                      stop_after_attempt, wait_exponential)

from .correlation import get_correlation_id
from .logging import get_logger

log = get_logger(__name__)


_RETRYABLE = (
    # ConnectError, ReadTimeout, NetworkError, etc.
    httpx.TransportError,
    httpx.RemoteProtocolError,
    httpx.HTTPStatusError,       # 5xx via raise_for_status
)


def _is_retryable_status(exc: BaseException) -> bool:
    if isinstance(exc, httpx.HTTPStatusError):
        return 500 <= exc.response.status_code < 600
    return isinstance(exc, _RETRYABLE)


class AgentHttpClient:
    """Fetches preference data and counts from the .NET Hope.Agent API."""

    def __init__(self, base_url: str, token: str,
                 timeout: float = 600.0,
                 retry_attempts: int = 3,
                 retry_backoff: float = 1.5):
        self._base_url = base_url.rstrip("/")
        self._token = token
        self._timeout = timeout
        self._attempts = max(1, retry_attempts)
        self._backoff = retry_backoff

    def _headers(self) -> dict[str, str]:
        h = {
            "Accept": "application/x-ndjson",
            "X-Correlation-Id": get_correlation_id(),
        }
        if self._token:
            h["Authorization"] = f"Bearer {self._token}"
        return h

    def _retry(self):
        return retry(
            stop=stop_after_attempt(self._attempts),
            wait=wait_exponential(multiplier=self._backoff, min=1, max=30),
            retry=retry_if_exception_type(_RETRYABLE),
            reraise=True,
        )

    async def download_dpo(self, *, since: str | None, until: str | None,
                           specialty: str | None, max_records: int | None,
                           output: Path) -> int:
        body = _make_export_body(since=since, until=until,
                                 specialty=specialty, max_records=max_records)

        @self._retry()
        async def _do() -> int:
            async with httpx.AsyncClient(timeout=self._timeout) as client:
                async with client.stream(
                    "POST", f"{self._base_url}/v1/training/export/dpo",
                    headers=self._headers(), json=body,
                ) as r:
                    r.raise_for_status()
                    output.parent.mkdir(parents=True, exist_ok=True)
                    with open(output, "wb") as f:
                        async for chunk in r.aiter_bytes():
                            f.write(chunk)
            return _count_lines(output)

        try:
            n = await _do()
        except _RETRYABLE as exc:
            log.error("download_dpo exhausted retries",
                      extra={"err": str(exc), "endpoint": "/v1/training/export/dpo"})
            raise
        log.info("Downloaded DPO export",
                 extra={"records": n, "path": str(output)})
        return n

    async def preference_count(self, since: str | None) -> int:
        @self._retry()
        async def _do() -> int:
            async with httpx.AsyncClient(timeout=30) as client:
                r = await client.get(
                    f"{self._base_url}/v1/training/preference/count",
                    headers=self._headers(),
                    params={"since": since} if since else None,
                )
                r.raise_for_status()
                return int(r.json().get("count", 0))

        try:
            return await _do()
        except _RETRYABLE as exc:
            log.warning("preference_count failed",
                        extra={"err": str(exc)})
            return 0  # graceful — scheduler will retry next tick


# ── Hashing helpers (re-used from legacy module) ─────────────────────────────

def hash_jsonl(path: Path) -> str:
    h = hashlib.sha256()
    if not path.exists():
        return ""
    with open(path, "rb") as f:
        lines = sorted(line.strip() for line in f if line.strip())
    for line in lines:
        h.update(line)
        h.update(b"\n")
    return h.hexdigest()


def config_hash(config: dict) -> str:
    canonical = json.dumps(config, sort_keys=True, ensure_ascii=False).encode()
    return hashlib.sha256(canonical).hexdigest()


def _make_export_body(*, since: str | None, until: str | None,
                      specialty: str | None, max_records: int | None) -> dict:
    body: dict = {"redactPhi": True}
    if since:
        body["since"] = since
    if until:
        body["until"] = until
    if specialty:
        body["specialty"] = specialty
    if max_records:
        body["maxRecords"] = max_records
    return body


def _count_lines(path: Path) -> int:
    if not path.exists():
        return 0
    with open(path, "rb") as f:
        return sum(1 for line in f if line.strip())
