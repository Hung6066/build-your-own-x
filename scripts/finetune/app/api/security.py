"""API security dependencies."""

from __future__ import annotations

from fastapi import Depends, Header, HTTPException

from ..infra.config import Settings, get_settings


def require_api_key(x_api_key: str | None = Header(default=None),
                    settings: Settings = Depends(get_settings)) -> None:
    if not settings.api_key:
        return
    if x_api_key != settings.api_key:
        raise HTTPException(status_code=401, detail="Invalid API key")
