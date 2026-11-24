"""The trick sentences (eval/trick-sentences.toml; the expansion plan's step 15.1), weighed again by this commit's
checks: what each is expected to give, and what it gives. No model is needed: each sentence is a whole final transcript,
and the dictation's own rules (validation.weigh()) say what it proposes. The gate (gate.py) fails when the file isn't the
one it records, when a sentence marked as reversing the meaning is proposed, when the wrong proposals number more than
the set's most_wrong, or when a control is not proposed; it reports what the strict rule costs (expect = "cost").

    uv run --project eval python -m ehr_eval.tricks
"""

from __future__ import annotations

import sys
import tomllib
from dataclasses import dataclass, field
from pathlib import Path

from ehr_dictation import validation
from ehr_dictation.transcript import Transcript

from .scripts import EVAL_DIR

TRICKS = "trick-sentences.toml"


@dataclass(frozen=True)
class Trick:
    said: str
    family: str
    expect: str  # proposed, held_back, or cost (a plain instruction the strict rule holds back)
    reverses: bool = False  # held back, and a proposal would reverse the meaning
    tolerated: str | None = None  # held back, and a proposal is a wrong one the owner tolerates, of this kind
    still_proposed: bool = False  # held back, and recorded as one the rules still propose


@dataclass
class Scores:
    sentences: int
    most_wrong: int
    wrong: list[str] = field(default_factory=list)  # proposed, though expected held back
    reversing: list[str] = field(default_factory=list)  # of those, the ones marked as reversing the meaning
    controls: int = 0  # expected proposed
    missed: list[str] = field(default_factory=list)  # expected proposed, and not
    cost: int = 0  # plain instructions the strict rule holds back (expect = "cost")
    cost_held: int = 0  # of those, held back now


def load(eval_dir: Path = EVAL_DIR) -> tuple[list[Trick], int, dict[str, str]]:
    """The trick sentences, the most wrong proposals they may give, and each tolerated kind's reason."""
    data = tomllib.loads((eval_dir / TRICKS).read_text(encoding="utf-8"))
    tricks = [
        Trick(said, group["family"], group["expect"], group.get("reverses", False), group.get("tolerated"),
              group.get("still_proposed", False))
        for group in data["group"]
        for said in group["said"]
    ]
    return tricks, data["most_wrong"], data.get("tolerated", {})


def proposed(said: str) -> list[str]:
    """The words of each proposal the dictation makes from a sentence said as a whole, final transcript."""
    return [said[weighed.start : weighed.end] for weighed in validation.weigh(Transcript(said, (), True))
            if weighed.held_back is None]


def score(eval_dir: Path = EVAL_DIR) -> Scores:
    tricks, most_wrong, _ = load(eval_dir)
    scores = Scores(sentences=len(tricks), most_wrong=most_wrong)
    for trick in tricks:
        given = proposed(trick.said)
        if trick.expect == "proposed":
            scores.controls += 1
            if not given:
                scores.missed.append(trick.said)
        elif trick.expect == "cost":
            scores.cost += 1
            scores.cost_held += not given
        elif given:
            scores.wrong.append(trick.said)
            if trick.reverses:
                scores.reversing.append(trick.said)
    return scores


def main() -> int:
    scores = score()
    for said in scores.wrong:
        print(f"{'reverses the meaning' if said in scores.reversing else 'wrong'}: {said!r}")
    for said in scores.missed:
        print(f"expected proposed, and not: {said!r}")
    print(
        f"{scores.sentences} trick sentences: {len(scores.wrong)} wrong proposals (at most {scores.most_wrong}), "
        f"{len(scores.reversing)} reversing the meaning (there must be none); {scores.controls - len(scores.missed)} of "
        f"{scores.controls} controls proposed; {scores.cost_held} of {scores.cost} plain instructions held back by the strict "
        "rule, what it costs."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
