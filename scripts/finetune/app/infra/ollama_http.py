"""Ollama inference-registry adapter + champion-announcer adapter.

Both are best-effort: failures are logged but never block promotion of
the new champion in the registry.
"""

from __future__ import annotations

import httpx
from tenacity import (retry, retry_if_exception_type, stop_after_attempt,
                      wait_exponential)

from .correlation import get_correlation_id
from .logging import get_logger

log = get_logger(__name__)


_RETRYABLE = (httpx.TransportError, httpx.RemoteProtocolError)


def _hdrs(token: str | None = None) -> dict[str, str]:
    h = {"X-Correlation-Id": get_correlation_id()}
    if token:
        h["Authorization"] = f"Bearer {token}"
    return h


class OllamaInferenceRegistry:
    """Registers a LoRA adapter with the local Ollama server."""

    def __init__(self, base_url: str, attempts: int = 3, backoff: float = 1.5):
        self._url = base_url.rstrip("/")
        self._attempts = max(1, attempts)
        self._backoff = backoff

    def register(self, *, tag: str, base_model_tag: str,
                 adapter_path: str) -> None:
        modelfile = (
            f"FROM {base_model_tag}\n"
            f"ADAPTER {adapter_path}\n"
        )
        payload = {"name": tag, "modelfile": modelfile, "stream": False}

        @retry(
            stop=stop_after_attempt(self._attempts),
            wait=wait_exponential(multiplier=self._backoff, min=1, max=15),
            retry=retry_if_exception_type(_RETRYABLE),
            reraise=True,
        )
        def _do() -> None:
            with httpx.Client(timeout=120) as client:
                r = client.post(f"{self._url}/api/create", headers=_hdrs(),
                                json=payload)
                r.raise_for_status()

        _do()
        log.info("Ollama registered adapter", extra={"tag": tag})


class AgentHttpNotifier:
    """Notifies the .NET Hope.Agent service that a new champion is live."""

    def __init__(self, base_url: str, token: str,
                 attempts: int = 3, backoff: float = 1.5):
        self._url = base_url.rstrip("/")
        self._token = token
        self._attempts = max(1, attempts)
        self._backoff = backoff

    def announce_champion(self, *, tag: str, specialty: str | None,
                          elo: float) -> None:
        payload = {"tag": tag, "specialty": specialty, "elo": elo}

        @retry(
            stop=stop_after_attempt(self._attempts),
            wait=wait_exponential(multiplier=self._backoff, min=1, max=15),
            retry=retry_if_exception_type(_RETRYABLE),
            reraise=True,
        )
        def _do() -> None:
            with httpx.Client(timeout=30) as client:
                r = client.post(
                    f"{self._url}/v1/training/champion",
                    headers=_hdrs(self._token), json=payload,
                )
                # 4xx are not retried (logic error); 5xx are
                if r.status_code >= 500:
                    r.raise_for_status()
                if r.status_code >= 400:
                    log.warning("Agent rejected champion announce",
                                extra={"status": r.status_code,
                                       "body": r.text[:200]})

        _do()
        log.info("Announced champion", extra={"tag": tag, "elo": elo})
