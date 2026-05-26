"""Tests for the new Clean Architecture layout."""

from pathlib import Path

from app.domain import (Adapter, EvaluationResult, Job, JobStatus, JobType,
                        InvalidJobStateTransition, decide_promotion)
from app.infra.persistence import (SqliteAdapterRepository, SqliteConnection,
                                   SqliteEvaluationRepository,
                                   SqliteJobRepository)


def test_job_status_terminal():
    assert JobStatus.PROMOTED.is_terminal
    assert JobStatus.FAILED.is_terminal
    assert JobStatus.PENDING.is_active
    assert not JobStatus.PROMOTED.is_active


def test_job_transition_blocked_from_terminal():
    j = Job(id="j", job_type=JobType.DPO, base_model="x",
            status=JobStatus.COMPLETED)
    try:
        j.transition(JobStatus.TRAINING)
    except InvalidJobStateTransition:
        return
    raise AssertionError("expected InvalidJobStateTransition")


def test_promotion_policy_below_min_samples():
    er = EvaluationResult(total=10, candidate_wins=8, ties=2, champion_wins=0,
                          win_rate=1.0, elo_delta=10.0, promote=True)
    d = decide_promotion(er, threshold=0.55, min_samples=50)
    assert not d.promote and "insufficient_samples" in d.reason


def test_promotion_policy_below_threshold():
    er = EvaluationResult(total=100, candidate_wins=40, ties=10, champion_wins=50,
                          win_rate=0.44, elo_delta=-5.0, promote=False)
    d = decide_promotion(er, threshold=0.55, min_samples=50)
    assert not d.promote and "win_rate" in d.reason


def test_promotion_policy_promote():
    er = EvaluationResult(total=100, candidate_wins=60, ties=10, champion_wins=30,
                          win_rate=0.667, elo_delta=20.0, promote=True)
    d = decide_promotion(er, threshold=0.55, min_samples=50)
    assert d.promote
    assert d.wilson_lower > 0


def test_promotion_policy_wilson_ci_too_wide():
    """10/15 wins = 0.667 win-rate but sample too small → CI too wide."""
    er = EvaluationResult(total=15, candidate_wins=10, ties=2, champion_wins=3,
                          win_rate=0.769, elo_delta=5.0, promote=True)
    d = decide_promotion(er, threshold=0.55, min_samples=10, wilson_min=0.50)
    # Wilson lower bound for 10/13 ≈ 0.46 < 0.50 → should not promote
    assert not d.promote
    assert "wilson_ci" in d.reason


def test_wilson_lower_bound_math():
    from app.domain import wilson_lower_bound
    # 100% wins, large n → lower bound close to 1
    assert wilson_lower_bound(100, 100) > 0.95
    # 50% wins, large n → lower bound close to 0.40
    lb = wilson_lower_bound(50, 100)
    assert 0.39 < lb < 0.50
    # Zero wins
    assert wilson_lower_bound(0, 100) == 0.0


def test_sqlite_repos_roundtrip(tmp_path: Path):
    db = SqliteConnection(tmp_path / "r.db")
    jobs = SqliteJobRepository(db)
    adapters = SqliteAdapterRepository(db)

    j = Job(id="j1", job_type=JobType.DPO, base_model="Qwen/Qwen3-8B",
            specialty="cardio", status=JobStatus.PENDING,
            created_at="2025-01-01T00:00:00+00:00")
    jobs.create(j)

    got = jobs.get("j1")
    assert got is not None and got.id == "j1"
    assert got.job_type == JobType.DPO
    assert got.status == JobStatus.PENDING

    jobs.update("j1", status=JobStatus.TRAINING.value, record_count=99)
    got = jobs.get("j1")
    assert got.status == JobStatus.TRAINING and got.record_count == 99

    a = Adapter(tag="a1", job_id="j1", job_type=JobType.DPO,
                base_model="Qwen/Qwen3-8B", path="/tmp/a1",
                specialty="cardio")
    adapters.add(a)
    adapters.promote("a1", elo_delta=25.0)

    champ = adapters.get_champion("cardio", JobType.DPO)
    assert champ is not None and champ.tag == "a1"
    assert champ.is_champion and champ.elo_rating > 1000


def test_sqlite_active_jobs(tmp_path: Path):
    db = SqliteConnection(tmp_path / "a.db")
    jobs = SqliteJobRepository(db)
    jobs.create(Job(id="a", job_type=JobType.DPO, base_model="x",
                    status=JobStatus.TRAINING, created_at="t"))
    jobs.create(Job(id="b", job_type=JobType.DPO, base_model="x",
                    status=JobStatus.COMPLETED, created_at="t"))
    active = jobs.active()
    ids = {j.id for j in active}
    assert ids == {"a"}
