"""ASGI middlewares: correlation-ID and Prometheus metrics."""

from __future__ import annotations

import time

from starlette.middleware.base import BaseHTTPMiddleware
from starlette.requests import Request
from starlette.responses import Response

from ..infra.correlation import set_correlation_id
from ..infra.metrics import LATENCY, REQUESTS


class CorrelationIdMiddleware(BaseHTTPMiddleware):
    """Reads X-Correlation-Id (or generates one) and echoes it back."""

    HEADER = "X-Correlation-Id"

    async def dispatch(self, request: Request, call_next):
        incoming = request.headers.get(self.HEADER)
        cid = set_correlation_id(incoming)
        response: Response = await call_next(request)
        response.headers[self.HEADER] = cid
        return response


class MetricsMiddleware(BaseHTTPMiddleware):
    async def dispatch(self, request: Request, call_next):
        route = request.url.path
        t0 = time.time()
        try:
            response = await call_next(request)
            REQUESTS.labels(route, str(response.status_code)).inc()
            return response
        except Exception:
            REQUESTS.labels(route, "500").inc()
            raise
        finally:
            LATENCY.labels(route).observe(time.time() - t0)
