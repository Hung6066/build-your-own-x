"""Settings via pydantic-settings. Validated at startup."""

from __future__ import annotations

from functools import lru_cache
from pathlib import Path

from pydantic import Field, field_validator
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_prefix="HOPE_FT_",
        env_file=".env",
        env_file_encoding="utf-8",
        extra="ignore",
    )

    # Server
    host: str = "0.0.0.0"
    port: int = 8765
    api_key: str = Field(
        default="", description="Required X-Api-Key header value")
    log_level: str = "INFO"
    log_json: bool = True

    # Hope.Agent API
    agent_api_url: str = "http://localhost:5000"
    agent_api_token: str = ""

    # Storage
    workdir: Path = Path("/var/lib/hope-finetune")
    db_path: Path = Path("/var/lib/hope-finetune/registry.db")
    data_dir: Path = Path("/var/lib/hope-finetune/data")
    adapters_dir: Path = Path("/var/lib/hope-finetune/adapters")
    logs_dir: Path = Path("/var/lib/hope-finetune/logs")

    # Training
    base_model: str = "Qwen/Qwen3-8B"
    load_in_4bit: bool = True
    max_seq_length: int = 2048
    lora_rank: int = 64
    lora_alpha: int = 16
    lora_dropout: float = 0.05

    sft_epochs: int = 3
    sft_lr: float = 2e-4
    sft_batch: int = 2
    sft_grad_accum: int = 8

    dpo_epochs: int = 1
    dpo_lr: float = 5e-6
    dpo_batch: int = 1
    dpo_grad_accum: int = 16
    dpo_beta: float = 0.1

    # Evaluation
    eval_min_samples: int = 50
    eval_win_rate_promote: float = 0.55
    # set to 0.50 to require statistical significance
    eval_wilson_min: float = 0.0
    eval_judge_model: str = "qwen2.5:7b-instruct"
    eval_judge_url: str = "http://localhost:11434"

    # Cloud-vs-local gate: compare local model against cloud baseline.
    # Set eval_cloud_baseline_model to a non-empty value to enable.
    # e.g. "gpt-4o-mini" or "claude-3-haiku"
    eval_cloud_baseline_model: str = ""
    # stricter than local-vs-local (0.55)
    eval_cloud_win_rate_promote: float = 0.58
    eval_cloud_wilson_min: float = 0.50        # require lower CI bound > 0.50
    eval_cloud_min_samples: int = 100          # need more evidence vs cloud

    # Scheduler
    auto_train_enabled: bool = False
    auto_train_cron: str = "0 3 * * *"
    auto_train_min_new_pairs: int = 200

    # Ollama
    ollama_url: str = "http://localhost:11434"
    ollama_auto_register: bool = True

    # Safety
    max_concurrent_jobs: int = 1
    max_runtime_hours: float = 12.0
    nan_check_every_n_steps: int = 50

    # Resilience
    http_retry_attempts: int = 3
    http_retry_backoff_seconds: float = 1.5
    shutdown_grace_seconds: float = 30.0

    @field_validator("eval_win_rate_promote")
    @classmethod
    def _validate_threshold(cls, v: float) -> float:
        if not (0.5 <= v <= 1.0):
            raise ValueError("eval_win_rate_promote must be in [0.5, 1.0]")
        return v

    @field_validator("max_concurrent_jobs")
    @classmethod
    def _validate_concurrency(cls, v: int) -> int:
        if v < 1:
            raise ValueError("max_concurrent_jobs must be >= 1")
        return v

    def ensure_dirs(self) -> None:
        for p in (self.workdir, self.data_dir, self.adapters_dir, self.logs_dir):
            Path(p).mkdir(parents=True, exist_ok=True)


@lru_cache(maxsize=1)
def get_settings() -> Settings:
    s = Settings()
    s.ensure_dirs()
    return s
