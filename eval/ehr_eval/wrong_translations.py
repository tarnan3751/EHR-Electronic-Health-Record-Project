"""The review's wrong translations (eval/wrong-translations.toml; the expansion plan's step 17), checked again by this
commit's Spanish checks, with no model: each pair as the review's harness checked it, its Spanish said after a sentence
of filler as one passage of a transcript, cited whole by one statement in Plan whose English is the pair's. The gate
(gate.py) fails when the file isn't the one it records, on each wrong pair marked as reversing the meaning that passes
without a warning unless the owner has accepted it by its id (known-limitations.toml, [[accepted_wrong_translation]]),
and on each accepted one that no longer passes; it reports the rest: wrong pairs passing, faithful pairs refused.

    uv run --project eval python -m ehr_eval.wrong_translations
"""

from __future__ import annotations

import sys
import tomllib
from dataclasses import dataclass, field
from pathlib import Path

from ehr_dictation import translation
from ehr_dictation.transcript import Placed, Segment, Transcript

from .scripts import EVAL_DIR

PAIRS = "wrong-translations.toml"


@dataclass(frozen=True)
class Pair:
    id: str
    spanish: str
    english: str
    wrong: bool  # a wrong translation, or a faithful one
    kind: str | None = None  # a wrong pair's class: what it changes
    reverses: bool = False  # a wrong pair that reverses the meaning


@dataclass
class Scores:
    wrong: int = 0
    passing: list[Pair] = field(default_factory=list)  # wrong, offered with no reason and no warning
    reversing: list[Pair] = field(default_factory=list)  # of those, the ones marked as reversing the meaning
    faithful: int = 0
    refused: list[Pair] = field(default_factory=list)  # faithful, and discarded


def load(eval_dir: Path = EVAL_DIR) -> tuple[str, list[Pair]]:
    """The filler said before each pair's Spanish, and every pair: the wrong ones, then the faithful ones."""
    data = tomllib.loads((eval_dir / PAIRS).read_text(encoding="utf-8"))
    pairs = [Pair(raw["id"], raw["spanish"], raw["english"], True, raw["class"], raw["reverses"]) for raw in data["wrong"]]
    pairs += [Pair(raw["id"], raw["spanish"], raw["english"], False) for raw in data["faithful"]]
    return data["context"], pairs


def checked(context: str, pair: Pair) -> tuple[list[str], list[str]]:
    """The reasons the checks discard the pair's field for, and the warnings it is offered with."""
    text = f"{context} {pair.spanish}"
    heard = Transcript(text, (Placed(Segment(0, 0, text, 1.0, None, True), 0, len(text)),), True)
    statement = {"spanish": [pair.spanish.rstrip(".")], "english": pair.english}
    plan = translation.check({"plan": [statement]}, [], heard).fields["plan"]
    return list(plan.reasons), [warning[0] for warning in plan.warnings]


def score(eval_dir: Path = EVAL_DIR) -> Scores:
    context, pairs = load(eval_dir)
    scores = Scores()
    for pair in pairs:
        reasons, warnings = checked(context, pair)
        if pair.wrong:
            scores.wrong += 1
            if not reasons and not warnings:
                scores.passing.append(pair)
                if pair.reverses:
                    scores.reversing.append(pair)
        else:
            scores.faithful += 1
            if reasons:
                scores.refused.append(pair)
    return scores


def main() -> int:
    scores = score()
    for pair in scores.passing:
        print(f"{pair.id} {'reverses the meaning' if pair.reverses else 'wrong'}, passes: {pair.spanish!r} as "
              f"{pair.english!r} ({pair.kind})")
    for pair in scores.refused:
        print(f"{pair.id} faithful, refused: {pair.spanish!r} as {pair.english!r}")
    print(f"{scores.wrong} wrong translations: {len(scores.passing)} pass without a warning, {len(scores.reversing)} of "
          f"them reversing the meaning (there must be none the owner hasn't accepted); {len(scores.refused)} of "
          f"{scores.faithful} faithful translations refused.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
