"""Prometheus metrics — singletons exported for routers + use cases."""

from __future__ import annotations

from prometheus_client import Counter, Gauge, Histogram

REQUESTS = Counter(
    "hope_ft_requests_total", "API requests", ["route", "status"]
)
LATENCY = Histogram(
    "hope_ft_request_seconds", "API latency", ["route"]
)
ACTIVE_JOBS = Gauge("hope_ft_active_jobs", "Active jobs")
PROMOTIONS = Counter("hope_ft_promotions_total", "Successful promotions")
FAILURES = Counter("hope_ft_failures_total", "Failed jobs")
TRAINING_DURATION = Histogram(
    "hope_ft_training_duration_seconds",
    "End-to-end training-cycle duration",
    buckets=(60, 300, 900, 1800, 3600, 7200, 14400, 28800, 43200),
)
DATA_RECORDS = Histogram(
    "hope_ft_data_records", "Records fetched per cycle",
    buckets=(0, 50, 200, 500, 1000, 5000, 10000, 50000),
)
