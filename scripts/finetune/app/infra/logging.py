"""Structured JSON logging with correlation-ID enrichment."""

from __future__ import annotations

import datetime as _dt
import json
import logging
import logging.handlers
from pathlib import Path
from typing import Any

from .correlation import get_correlation_id

_RESERVED = {
    "name", "msg", "args", "levelname", "levelno", "pathname", "filename",
    "module", "exc_info", "exc_text", "stack_info", "lineno", "funcName",
    "created", "msecs", "relativeCreated", "thread", "threadName",
    "processName", "process", "message", "asctime", "taskName",
}


class JsonFormatter(logging.Formatter):
    def format(self, record: logging.LogRecord) -> str:
        payload: dict[str, Any] = {
            "ts": _dt.datetime.fromtimestamp(record.created, _dt.timezone.utc).isoformat(),
            "level": record.levelname,
            "logger": record.name,
            "msg": record.getMessage(),
            "cid": get_correlation_id(),
        }
        if record.exc_info:
            payload["exc"] = self.formatException(record.exc_info)
        for k, v in record.__dict__.items():
            if k in _RESERVED or k.startswith("_"):
                continue
            try:
                json.dumps(v)
                payload[k] = v
            except (TypeError, ValueError):
                payload[k] = repr(v)
        return json.dumps(payload, ensure_ascii=False)


def configure_logging(level: str = "INFO", json_format: bool = True,
                      logs_dir: Path | None = None) -> None:
    root = logging.getLogger()
    root.handlers.clear()
    root.setLevel(level.upper())

    fmt: logging.Formatter
    if json_format:
        fmt = JsonFormatter()
    else:
        fmt = logging.Formatter(
            "%(asctime)s %(levelname)s [%(name)s] %(message)s"
        )

    sh = logging.StreamHandler()
    sh.setFormatter(fmt)
    root.addHandler(sh)

    if logs_dir is not None:
        logs_dir.mkdir(parents=True, exist_ok=True)
        fh = logging.handlers.RotatingFileHandler(
            logs_dir / "training.log",
            maxBytes=20 * 1024 * 1024, backupCount=10, encoding="utf-8",
        )
        fh.setFormatter(fmt)
        root.addHandler(fh)

    # Tame noisy libs
    for noisy in ("httpx", "httpcore", "transformers.tokenization_utils_base",
                  "urllib3", "apscheduler"):
        logging.getLogger(noisy).setLevel(logging.WARNING)


def get_logger(name: str) -> logging.Logger:
    return logging.getLogger(name)
