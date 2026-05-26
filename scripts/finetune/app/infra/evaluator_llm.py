"""Adapter wrapping the existing LLM-judge evaluator (`evaluate.evaluate`)."""

from __future__ import annotations

import sys
from pathlib import Path

from ..domain import EvaluationResult

_THIS_DIR = Path(__file__).resolve().parent
_FINETUNE_DIR = _THIS_DIR.parent.parent
if str(_FINETUNE_DIR) not in sys.path:
    sys.path.insert(0, str(_FINETUNE_DIR))


class LlmJudgeEvaluator:
    """ChampionEvaluator port impl. Calls into `evaluate.evaluate`."""

    def evaluate(self, *, base_model: str,
                 candidate_path: Path, champion_path: Path | None,
                 suite_path: Path, judge_url: str, judge_model: str,
                 promote_win_rate: float, min_samples: int,
                 ) -> EvaluationResult:
        # type: ignore[import-not-found]
        from evaluate import evaluate as _evaluate

        raw = _evaluate(
            base_model=base_model,
            candidate_path=candidate_path,
            champion_path=champion_path,
            suite_path=suite_path,
            judge_url=judge_url,
            judge_model=judge_model,
            promote_win_rate=promote_win_rate,
            min_samples=min_samples,
        )
        return EvaluationResult(
            total=raw.total,
            candidate_wins=raw.candidate_wins,
            ties=raw.ties,
            champion_wins=raw.champion_wins,
            win_rate=raw.win_rate,
            elo_delta=raw.elo_delta,
            promote=raw.promote,
            details=list(raw.details or []),
        )
