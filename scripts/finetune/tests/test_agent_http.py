"""Tests for HTTP infra adapters with mocked transport."""

from pathlib import Path

import httpx
import pytest

from app.infra.agent_http import AgentHttpClient, hash_jsonl


@pytest.mark.asyncio
async def test_agent_client_retries_on_transient_error(tmp_path: Path,
                                                       monkeypatch):
    """Transport error → 2 retries → success."""
    attempts = {"n": 0}

    def handler(request: httpx.Request) -> httpx.Response:
        attempts["n"] += 1
        if attempts["n"] < 3:
            raise httpx.ConnectError("boom", request=request)
        body = b'{"id":1}\n{"id":2}\n'
        return httpx.Response(200, content=body)

    transport = httpx.MockTransport(handler)

    # Patch AsyncClient to use our mock transport
    real_async_client = httpx.AsyncClient
    monkeypatch.setattr(
        httpx, "AsyncClient",
        lambda *a, **kw: real_async_client(transport=transport, **{
            k: v for k, v in kw.items() if k != "transport"
        }),
    )

    client = AgentHttpClient("http://fake", token="t",
                             retry_attempts=3, retry_backoff=0.01)
    out = tmp_path / "out.jsonl"
    n = await client.download_dpo(since=None, until=None, specialty=None,
                                  max_records=10, output=out)
    assert n == 2
    assert attempts["n"] == 3


@pytest.mark.asyncio
async def test_agent_client_preference_count_graceful(monkeypatch):
    """All retries fail → returns 0 instead of raising."""

    def handler(request):
        raise httpx.ConnectError("nope", request=request)

    transport = httpx.MockTransport(handler)
    real_async_client = httpx.AsyncClient
    monkeypatch.setattr(
        httpx, "AsyncClient",
        lambda *a, **kw: real_async_client(transport=transport, **{
            k: v for k, v in kw.items() if k != "transport"
        }),
    )

    client = AgentHttpClient("http://fake", token="",
                             retry_attempts=2, retry_backoff=0.01)
    count = await client.preference_count(since=None)
    assert count == 0


def test_hash_jsonl_stable(tmp_path: Path):
    p = tmp_path / "x.jsonl"
    p.write_text('{"a":1}\n{"a":2}\n', encoding="utf-8")
    h1 = hash_jsonl(p)
    p.write_text('{"a":2}\n{"a":1}\n', encoding="utf-8")
    h2 = hash_jsonl(p)
    assert h1 == h2 and len(h1) == 64
