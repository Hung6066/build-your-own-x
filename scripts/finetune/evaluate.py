"""
Champion-vs-challenger evaluation with LLM-as-judge.

Generates responses from both adapters (loaded over the same base model in sequence,
to fit on a single GPU), then asks an Ollama judge model which response is better.
Returns win-rate + Elo delta + promote decision.

Eval suite format (JSONL):
    {"id": "...", "prompt": [{"role":"system","content":"..."},{"role":"user","content":"..."}]}
"""

from __future__ import annotations

import json
import math
import time
import uuid
from dataclasses import dataclass
from pathlib import Path

import httpx

from app.infra.logging import get_logger

log = get_logger(__name__)


JUDGE_PROMPT_VI = """Bạn là giám khảo y khoa khách quan. So sánh hai câu trả lời (A và B)
cho cùng câu hỏi của bác sĩ lâm sàng. Chọn câu trả lời chính xác hơn, an toàn hơn,
súc tích và có dẫn chứng/lý do tốt hơn. Bỏ qua khác biệt nhỏ về văn phong.

CÂU HỎI:
{prompt}

CÂU TRẢ LỜI A:
{answer_a}

CÂU TRẢ LỜI B:
{answer_b}

Trả lời CHỈ một trong: "A", "B", hoặc "TIE". Không giải thích."""


@dataclass
class EvalResult:
    total: int
    candidate_wins: int
    champion_wins: int
    ties: int
    win_rate: float
    elo_delta: float
    promote: bool
    details: list[dict]


def _expected(a: float, b: float) -> float:
    return 1.0 / (1.0 + math.pow(10.0, (b - a) / 400.0))


def _elo_update(a: float, b: float, score_a: float, k: float = 32.0) -> tuple[float, float]:
    ea = _expected(a, b)
    return a + k * (score_a - ea), b + k * ((1 - score_a) - (1 - ea))


def _load_suite(path: Path, max_items: int) -> list[dict]:
    items: list[dict] = []
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            items.append(json.loads(line))
            if len(items) >= max_items:
                break
    return items


def _generate(adapter_path: Path | None, base_model: str, items: list[dict],
              max_new_tokens: int = 512) -> list[str]:
    """Generate responses for each prompt. Returns list aligned with `items`."""
    import torch
    from peft import PeftModel
    from transformers import AutoModelForCausalLM, AutoTokenizer

    tok = AutoTokenizer.from_pretrained(base_model, trust_remote_code=True)
    if tok.pad_token is None:
        tok.pad_token = tok.eos_token

    model = AutoModelForCausalLM.from_pretrained(
        base_model, torch_dtype=torch.bfloat16, device_map="auto",
        trust_remote_code=True,
    )
    if adapter_path is not None and adapter_path.exists():
        model = PeftModel.from_pretrained(model, str(adapter_path))
    model.eval()

    outputs: list[str] = []
    for it in items:
        messages = it["prompt"] if isinstance(it.get("prompt"), list) else [
            {"role": "user", "content": it["prompt"]}
        ]
        text = tok.apply_chat_template(messages, tokenize=False,
                                       add_generation_prompt=True)
        inputs = tok(text, return_tensors="pt", truncation=True,
                     max_length=4096).to(model.device)
        with torch.no_grad():
            gen = model.generate(
                **inputs, max_new_tokens=max_new_tokens,
                do_sample=False, temperature=1.0, top_p=1.0,
                pad_token_id=tok.pad_token_id,
            )
        completion = tok.decode(gen[0][inputs.input_ids.shape[1]:],
                                skip_special_tokens=True).strip()
        outputs.append(completion)

    del model
    if torch.cuda.is_available():
        torch.cuda.empty_cache()
    return outputs


