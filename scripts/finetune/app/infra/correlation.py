"""Correlation-ID context for distributed tracing across async work."""

from __future__ import annotations

import uuid
from contextvars import ContextVar

_correlation_id: ContextVar[str] = ContextVar("correlation_id", default="-")


def set_correlation_id(value: str | None = None) -> str:
    cid = value or uuid.uuid4().hex[:16]
    _correlation_id.set(cid)
    return cid


def get_correlation_id() -> str:
    return _correlation_id.get()
