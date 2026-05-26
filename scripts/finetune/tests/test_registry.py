from pathlib import Path

from app.domain import Adapter, Job, JobStatus, JobType
from app.infra.persistence import (SqliteAdapterRepository, SqliteConnection,
                                   SqliteEvaluationRepository,
                                   SqliteJobRepository)
from app.domain import EvaluationResult


def _db(tmp_path: Path, name: str):
    return SqliteConnection(tmp_path / name)


def test_create_and_promote(tmp_path: Path):
    db = _db(tmp_path, "test.db")
    jobs = SqliteJobRepository(db)
    adapters = SqliteAdapterRepository(db)

    j = Job(id="j1", job_type=JobType.DPO, base_model="Qwen/Qwen3-8B",
            specialty="cardio", status=JobStatus.PENDING,
            created_at="2025-01-01T00:00:00+00:00")
    jobs.create(j)

    got = jobs.get("j1")
    assert got and got.status == JobStatus.PENDING

    jobs.update("j1", status="training", record_count=42)
    got = jobs.get("j1")
    assert got.status == JobStatus.TRAINING and got.record_count == 42

    adapters.add(Adapter(tag="adapter-v1", job_id="j1", job_type=JobType.DPO,
                         specialty="cardio", base_model="Qwen/Qwen3-8B",
                         path="/tmp/adapter-v1"))
    adapters.promote("adapter-v1", elo_delta=25.0)
    champ = adapters.get_champion("cardio", JobType.DPO)
    assert champ and champ.tag == "adapter-v1"
    assert champ.is_champion and champ.elo_rating > 1000

    # Promote a second adapter and verify previous is retired
    adapters.add(Adapter(tag="adapter-v2", job_id="j1", job_type=JobType.DPO,
                         specialty="cardio", base_model="Qwen/Qwen3-8B",
                         parent_tag="adapter-v1", path="/tmp/adapter-v2"))
    adapters.promote("adapter-v2", elo_delta=10.0)
    champ = adapters.get_champion("cardio", JobType.DPO)
    assert champ.tag == "adapter-v2"

    all_adapters = adapters.list("cardio")
    by_tag = {a.tag: a for a in all_adapters}
    assert not by_tag["adapter-v1"].is_champion
    assert by_tag["adapter-v2"].is_champion


def test_evaluations(tmp_path: Path):
    db = _db(tmp_path, "e.db")
    jobs = SqliteJobRepository(db)
    evals = SqliteEvaluationRepository(db)

    jobs.create(Job(id="j2", job_type=JobType.DPO, base_model="x",
                    status=JobStatus.PENDING,
                    created_at="2025-01-01T00:00:00+00:00"))
    evals.record(
        eval_id="ev1", job_id="j2", candidate_tag="c",
        champion_tag=None, suite="golden",
        result=EvaluationResult(
            total=100, candidate_wins=60, ties=10, champion_wins=30,
            win_rate=0.65, elo_delta=42.0, promote=True,
        ),
    )
    rows = evals.recent()
    assert len(rows) == 1 and rows[0]["win_rate"] == 0.65
