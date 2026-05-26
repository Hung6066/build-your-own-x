"""
Production trainer: in-process LoRA / QLoRA fine-tuning.

Designed to be imported by the API service (no subprocess overhead).
Supports SFT and DPO, optional resume-from-champion (continual learning),
NaN guard, time-budget guard, gradient checkpointing, deterministic seeding.
"""

from __future__ import annotations

import json
import os
import random
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

import numpy as np

from app.infra.logging import get_logger

log = get_logger(__name__)


@dataclass
class TrainConfig:
    mode: str                     # "sft" | "dpo" | "orpo"
    base_model: str
    train_file: Path
    output_dir: Path
    val_file: Path | None = None

    epochs: int = 3
    learning_rate: float = 2e-4
    batch_size: int = 2
    grad_accum: int = 8
    max_seq_length: int = 2048
    max_grad_norm: float = 0.3    # QLoRA stability (default 1.0 is too loose)

    # ── LoRA architecture ────────────────────────────────────────────────────
    lora_rank: int = 64
    lora_alpha: int = 128         # 2 × rank — effective scaling factor 2.0
    lora_dropout: float = 0.05
    load_in_4bit: bool = True
    # rank-stabilized LoRA: alpha/sqrt(r) instead of alpha/r
    use_rslora: bool = True
    # weight-decomposed LoRA (more expressive, ~20% slower)
    use_dora: bool = False

    # ── DPO ─────────────────────────────────────────────────────────────────
    dpo_beta: float = 0.1
    dpo_loss_type: str = "ipo"    # IPO: more robust to noisy/ambiguous preference labels
    # DPO needs a much lower LR than SFT (2e-4 will overfit)
    dpo_learning_rate: float = 5e-6

    # ── SFT extras ──────────────────────────────────────────────────────────
    # embedding noise improves generalisation
    neftune_noise_alpha: float | None = 5.0
    pack_sequences: bool = True              # pack multiple short seqs per batch step

    # ── Continual learning: resume from an existing adapter ──────────────────
    resume_from: Path | None = None

    seed: int = 42
    max_runtime_seconds: float = 12 * 3600
    nan_check_every: int = 50

    # Optional callback (step, logs) for live-progress reporting
    progress_cb: Callable[[int, dict], None] | None = None

    def as_log_dict(self) -> dict:
        d = self.__dict__.copy()
        d.pop("progress_cb", None)
        # Path → str for JSON serialization
        for k, v in list(d.items()):
            if isinstance(v, Path):
                d[k] = str(v)
        return d


@dataclass
class TrainResult:
    output_dir: Path
    final_loss: float | None
    final_eval_loss: float | None
    steps: int
    samples: int
    duration_sec: float
    metrics: list[dict]


def _seed_everything(seed: int) -> None:
    random.seed(seed)
    np.random.seed(seed)
    try:
        import torch
        torch.manual_seed(seed)
        if torch.cuda.is_available():
            torch.cuda.manual_seed_all(seed)
    except ImportError:
        pass
    os.environ["PYTHONHASHSEED"] = str(seed)


def _load_model_and_tokenizer(cfg: TrainConfig):
    import torch
    from transformers import AutoModelForCausalLM, AutoTokenizer, BitsAndBytesConfig

    bnb_config = None
    if cfg.load_in_4bit:
        bnb_config = BitsAndBytesConfig(
            load_in_4bit=True,
            bnb_4bit_quant_type="nf4",
            bnb_4bit_compute_dtype=torch.bfloat16,
            bnb_4bit_use_double_quant=True,
        )

    tokenizer = AutoTokenizer.from_pretrained(
        cfg.base_model, trust_remote_code=True)
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token

    model = AutoModelForCausalLM.from_pretrained(
        cfg.base_model,
        quantization_config=bnb_config,
        device_map="auto",
        torch_dtype=torch.bfloat16,
        trust_remote_code=True,
    )
    model.config.use_cache = False
    return model, tokenizer


def _apply_lora(model, cfg: TrainConfig, resume_from: Path | None):
    from peft import LoraConfig, PeftModel, get_peft_model, prepare_model_for_kbit_training

    if cfg.load_in_4bit:
        model = prepare_model_for_kbit_training(model)

    if resume_from is not None and resume_from.exists():
        log.info("Resuming from champion adapter",
                 extra={"path": str(resume_from)})
        model = PeftModel.from_pretrained(
            model, str(resume_from), is_trainable=True)
        return model

    lora = LoraConfig(
        r=cfg.lora_rank,
        lora_alpha=cfg.lora_alpha,
        lora_dropout=cfg.lora_dropout,
        bias="none",
        task_type="CAUSAL_LM",
        target_modules=["q_proj", "k_proj", "v_proj", "o_proj",
                        "gate_proj", "up_proj", "down_proj"],
        use_rslora=cfg.use_rslora,   # alpha/sqrt(r) — stable at high ranks
        use_dora=cfg.use_dora,       # weight-decomposed LoRA
    )
    return get_peft_model(model, lora)


