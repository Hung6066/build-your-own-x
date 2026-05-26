from pathlib import Path

from app.infra.agent_http import config_hash, hash_jsonl


def test_hash_stable(tmp_path: Path):
    p = tmp_path / "a.jsonl"
    p.write_text('{"x":1}\n{"x":2}\n', encoding="utf-8")
    h1 = hash_jsonl(p)
    # Same content reordered → same hash
    p.write_text('{"x":2}\n{"x":1}\n', encoding="utf-8")
    h2 = hash_jsonl(p)
    assert h1 == h2 and len(h1) == 64

    p.write_text('{"x":3}\n', encoding="utf-8")
    assert hash_jsonl(p) != h1


def test_config_hash():
    a = config_hash({"a": 1, "b": 2})
    b = config_hash({"b": 2, "a": 1})
    assert a == b
    assert config_hash({"a": 1, "b": 3}) != a
