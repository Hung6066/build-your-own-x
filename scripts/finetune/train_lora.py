"""
train_lora.py — LoRA / QLoRA fine-tuning for Hope.Agent clinical model.

Supports both SFT (supervised fine-tuning) and DPO (direct preference optimisation) modes.

Usage — SFT:
    python train_lora.py \\
        --mode sft \\
        --model Qwen/Qwen3-8B \\
        --train data/sft_train.jsonl \\
        --val   data/sft_val.jsonl \\
        --output ./adapters/hope-sft-v1 \\
        --epochs 3 --batch 2 --grad-accum 8 --max-len 2048

Usage — DPO:
    python train_lora.py \\
        --mode dpo \\
        --model Qwen/Qwen3-8B \\
        --train data/dpo_train.jsonl \\
        --val   data/dpo_val.jsonl \\
        --output ./adapters/hope-dpo-v1 \\
        --epochs 1 --batch 1 --grad-accum 16 --max-len 2048 \\
        --dpo-beta 0.1

Requirements: GPU with ≥24GB VRAM for 8B (use --load-in-4bit for 16GB GPUs).
"""

import argparse
import json
import os
from pathlib import Path

import torch
from datasets import Dataset
from peft import LoraConfig, TaskType, get_peft_model, prepare_model_for_kbit_training
from transformers import (
    AutoModelForCausalLM,
    AutoTokenizer,
    BitsAndBytesConfig,
    TrainingArguments,
)
from trl import DPOConfig, DPOTrainer, SFTConfig, SFTTrainer


# ──────────────────────────────────────────────────────────────────────────────
# Data loading helpers
# ──────────────────────────────────────────────────────────────────────────────

