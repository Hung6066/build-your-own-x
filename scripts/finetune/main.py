"""Entry point. Run with `python -m main` or `uvicorn main:app --factory`."""

from __future__ import annotations

import uvicorn

from app.api.app import create_app
from app.infra.config import get_settings


def main() -> None:
    settings = get_settings()
    uvicorn.run(
        "app.api.app:create_app",
        host=settings.host,
        port=settings.port,
        factory=True,
        log_config=None,        # we configure logging inside lifespan
        access_log=False,        # access log handled by MetricsMiddleware
    )


if __name__ == "__main__":
    main()
