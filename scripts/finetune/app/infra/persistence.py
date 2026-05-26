"""SQLite-backed implementations of the repository ports.

The schema is identical to the legacy `registry.py` so existing DB files
remain compatible. Mapping between SQLite rows and domain entities lives
here, keeping the domain layer 100% storage-agnostic.
"""

from __future__ import annotations

import json
import sqlite3
import threading
from contextlib import contextmanager
from pathlib import Path
from typing import Any, Iterator

from ..domain import (Adapter, EvaluationResult, Job, JobType,
                      adapter_from_row, job_from_row, utc_now_iso)
from .logging import get_logger

log = get_logger(__name__)


_SCHEMA = """
CREATE TABLE IF NOT EXISTS jobs (
    id              TEXT PRIMARY KEY,
    job_type        TEXT NOT NULL,
    base_model      TEXT NOT NULL,
    output_tag      TEXT,
    specialty       TEXT,
    status          TEXT NOT NULL,
    progress        TEXT,
    output_model_tag TEXT,
    elo_score       REAL,
    win_rate        REAL,
    error_detail    TEXT,
    data_hash       TEXT,
    config_hash     TEXT,
    record_count    INTEGER DEFAULT 0,
    created_at      TEXT NOT NULL,
    started_at      TEXT,
    finished_at     TEXT
);
CREATE INDEX IF NOT EXISTS idx_jobs_status ON jobs(status, created_at);

CREATE TABLE IF NOT EXISTS adapters (
    tag             TEXT PRIMARY KEY,
    job_id          TEXT NOT NULL,
    job_type        TEXT NOT NULL,
    specialty       TEXT,
    base_model      TEXT NOT NULL,
    parent_tag      TEXT,
    path            TEXT NOT NULL,
    elo_rating      REAL DEFAULT 1000.0,
    is_champion     INTEGER DEFAULT 0,
    promoted_at     TEXT,
    retired_at      TEXT,
    created_at      TEXT NOT NULL,
    FOREIGN KEY (job_id) REFERENCES jobs(id)
);
CREATE INDEX IF NOT EXISTS idx_adapters_champion ON adapters(specialty, job_type, is_champion);

CREATE TABLE IF NOT EXISTS evaluations (
    id              TEXT PRIMARY KEY,
    job_id          TEXT NOT NULL,
    candidate_tag   TEXT NOT NULL,
    champion_tag    TEXT,
    suite           TEXT NOT NULL,
    total           INTEGER NOT NULL,
    candidate_wins  INTEGER NOT NULL,
    ties            INTEGER NOT NULL,
    champion_wins   INTEGER NOT NULL,
    win_rate        REAL NOT NULL,
    elo_delta       REAL NOT NULL,
    promoted        INTEGER NOT NULL,
    detail_json     TEXT,
    created_at      TEXT NOT NULL,
    FOREIGN KEY (job_id) REFERENCES jobs(id)
);
CREATE INDEX IF NOT EXISTS idx_eval_job ON evaluations(job_id, created_at);

CREATE TABLE IF NOT EXISTS training_runs (
    id              TEXT PRIMARY KEY,
    job_id          TEXT NOT NULL,
    phase           TEXT NOT NULL,
    metrics_json    TEXT,
    final_loss      REAL,
    final_eval_loss REAL,
    samples         INTEGER,
    steps           INTEGER,
    duration_sec    REAL,
    succeeded       INTEGER NOT NULL,
    error_detail    TEXT,
    created_at      TEXT NOT NULL,
    FOREIGN KEY (job_id) REFERENCES jobs(id)
);
"""


class SqliteConnection:
    """Owns the SQLite file + a thread-local RLock. Shared across repositories."""

    def __init__(self, db_path: Path):
        self._db_path = Path(db_path)
        self._db_path.parent.mkdir(parents=True, exist_ok=True)
        self._lock = threading.RLock()
        with self.connect() as conn:
            conn.executescript(_SCHEMA)
        log.info("SQLite ready", extra={"db_path": str(self._db_path)})

    @contextmanager
    def connect(self) -> Iterator[sqlite3.Connection]:
        with self._lock:
            conn = sqlite3.connect(
                self._db_path, isolation_level=None, timeout=10)
            conn.row_factory = sqlite3.Row
            try:
                conn.execute("PRAGMA journal_mode=WAL")
                conn.execute("PRAGMA foreign_keys=ON")
                yield conn
            finally:
                conn.close()


# ── Repositories ─────────────────────────────────────────────────────────────

class SqliteJobRepository:
    def __init__(self, db: SqliteConnection):
        self._db = db

    def create(self, job: Job) -> None:
        with self._db.connect() as c:
            c.execute(
                """INSERT INTO jobs (id, job_type, base_model, output_tag, specialty,
                                     status, created_at)
                   VALUES (?, ?, ?, ?, ?, ?, ?)""",
                (job.id, job.job_type.value, job.base_model, job.output_tag,
                 job.specialty, job.status.value, job.created_at or utc_now_iso()),
            )

    def update(self, job_id: str, **fields: Any) -> None:
        if not fields:
            return
        if "progress" in fields and not isinstance(fields["progress"], (str, type(None))):
            fields["progress"] = json.dumps(
                fields["progress"], ensure_ascii=False)
        cols = ", ".join(f"{k} = ?" for k in fields)
        with self._db.connect() as c:
            c.execute(f"UPDATE jobs SET {cols} WHERE id = ?",
                      (*fields.values(), job_id))

    def get(self, job_id: str) -> Job | None:
        with self._db.connect() as c:
            row = c.execute("SELECT * FROM jobs WHERE id = ?",
                            (job_id,)).fetchone()
            return job_from_row(dict(row)) if row else None

    def list(self, take: int = 50) -> list[Job]:
        with self._db.connect() as c:
            rows = c.execute(
                "SELECT * FROM jobs ORDER BY created_at DESC LIMIT ?", (take,)
            ).fetchall()
            return [job_from_row(dict(r)) for r in rows]

    def active(self) -> list[Job]:
        with self._db.connect() as c:
            rows = c.execute(
                """SELECT * FROM jobs
                   WHERE status IN ('pending','preparing','training','evaluating')"""
            ).fetchall()
            return [job_from_row(dict(r)) for r in rows]


