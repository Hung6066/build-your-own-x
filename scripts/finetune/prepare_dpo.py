"""
prepare_dpo.py — Convert Hope.Agent DPO JSONL export to HuggingFace TRL DPOTrainer format.

Usage:
    python prepare_dpo.py --input dpo.jsonl --output dpo_train.jsonl [--val-split 0.1]

Input format (one line per preference pair, from /v1/training/export/dpo):
    {
        "prompt": "bệnh nhân bị đau ngực ...",
        "chosen": "Cần làm ECG ngay và ...",
        "rejected": "Uống thuốc giảm đau ...",
        "specialty": "cardiology",
        "source": "hope-agent"
    }

Output format compatible with TRL DPOTrainer (chat template):
    {
        "prompt": [{"role": "user", "content": "..."}],
        "chosen": [{"role": "assistant", "content": "..."}],
        "rejected": [{"role": "assistant", "content": "..."}]
    }
"""

import argparse
import json
import random
from pathlib import Path

SYSTEM_PROMPT = (
    "Bạn là Hope Agent – trợ lý y tế lâm sàng thông minh, hỗ trợ bác sĩ và nhân viên y tế "
    "tại Việt Nam. Trả lời chính xác, ngắn gọn, dựa trên bằng chứng y học. "
    "Không chẩn đoán thay bác sĩ. Ưu tiên an toàn bệnh nhân."
)


def load_jsonl(path: str):
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                yield json.loads(line)


def to_dpo_sample(row: dict) -> dict | None:
    prompt = row.get("prompt", "").strip()
    chosen = row.get("chosen", "").strip()
    rejected = row.get("rejected", "").strip()

    if not prompt or not chosen or not rejected:
        return None
    if chosen == rejected:
        return None

    return {
        "prompt": [
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": prompt},
        ],
        "chosen": [{"role": "assistant", "content": chosen}],
        "rejected": [{"role": "assistant", "content": rejected}],
    }


def main():
    parser = argparse.ArgumentParser(
        description="Prepare DPO dataset from Hope.Agent preference export.")
    parser.add_argument("--input", required=True,
                        help="DPO JSONL from /v1/training/export/dpo")
    parser.add_argument("--output", required=True,
                        help="Output JSONL for DPO training")
    parser.add_argument("--val-output", help="Validation JSONL (optional)")
    parser.add_argument("--val-split", type=float, default=0.1,
                        help="Fraction for validation (0 to skip)")
    parser.add_argument("--seed", type=int, default=42)
    args = parser.parse_args()

    random.seed(args.seed)
    all_samples = []
    skipped = 0

    for row in load_jsonl(args.input):
        sample = to_dpo_sample(row)
        if sample:
            all_samples.append(sample)
        else:
            skipped += 1

    random.shuffle(all_samples)
    print(f"Total DPO pairs: {len(all_samples)} (skipped {skipped})")

    if args.val_split > 0 and args.val_output:
        split = int(len(all_samples) * (1 - args.val_split))
        train, val = all_samples[:split], all_samples[split:]
    else:
        train, val = all_samples, []

    Path(args.output).write_text(
        "\n".join(json.dumps(s, ensure_ascii=False) for s in train),
        encoding="utf-8",
    )
    print(f"Wrote {len(train)} training pairs → {args.output}")

    if val and args.val_output:
        Path(args.val_output).write_text(
            "\n".join(json.dumps(s, ensure_ascii=False) for s in val),
            encoding="utf-8",
        )
        print(f"Wrote {len(val)} validation pairs → {args.val_output}")


if __name__ == "__main__":
    main()