def _judge(prompt_text: str, answer_a: str, answer_b: str,
           judge_url: str, judge_model: str) -> str:
    """Returns 'A', 'B', or 'TIE'."""
    body = {
        "model": judge_model,
        "prompt": JUDGE_PROMPT_VI.format(
            prompt=prompt_text, answer_a=answer_a, answer_b=answer_b),
        "stream": False,
        "options": {"temperature": 0.0, "num_ctx": 8192},
    }
    try:
        r = httpx.post(f"{judge_url.rstrip('/')}/api/generate",
                       json=body, timeout=120)
        r.raise_for_status()
        out = (r.json().get("response") or "").strip().upper()
    except Exception as exc:  # noqa: BLE001
        log.warning("Judge call failed — counting as TIE",
                    extra={"err": str(exc)})
        return "TIE"
    if out.startswith("A"):
        return "A"
    if out.startswith("B"):
        return "B"
    return "TIE"


def _flatten_prompt(prompt) -> str:
    if isinstance(prompt, str):
        return prompt
    return "\n".join(f"[{m.get('role', 'user')}] {m.get('content', '')}" for m in prompt)


def evaluate(*, base_model: str, candidate_path: Path,
             champion_path: Path | None, suite_path: Path,
             judge_url: str, judge_model: str,
             promote_win_rate: float, min_samples: int,
             max_items: int = 200) -> EvalResult:
    items = _load_suite(suite_path, max_items)
    if len(items) < min_samples:
        raise ValueError(
            f"Eval suite has {len(items)} items < min_samples={min_samples}")

    log.info("Generating candidate responses",
             extra={"adapter": str(candidate_path), "n": len(items)})
    cand_answers = _generate(candidate_path, base_model, items)

    if champion_path is None:
        log.info("No prior champion — auto-promoting candidate")
        return EvalResult(total=len(items), candidate_wins=len(items),
                          champion_wins=0, ties=0, win_rate=1.0,
                          elo_delta=0.0, promote=True, details=[])

    log.info("Generating champion responses",
             extra={"adapter": str(champion_path), "n": len(items)})
    champ_answers = _generate(champion_path, base_model, items)

    details: list[dict] = []
    cand_wins = champ_wins = ties = 0
    # Order swap to mitigate positional bias
    for idx, (item, ca, ka) in enumerate(zip(items, cand_answers, champ_answers)):
        prompt_text = _flatten_prompt(item.get("prompt"))
        if idx % 2 == 0:
            v = _judge(prompt_text, ca, ka, judge_url, judge_model)
            winner = {"A": "candidate", "B": "champion", "TIE": "tie"}[v]
        else:
            v = _judge(prompt_text, ka, ca, judge_url, judge_model)
            winner = {"A": "champion", "B": "candidate", "TIE": "tie"}[v]

        if winner == "candidate":
            cand_wins += 1
        elif winner == "champion":
            champ_wins += 1
        else:
            ties += 1

        details.append({"id": item.get("id", str(idx)), "winner": winner})

    scored = cand_wins + champ_wins
    win_rate = (cand_wins + 0.5 * ties) / max(1, len(items))

    # Elo update (one virtual match per decided comparison)
    candidate_elo = champion_elo = 1000.0
    for d in details:
        if d["winner"] == "candidate":
            candidate_elo, champion_elo = _elo_update(
                candidate_elo, champion_elo, 1.0)
        elif d["winner"] == "champion":
            candidate_elo, champion_elo = _elo_update(
                candidate_elo, champion_elo, 0.0)
        else:
            candidate_elo, champion_elo = _elo_update(
                candidate_elo, champion_elo, 0.5)

    elo_delta = candidate_elo - 1000.0
    promote = win_rate >= promote_win_rate and scored >= min_samples

    log.info("Eval complete",
             extra={"total": len(items), "candidate_wins": cand_wins,
                    "champion_wins": champ_wins, "ties": ties,
                    "win_rate": win_rate, "elo_delta": elo_delta,
                    "promote": promote})

    return EvalResult(
        total=len(items),
        candidate_wins=cand_wins,
        champion_wins=champ_wins,
        ties=ties,
        win_rate=win_rate,
        elo_delta=elo_delta,
        promote=promote,
        details=details,
    )


def new_eval_id() -> str:
    return f"eval_{int(time.time())}_{uuid.uuid4().hex[:8]}"