class SqliteAdapterRepository:
    def __init__(self, db: SqliteConnection):
        self._db = db

    def add(self, adapter: Adapter) -> None:
        with self._db.connect() as c:
            c.execute(
                """INSERT OR REPLACE INTO adapters
                   (tag, job_id, job_type, specialty, base_model, parent_tag, path,
                    elo_rating, is_champion, created_at)
                   VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                (adapter.tag, adapter.job_id, adapter.job_type.value,
                 adapter.specialty, adapter.base_model, adapter.parent_tag,
                 adapter.path, adapter.elo_rating, int(adapter.is_champion),
                 adapter.created_at or utc_now_iso()),
            )

    def get(self, tag: str) -> Adapter | None:
        with self._db.connect() as c:
            row = c.execute(
                "SELECT * FROM adapters WHERE tag = ?", (tag,)).fetchone()
            return adapter_from_row(dict(row)) if row else None

    def list(self, specialty: str | None = None, take: int = 20) -> list[Adapter]:
        with self._db.connect() as c:
            if specialty:
                rows = c.execute(
                    "SELECT * FROM adapters WHERE specialty = ? "
                    "ORDER BY elo_rating DESC LIMIT ?", (specialty, take),
                ).fetchall()
            else:
                rows = c.execute(
                    "SELECT * FROM adapters ORDER BY elo_rating DESC LIMIT ?", (
                        take,)
                ).fetchall()
            return [adapter_from_row(dict(r)) for r in rows]

    def get_champion(self, specialty: str | None,
                     job_type: JobType) -> Adapter | None:
        with self._db.connect() as c:
            row = c.execute(
                """SELECT * FROM adapters
                   WHERE is_champion = 1 AND job_type = ?
                     AND (specialty IS ? OR specialty = ?)
                   ORDER BY promoted_at DESC LIMIT 1""",
                (job_type.value, specialty, specialty),
            ).fetchone()
            return adapter_from_row(dict(row)) if row else None

    def promote(self, tag: str, elo_delta: float) -> None:
        with self._db.connect() as c:
            row = c.execute(
                "SELECT * FROM adapters WHERE tag = ?", (tag,)).fetchone()
            if not row:
                raise ValueError(f"Adapter '{tag}' not found")
            c.execute(
                """UPDATE adapters
                   SET is_champion = 0, retired_at = ?
                   WHERE is_champion = 1 AND job_type = ?
                     AND (specialty IS ? OR specialty = ?)""",
                (utc_now_iso(), row["job_type"],
                 row["specialty"], row["specialty"]),
            )
            c.execute(
                """UPDATE adapters
                   SET is_champion = 1, promoted_at = ?,
                       elo_rating = elo_rating + ?
                   WHERE tag = ?""",
                (utc_now_iso(), elo_delta, tag),
            )
            log.info("Promoted adapter", extra={
                     "tag": tag, "elo_delta": elo_delta})


class SqliteEvaluationRepository:
    def __init__(self, db: SqliteConnection):
        self._db = db

    def record(self, *, eval_id: str, job_id: str, candidate_tag: str,
               champion_tag: str | None, suite: str,
               result: EvaluationResult) -> None:
        with self._db.connect() as c:
            c.execute(
                """INSERT INTO evaluations
                   (id, job_id, candidate_tag, champion_tag, suite, total,
                    candidate_wins, ties, champion_wins, win_rate, elo_delta,
                    promoted, detail_json, created_at)
                   VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                (eval_id, job_id, candidate_tag, champion_tag, suite,
                 result.total, result.candidate_wins, result.ties,
                 result.champion_wins, result.win_rate, result.elo_delta,
                 int(result.promote),
                 json.dumps(
                     {"sample": result.details[:20]}, ensure_ascii=False),
                 utc_now_iso()),
            )

    def recent(self, take: int = 20) -> list[dict]:
        with self._db.connect() as c:
            rows = c.execute(
                "SELECT * FROM evaluations ORDER BY created_at DESC LIMIT ?", (
                    take,)
            ).fetchall()
            return [dict(r) for r in rows]


class SqliteTrainingRunRepository:
    def __init__(self, db: SqliteConnection):
        self._db = db

    def record(self, *, run_id: str, job_id: str, phase: str,
               result, succeeded: bool, error_detail: str | None) -> None:
        with self._db.connect() as c:
            c.execute(
                """INSERT INTO training_runs
                   (id, job_id, phase, metrics_json, final_loss, final_eval_loss,
                    samples, steps, duration_sec, succeeded, error_detail, created_at)
                   VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                (run_id, job_id, phase,
                 json.dumps(
                     {"tail": result.metrics[-50:]}, ensure_ascii=False),
                 result.final_loss, result.final_eval_loss,
                 result.samples, result.steps, result.duration_sec,
                 int(succeeded), error_detail, utc_now_iso()),
            )
