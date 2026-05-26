"""Domain-error → HTTP-response mapping."""

from __future__ import annotations

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse

from ..domain import DomainError
from ..infra.correlation import get_correlation_id
from ..infra.logging import get_logger

log = get_logger("api.errors")


def install_exception_handlers(app: FastAPI) -> None:
    @app.exception_handler(DomainError)
    async def _domain_handler(_request: Request, exc: DomainError):
        return JSONResponse(
            status_code=exc.http_status,
            content={
                "error": exc.__class__.__name__,
                "detail": str(exc),
                "correlation_id": get_correlation_id(),
            },
        )

    @app.exception_handler(Exception)
    async def _unhandled(_request: Request, exc: Exception):  # noqa: BLE001
        log.exception("Unhandled exception", extra={"err": str(exc)})
        return JSONResponse(
            status_code=500,
            content={
                "error": "InternalServerError",
                "detail": "An internal error occurred",
                "correlation_id": get_correlation_id(),
            },
        )
