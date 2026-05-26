"""
NaN/Inf watcher and early-stop callbacks for HuggingFace Trainer.
"""

from __future__ import annotations

import math

import torch
from transformers import TrainerCallback, TrainerControl, TrainerState, TrainingArguments

from app.infra.logging import get_logger

log = get_logger(__name__)


class NaNGuardCallback(TrainerCallback):
    """Aborts training if loss becomes NaN or Inf for several consecutive steps."""

    def __init__(self, max_bad_steps: int = 3, check_every: int = 50):
        self._max_bad = max_bad_steps
        self._every = check_every
        self._bad = 0

    def on_log(self, args: TrainingArguments, state: TrainerState,
               control: TrainerControl, logs=None, **kwargs):
        if not logs or state.global_step % self._every:
            return control
        loss = logs.get("loss")
        if loss is None:
            return control
        if not math.isfinite(loss):
            self._bad += 1
            log.warning("Non-finite loss detected",
                        extra={"step": state.global_step, "loss": loss,
                               "bad_count": self._bad})
            if self._bad >= self._max_bad:
                log.error("Stopping run due to repeated non-finite loss")
                control.should_training_stop = True
        else:
            self._bad = 0
        return control


class TimeBudgetCallback(TrainerCallback):
    """Stops training when wall-clock budget elapses."""

    def __init__(self, max_seconds: float):
        self._budget = max_seconds
        self._start: float | None = None

    def on_train_begin(self, args, state, control, **kwargs):
        import time
        self._start = time.time()
        return control

    def on_step_end(self, args, state, control, **kwargs):
        import time
        if self._start and (time.time() - self._start) > self._budget:
            log.warning("Time budget exceeded — stopping",
                        extra={"budget_sec": self._budget})
            control.should_training_stop = True
        return control


class MetricSink(TrainerCallback):
    """Collects per-step logs in memory for the registry."""

    def __init__(self):
        self.records: list[dict] = []

    def on_log(self, args, state, control, logs=None, **kwargs):
        if logs:
            self.records.append({"step": state.global_step, **logs})
        return control
