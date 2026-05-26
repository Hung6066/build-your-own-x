"""Adapter wrapping the existing HuggingFace `trainer.run_training`.

The heavy ML code lives in the legacy `trainer.py` module at the package
root; this adapter just translates the port signature into a `TrainConfig`.
"""

from __future__ import annotations

import sys
from pathlib import Path
from typing import Callable

from ..domain import TrainingFailed, TrainingResult

# Ensure the legacy modules at scripts/finetune/ are importable
_THIS_DIR = Path(__file__).resolve().parent
_FINETUNE_DIR = _THIS_DIR.parent.parent
if str(_FINETUNE_DIR) not in sys.path:
    sys.path.insert(0, str(_FINETUNE_DIR))


class HuggingFaceTrainer:
    """ModelTrainer port impl. Calls into `trainer.run_training`."""

    def train_dpo(self, *, base_model: str, train_file: Path, val_file: Path,
                  output_dir: Path, resume_from: Path | None,
                  epochs: int, learning_rate: float, batch_size: int,
                  grad_accum: int, max_seq_length: int,
                  lora_rank: int, lora_alpha: int, lora_dropout: float,
                  load_in_4bit: bool, dpo_beta: float,
                  max_runtime_seconds: float, nan_check_every: int,
                  progress_cb: Callable[[int, dict], None] | None,
                  ) -> TrainingResult:
        return self._run(
            mode="dpo",
            base_model=base_model, train_file=train_file, val_file=val_file,
            output_dir=output_dir, resume_from=resume_from, epochs=epochs,
            learning_rate=learning_rate, batch_size=batch_size,
            grad_accum=grad_accum, max_seq_length=max_seq_length,
            lora_rank=lora_rank, lora_alpha=lora_alpha, lora_dropout=lora_dropout,
            load_in_4bit=load_in_4bit, dpo_beta=dpo_beta,
            max_runtime_seconds=max_runtime_seconds, nan_check_every=nan_check_every,
            progress_cb=progress_cb,
        )

    def train_orpo(self, *, base_model: str, train_file: Path, val_file: Path,
                   output_dir: Path, resume_from: Path | None,
                   epochs: int, learning_rate: float, batch_size: int,
                   grad_accum: int, max_seq_length: int,
                   lora_rank: int, lora_alpha: int, lora_dropout: float,
                   load_in_4bit: bool,
                   max_runtime_seconds: float, nan_check_every: int,
                   progress_cb: Callable[[int, dict], None] | None,
                   ) -> TrainingResult:
        """ORPO: single-pass SFT + preference alignment — no reference model."""
        return self._run(
            mode="orpo",
            base_model=base_model, train_file=train_file, val_file=val_file,
            output_dir=output_dir, resume_from=resume_from, epochs=epochs,
            learning_rate=learning_rate, batch_size=batch_size,
            grad_accum=grad_accum, max_seq_length=max_seq_length,
            lora_rank=lora_rank, lora_alpha=lora_alpha, lora_dropout=lora_dropout,
            load_in_4bit=load_in_4bit, dpo_beta=0.1,
            max_runtime_seconds=max_runtime_seconds, nan_check_every=nan_check_every,
            progress_cb=progress_cb,
        )

    def _run(self, *, mode: str, base_model: str, train_file: Path, val_file: Path,
             output_dir: Path, resume_from: Path | None,
             epochs: int, learning_rate: float, batch_size: int,
             grad_accum: int, max_seq_length: int,
             lora_rank: int, lora_alpha: int, lora_dropout: float,
             load_in_4bit: bool, dpo_beta: float,
             max_runtime_seconds: float, nan_check_every: int,
             progress_cb: Callable[[int, dict], None] | None,
             ) -> TrainingResult:
        try:
            # type: ignore[import-not-found]
            from trainer import TrainConfig, run_training
        except ImportError as exc:
            raise TrainingFailed(f"trainer module unavailable: {exc}") from exc

        cfg = TrainConfig(
            mode=mode,
            base_model=base_model,
            train_file=train_file,
            val_file=val_file,
            output_dir=output_dir,
            epochs=epochs,
            learning_rate=learning_rate,
            batch_size=batch_size,
            grad_accum=grad_accum,
            max_seq_length=max_seq_length,
            lora_rank=lora_rank,
            lora_alpha=lora_alpha,
            lora_dropout=lora_dropout,
            load_in_4bit=load_in_4bit,
            dpo_beta=dpo_beta,
            resume_from=resume_from,
            max_runtime_seconds=max_runtime_seconds,
            nan_check_every=nan_check_every,
            progress_cb=progress_cb,
        )

        try:
            result = run_training(cfg)
        except Exception as exc:
            raise TrainingFailed(f"training run failed: {exc}") from exc

        return TrainingResult(
            output_dir=str(result.output_dir if hasattr(result, "output_dir")
                           else output_dir),
            final_loss=getattr(result, "final_loss", None),
            final_eval_loss=getattr(result, "final_eval_loss", None),
            steps=getattr(result, "steps", 0),
            samples=getattr(result, "samples", 0),
            duration_sec=getattr(result, "duration_sec", 0.0),
            metrics=list(getattr(result, "metrics", []) or []),
        )