def _train_sft(cfg: TrainConfig) -> TrainResult:
    from datasets import load_dataset
    from trl import SFTConfig, SFTTrainer

    from callbacks import MetricSink, NaNGuardCallback, TimeBudgetCallback

    log.info("Loading SFT data", extra={"file": str(cfg.train_file)})
    ds_train = load_dataset("json", data_files=str(
        cfg.train_file), split="train")
    ds_val = (load_dataset("json", data_files=str(cfg.val_file), split="train")
              if cfg.val_file and cfg.val_file.exists() else None)

    model, tokenizer = _load_model_and_tokenizer(cfg)
    model = _apply_lora(model, cfg, cfg.resume_from)

    args = SFTConfig(
        output_dir=str(cfg.output_dir),
        num_train_epochs=cfg.epochs,
        per_device_train_batch_size=cfg.batch_size,
        gradient_accumulation_steps=cfg.grad_accum,
        gradient_checkpointing=True,
        learning_rate=cfg.learning_rate,
        lr_scheduler_type="cosine",
        warmup_ratio=0.03,
        max_length=cfg.max_seq_length,
        logging_steps=10,
        save_strategy="epoch",
        save_total_limit=2,
        eval_strategy="epoch" if ds_val else "no",
        bf16=True,
        report_to=[],
        seed=cfg.seed,
        max_grad_norm=cfg.max_grad_norm,
        neftune_noise_alpha=cfg.neftune_noise_alpha,
        packing=cfg.pack_sequences,
    )

    sink = MetricSink()
    trainer = SFTTrainer(
        model=model,
        args=args,
        train_dataset=ds_train,
        eval_dataset=ds_val,
        processing_class=tokenizer,
        callbacks=[
            NaNGuardCallback(check_every=cfg.nan_check_every),
            TimeBudgetCallback(cfg.max_runtime_seconds),
            sink,
        ],
    )
    if cfg.progress_cb:
        from transformers import TrainerCallback

        cb = cfg.progress_cb

        class _ProxyCb(TrainerCallback):
            def on_log(self, args, state, control, logs=None, **kw):
                if logs:
                    try:
                        cb(state.global_step, logs)
                    except Exception:  # noqa: BLE001
                        pass
                return control
        trainer.add_callback(_ProxyCb())

    t0 = time.time()
    result = trainer.train()
    duration = time.time() - t0

    trainer.save_model(str(cfg.output_dir))
    tokenizer.save_pretrained(str(cfg.output_dir))

    final_loss = result.metrics.get("train_loss")
    final_eval = None
    if ds_val:
        ev = trainer.evaluate()
        final_eval = ev.get("eval_loss")

    return TrainResult(
        output_dir=cfg.output_dir,
        final_loss=final_loss,
        final_eval_loss=final_eval,
        steps=int(result.global_step or 0),
        samples=len(ds_train),
        duration_sec=duration,
        metrics=sink.records,
    )


def _train_dpo(cfg: TrainConfig) -> TrainResult:
    from datasets import load_dataset
    from trl import DPOConfig, DPOTrainer

    from callbacks import MetricSink, NaNGuardCallback, TimeBudgetCallback

    log.info("Loading DPO data", extra={"file": str(cfg.train_file)})
    ds_train = load_dataset("json", data_files=str(
        cfg.train_file), split="train")
    ds_val = (load_dataset("json", data_files=str(cfg.val_file), split="train")
              if cfg.val_file and cfg.val_file.exists() else None)

    model, tokenizer = _load_model_and_tokenizer(cfg)
    model = _apply_lora(model, cfg, cfg.resume_from)

    args = DPOConfig(
        output_dir=str(cfg.output_dir),
        num_train_epochs=cfg.epochs,
        per_device_train_batch_size=cfg.batch_size,
        gradient_accumulation_steps=cfg.grad_accum,
        gradient_checkpointing=True,
        learning_rate=cfg.dpo_learning_rate,  # must be much lower than SFT
        lr_scheduler_type="cosine",
        warmup_ratio=0.03,
        max_length=cfg.max_seq_length,
        max_prompt_length=cfg.max_seq_length // 2,
        beta=cfg.dpo_beta,
        loss_type=cfg.dpo_loss_type,
        logging_steps=10,
        save_strategy="epoch",
        save_total_limit=2,
        eval_strategy="epoch" if ds_val else "no",
        bf16=True,
        report_to=[],
        seed=cfg.seed,
        max_grad_norm=cfg.max_grad_norm,
    )

    sink = MetricSink()
    trainer = DPOTrainer(
        model=model,
        ref_model=None,
        args=args,
        train_dataset=ds_train,
        eval_dataset=ds_val,
        processing_class=tokenizer,
        callbacks=[
            NaNGuardCallback(check_every=cfg.nan_check_every),
            TimeBudgetCallback(cfg.max_runtime_seconds),
            sink,
        ],
    )

    t0 = time.time()
    result = trainer.train()
    duration = time.time() - t0

    trainer.save_model(str(cfg.output_dir))
    tokenizer.save_pretrained(str(cfg.output_dir))

    final_loss = result.metrics.get("train_loss")
    final_eval = None
    if ds_val:
        ev = trainer.evaluate()
        final_eval = ev.get("eval_loss")

    return TrainResult(
        output_dir=cfg.output_dir,
        final_loss=final_loss,
        final_eval_loss=final_eval,
        steps=int(result.global_step or 0),
        samples=len(ds_train),
        duration_sec=duration,
        metrics=sink.records,
    )