def load_jsonl(path: str) -> list[dict]:
    with open(path, encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def make_sft_dataset(path: str) -> Dataset:
    """Load SFT JSONL (messages format) as a HuggingFace Dataset."""
    rows = load_jsonl(path)
    return Dataset.from_list(rows)


def make_dpo_dataset(path: str) -> Dataset:
    """Load DPO JSONL (prompt/chosen/rejected chat format) as a HuggingFace Dataset."""
    rows = load_jsonl(path)
    return Dataset.from_list(rows)


# ──────────────────────────────────────────────────────────────────────────────
# Model / tokenizer loading
# ──────────────────────────────────────────────────────────────────────────────

def load_model_and_tokenizer(model_name: str, load_in_4bit: bool):
    tokenizer = AutoTokenizer.from_pretrained(
        model_name, trust_remote_code=True)
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token

    bnb_cfg = None
    if load_in_4bit:
        bnb_cfg = BitsAndBytesConfig(
            load_in_4bit=True,
            bnb_4bit_quant_type="nf4",
            bnb_4bit_compute_dtype=torch.bfloat16,
            bnb_4bit_use_double_quant=True,
        )

    model = AutoModelForCausalLM.from_pretrained(
        model_name,
        quantization_config=bnb_cfg,
        torch_dtype=torch.bfloat16 if not load_in_4bit else None,
        device_map="auto",
        trust_remote_code=True,
    )

    if load_in_4bit:
        model = prepare_model_for_kbit_training(model)

    return model, tokenizer


# ──────────────────────────────────────────────────────────────────────────────
# LoRA configuration
# ──────────────────────────────────────────────────────────────────────────────

def make_lora_config(rank: int, alpha: int, dropout: float) -> LoraConfig:
    return LoraConfig(
        task_type=TaskType.CAUSAL_LM,
        r=rank,
        lora_alpha=alpha,
        lora_dropout=dropout,
        target_modules=["q_proj", "k_proj", "v_proj",
                        "o_proj", "gate_proj", "up_proj", "down_proj"],
        bias="none",
    )


# ──────────────────────────────────────────────────────────────────────────────
# Training modes
# ──────────────────────────────────────────────────────────────────────────────

def run_sft(args, model, tokenizer):
    train_ds = make_sft_dataset(args.train)
    val_ds = make_sft_dataset(args.val) if args.val else None

    lora_cfg = make_lora_config(
        args.lora_rank, args.lora_alpha, args.lora_dropout)
    model = get_peft_model(model, lora_cfg)
    model.print_trainable_parameters()

    training_args = SFTConfig(
        output_dir=args.output,
        num_train_epochs=args.epochs,
        per_device_train_batch_size=args.batch,
        gradient_accumulation_steps=args.grad_accum,
        learning_rate=args.lr,
        warmup_ratio=0.05,
        lr_scheduler_type="cosine",
        bf16=torch.cuda.is_bf16_supported(),
        fp16=not torch.cuda.is_bf16_supported(),
        logging_steps=10,
        save_strategy="epoch",
        eval_strategy="epoch" if val_ds else "no",
        load_best_model_at_end=bool(val_ds),
        report_to="none",
        max_seq_length=args.max_len,
        packing=False,
    )

    trainer = SFTTrainer(
        model=model,
        args=training_args,
        train_dataset=train_ds,
        eval_dataset=val_ds,
        processing_class=tokenizer,
    )
    trainer.train()
    trainer.save_model(args.output)
    tokenizer.save_pretrained(args.output)
    print(f"SFT adapter saved to {args.output}")


def run_dpo(args, model, tokenizer):
    train_ds = make_dpo_dataset(args.train)
    val_ds = make_dpo_dataset(args.val) if args.val else None

    lora_cfg = make_lora_config(
        args.lora_rank, args.lora_alpha, args.lora_dropout)
    model = get_peft_model(model, lora_cfg)
    model.print_trainable_parameters()

    training_args = DPOConfig(
        output_dir=args.output,
        num_train_epochs=args.epochs,
        per_device_train_batch_size=args.batch,
        gradient_accumulation_steps=args.grad_accum,
        learning_rate=args.lr,
        warmup_ratio=0.05,
        lr_scheduler_type="cosine",
        bf16=torch.cuda.is_bf16_supported(),
        fp16=not torch.cuda.is_bf16_supported(),
        logging_steps=5,
        save_strategy="epoch",
        eval_strategy="epoch" if val_ds else "no",
        beta=args.dpo_beta,
        max_length=args.max_len,
        max_prompt_length=args.max_len // 2,
        report_to="none",
    )

    trainer = DPOTrainer(
        model=model,
        args=training_args,
        train_dataset=train_ds,
        eval_dataset=val_ds,
        processing_class=tokenizer,
    )
    trainer.train()
    trainer.save_model(args.output)
    tokenizer.save_pretrained(args.output)
    print(f"DPO adapter saved to {args.output}")


# ──────────────────────────────────────────────────────────────────────────────
# Entry point
# ──────────────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(
        description="LoRA fine-tune Hope.Agent clinical model.")
    parser.add_argument("--mode", choices=["sft", "dpo"], required=True)
    parser.add_argument("--model", default="Qwen/Qwen3-8B",
                        help="HuggingFace model ID or local path")
    parser.add_argument("--train", required=True, help="Training JSONL")
    parser.add_argument("--val", help="Validation JSONL (optional)")
    parser.add_argument("--output", required=True,
                        help="Directory to save the LoRA adapter")
    parser.add_argument("--epochs", type=int, default=3)
    parser.add_argument("--batch", type=int, default=2)
    parser.add_argument("--grad-accum", type=int, default=8)
    parser.add_argument("--max-len", type=int, default=2048)
    parser.add_argument("--lr", type=float, default=2e-4)
    parser.add_argument("--lora-rank", type=int, default=64)
    parser.add_argument("--lora-alpha", type=int, default=16)
    parser.add_argument("--lora-dropout", type=float, default=0.05)
    parser.add_argument("--dpo-beta", type=float,
                        default=0.1, help="DPO KL penalty (β)")
    parser.add_argument("--load-in-4bit", action="store_true",
                        help="QLoRA: quantise base model to 4-bit")
    args = parser.parse_args()

    Path(args.output).mkdir(parents=True, exist_ok=True)
    model, tokenizer = load_model_and_tokenizer(args.model, args.load_in_4bit)

    if args.mode == "sft":
        run_sft(args, model, tokenizer)
    else:
        run_dpo(args, model, tokenizer)


if __name__ == "__main__":
    main()
