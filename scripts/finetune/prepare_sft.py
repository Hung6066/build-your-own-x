"""
prepare_sft.py — Convert Hope.Agent trajectory JSONL export to Qwen3 SFT chat format.

Usage:
    python prepare_sft.py --input trajectory.jsonl --output sft_train.jsonl [--val-split 0.1]

Input format (one line per conversation):
    {
        "conversation_id": "...",
        "messages": [
            {"role": "user", "content": "...", "at": "..."},
            {"role": "assistant", "content": "...", "at": "..."},
            ...
        ]
    }

Output format (one line per turn pair):
    {
        "messages": [
            {"role": "system", "content": "..."},
            {"role": "user", "content": "..."},
            {"role": "assistant", "content": "..."}
        ]
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


def conversation_to_pairs(conv: dict) -> list[dict]:
    """
    Convert a multi-turn conversation to (system, user, assistant) training samples.
    Each consecutive user→assistant pair becomes one sample.
    """
    messages = conv.get("messages", [])
    samples = []
    i = 0
    while i < len(messages) - 1:
        if messages[i]["role"] == "user" and messages[i + 1]["role"] == "assistant":
            user_content = messages[i].get("content", "").strip()
            assistant_content = messages[i + 1].get("content", "").strip()
            if user_content and assistant_content:
                samples.append({
                    "messages": [
                        {"role": "system", "content": SYSTEM_PROMPT},
                        {"role": "user", "content": user_content},
                        {"role": "assistant", "content": assistant_content},
                    ]
                })
        i += 1
    return samples


def main():
    parser = argparse.ArgumentParser(
        description="Prepare SFT dataset from Hope.Agent trajectories.")
    parser.add_argument("--input", required=True,
                        help="Input JSONL file from /v1/training/export")
    parser.add_argument("--output", required=True,
                        help="Output JSONL file for SFT training")
    parser.add_argument("--val-output", help="Validation JSONL (optional)")
    parser.add_argument("--val-split", type=float, default=0.1,
                        help="Fraction for validation (0 to skip)")
    parser.add_argument("--seed", type=int, default=42)
    args = parser.parse_args()

    random.seed(args.seed)
    all_samples = []

    for conv in load_jsonl(args.input):
        all_samples.extend(conversation_to_pairs(conv))

    random.shuffle(all_samples)
    print(f"Total samples: {len(all_samples)}")

    if args.val_split > 0 and args.val_output:
        split = int(len(all_samples) * (1 - args.val_split))
        train, val = all_samples[:split], all_samples[split:]
    else:
        train, val = all_samples, []

    Path(args.output).write_text(
        "\n".join(json.dumps(s, ensure_ascii=False) for s in train),
        encoding="utf-8",
    )
    print(f"Wrote {len(train)} training samples → {args.output}")

    if val and args.val_output:
        Path(args.val_output).write_text(
            "\n".join(json.dumps(s, ensure_ascii=False) for s in val),
            encoding="utf-8",
        )
        print(f"Wrote {len(val)} validation samples → {args.val_output}")


if __name__ == "__main__":
    main()