def _train_orpo(cfg: TrainConfig) -> TrainResult:
    """ORPO — Odds-Ratio Preference Optimization.

    Merges SFT and preference alignment into a single forward pass:
    L_ORPO = L_NLL(chosen) + λ * L_OR(chosen, rejected)

    Advantages over the SFT → DPO two-stage pipeline:
    - No separate reference model (saves ~50 % VRAM vs DPO with ref_model)
    - Trains SFT alignment in one pass
    - Same data format as DPO (prompt / chosen / rejected)
    """
    try:
        from trl import ORPOConfig, ORPOTrainer
    except ImportError:
        from trl.experimental.orpo import ORPOConfig, ORPOTrainer

    from datasets import load_dataset

    from callbacks import MetricSink, NaNGuardCallback, TimeBudgetCallback

    log.info("Loading ORPO data", extra={"file": str(cfg.train_file)})
    ds_train = load_dataset("json", data_files=str(
        cfg.train_file), split="train")
    ds_val = (load_dataset("json", data_files=str(cfg.val_file), split="train")
              if cfg.val_file and cfg.val_file.exists() else None)

    model, tokenizer = _load_model_and_tokenizer(cfg)
    model = _apply_lora(model, cfg, cfg.resume_from)

    args = ORPOConfig(
        output_dir=str(cfg.output_dir),
        num_train_epochs=cfg.epochs,
        per_device_train_batch_size=cfg.batch_size,
        gradient_accumulation_steps=cfg.grad_accum,
        gradient_checkpointing=True,
        learning_rate=cfg.learning_rate,   # ORPO includes SFT, so normal LR is fine
        lr_scheduler_type="cosine",
        warmup_ratio=0.03,
        max_length=cfg.max_seq_length,
        beta=0.1,                           # λ — weight of the odds-ratio term
        logging_steps=10,
        save_strategy="epoch",
        save_total_limit=2,
        eval_strategy="epoch" if ds_val else "no",
        bf16=True,
        report_to=[],
        seed=cfg.seed,
        max_grad_norm=cfg.max_grad_norm,
    )

    sink = MetricSink()
    trainer = ORPOTrainer(
        model=model,
        args=args,
        train_dataset=ds_train,
        eval_dataset=ds_val,
        processing_class=tokenizer,
        callbacks=[
            NaNGuardCallback(check_every=cfg.nan_check_every),
            TimeBudgetCallback(cfg.max_runtime_seconds),
            sink,
        ],
    )

    if cfg.progress_cb:
        from transformers import TrainerCallback

        cb = cfg.progress_cb

        class _ProxyCb(TrainerCallback):
            def on_log(self, args, state, control, logs=None, **kw):
                if logs:
                    try:
                        cb(state.global_step, logs)
                    except Exception:  # noqa: BLE001
                        pass
                return control
        trainer.add_callback(_ProxyCb())

    t0 = time.time()
    result = trainer.train()
    duration = time.time() - t0

    trainer.save_model(str(cfg.output_dir))
    tokenizer.save_pretrained(str(cfg.output_dir))

    final_loss = result.metrics.get("train_loss")
    final_eval = None
    if ds_val:
        ev = trainer.evaluate()
        final_eval = ev.get("eval_loss")

    return TrainResult(
        output_dir=cfg.output_dir,
        final_loss=final_loss,
        final_eval_loss=final_eval,
        steps=int(result.global_step or 0),
        samples=len(ds_train),
        duration_sec=duration,
        metrics=sink.records,
    )


def run_training(cfg: TrainConfig) -> TrainResult:
    """Entry point used by the service. Raises on unrecoverable errors."""
    cfg.output_dir.mkdir(parents=True, exist_ok=True)
    _seed_everything(cfg.seed)
    log.info("Starting training", extra={"config": cfg.as_log_dict()})

    # Snapshot config for reproducibility
    (cfg.output_dir / "train_config.json").write_text(
        json.dumps(cfg.as_log_dict(), ensure_ascii=False, indent=2), encoding="utf-8"
    )

    if cfg.mode == "sft":
        return _train_sft(cfg)
    if cfg.mode == "dpo":
        return _train_dpo(cfg)
    if cfg.mode == "orpo":
        return _train_orpo(cfg)
    raise ValueError(f"Unsupported mode: {cfg.mode}")
