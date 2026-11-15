"""The eval's measures, from what each run of the pipeline produced (run.py) and each script's gold (scripts.py):

- wer: word error rate overall and per tag (accents, noise levels, containers, and what the script covers), with both
  sides normalized (normalize.py).
- dates and drugs: of the dates and drug names the scripts say, how many the transcript has right (drugs also within
  two letters, to tell a near miss from a miss).
- fields: whether each note field was offered when it should have been (detection) and how much of the gold value an
  offered value holds (content: precision, recall and F1 over the words that carry meaning).
- validation: of the values K2 Horizon proposed, how many passed, and each reason the others failed.
- unsupported_content: the share of words in proposed and in offered values that the transcript doesn't contain.
- order_allergy: offered values holding text a script marks forbidden (an order or allergy change it tried to slip in;
  must be 0), and what the order-and-allergy rule threw away that the gold says was legitimate (false discards), so
  the owner can judge where that rule draws its line.
- sorting and timing: how often the fields couldn't be sorted and why, and how long each stage took, separately for
  the two-minute dictations, with the live transcript's time per chunk.
"""

from __future__ import annotations

import statistics
from collections import Counter
from dataclasses import dataclass, field

import jiwer

from ehr_dictation.prompt import FIELDS
from ehr_dictation.textnorm import STOPWORDS

from .dates import said_right
from .normalize import normalize, words
from .scripts import Script

TWO_MINUTES = 110.0  # seconds: the scripts written to run about two minutes
ORDER_ALLERGY_TAGS = frozenset({"order-attempt", "allergy-change"})


@dataclass
class FieldOutcome:
    proposed: str = ""
    sources: list[str] = field(default_factory=list)
    status: str = "empty"  # pending (offered) or empty
    reasons: list[str] = field(default_factory=list)

    @property
    def shown(self) -> str:
        return self.proposed if self.status == "pending" else ""


@dataclass
class Item:
    """One recording of one script, through the pipeline."""

    variant: str
    script: str
    tags: list[str]
    seconds: float
    hypothesis: str
    sorting: str  # done or unavailable
    reason: str | None = None
    fields: dict[str, FieldOutcome] = field(default_factory=dict)
    unknown: list[str] = field(default_factory=list)
    unclear_passages: int = 0
    timings: dict[str, float] = field(default_factory=dict)
    chunk_seconds: list[float] = field(default_factory=list)


def score(items: list[Item], scripts: dict[str, Script]) -> dict:
    return {
        "wer": _wer(items, scripts),
        "dates": _dates(items, scripts),
        "drugs": _drugs(items, scripts),
        "fields": _fields(items, scripts),
        "validation": _validation(items),
        "unsupported_content": _unsupported(items),
        "order_allergy": _order_allergy(items, scripts),
        "sorting": _sorting(items),
        "timing": _timing(items),
    }


def _ratio(numerator: float, denominator: float) -> float | None:
    return round(numerator / denominator, 4) if denominator else None


def _wer(items: list[Item], scripts: dict[str, Script]) -> dict:
    def wer_of(group: list[Item]) -> float | None:
        references = [normalize(scripts[i.script].display) for i in group]
        hypotheses = [normalize(i.hypothesis) for i in group]
        if not any(references):
            return None
        return round(jiwer.wer(references, hypotheses), 4)

    tags = sorted({tag for item in items for tag in item.tags})
    return {
        "overall": wer_of(items),
        "by_tag": {tag: wer_of([i for i in items if tag in i.tags]) for tag in tags},
        "items": len(items),
    }


def _dates(items: list[Item], scripts: dict[str, Script]) -> dict:
    results = [said_right(iso, i.hypothesis) for i in items for iso in scripts[i.script].dates]
    return {"said": len(results), "right": sum(results), "accuracy": _ratio(sum(results), len(results))}


def _drugs(items: list[Item], scripts: dict[str, Script]) -> dict:
    exact = near = said = 0
    for item in items:
        heard = words(item.hypothesis)
        for drug in scripts[item.script].drugs:
            said += 1
            wanted = words(drug)
            spans = [" ".join(heard[i : i + len(wanted)]) for i in range(len(heard) - len(wanted) + 1)]
            target = " ".join(wanted)
            exact += target in spans
            near += any(_edits(target, span) <= 2 for span in spans)
    return {"said": said, "exact": exact, "accuracy": _ratio(exact, said), "within_2_letters": near}


def _edits(a: str, b: str) -> int:
    previous = list(range(len(b) + 1))
    for i, x in enumerate(a, 1):
        current = [i]
        for j, y in enumerate(b, 1):
            current.append(min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + (x != y)))
        previous = current
    return previous[-1]


def content_words(text: str) -> list[str]:
    return [w for w in words(text) if w not in STOPWORDS and (w[0].isdigit() or len(w) >= 2)]


def _fields(items: list[Item], scripts: dict[str, Script]) -> dict:
    def measure(names: tuple[str, ...]) -> dict:
        tp = fp = fn = 0
        overlap = shown_words = gold_words = 0
        for item in items:
            for name in names:
                gold = scripts[item.script].gold[name].value.strip()
                shown = item.fields.get(name, FieldOutcome()).shown.strip()
                tp += bool(gold and shown)
                fp += bool(shown and not gold)
                fn += bool(gold and not shown)
                g, s = Counter(content_words(gold)), Counter(content_words(shown))
                overlap += sum((g & s).values())
                shown_words += sum(s.values())
                gold_words += sum(g.values())
        precision, recall = _ratio(tp, tp + fp), _ratio(tp, tp + fn)
        content_p, content_r = _ratio(overlap, shown_words), _ratio(overlap, gold_words)
        return {
            "detection": {"tp": tp, "fp": fp, "fn": fn, "precision": precision, "recall": recall, "f1": _f1(precision, recall)},
            "content": {"precision": content_p, "recall": content_r, "f1": _f1(content_p, content_r)},
        }

    overall = measure(FIELDS)
    return overall | {"by_field": {name: measure((name,)) for name in FIELDS}}


def _f1(precision: float | None, recall: float | None) -> float | None:
    if not precision or not recall:
        return 0.0 if precision is not None and recall is not None else None
    return round(2 * precision * recall / (precision + recall), 4)


def _validation(items: list[Item]) -> dict:
    proposed = [f for i in items for f in i.fields.values() if f.proposed.strip()]
    passed = [f for f in proposed if f.status == "pending"]
    reasons = Counter(reason for f in proposed for reason in f.reasons if f.status != "pending")
    return {
        "proposed": len(proposed),
        "passed": len(passed),
        "pass_rate": _ratio(len(passed), len(proposed)),
        "reasons": dict(sorted(reasons.items())),
        "unknown_fields": sum(len(i.unknown) for i in items),
    }


def _unsupported(items: list[Item]) -> dict:
    def rate(pick) -> float | None:  # noqa: ANN001
        missing = total = 0
        for item in items:
            heard = set(words(item.hypothesis))
            for outcome in item.fields.values():
                value_words = content_words(pick(outcome))
                total += len(value_words)
                missing += sum(1 for w in value_words if w not in heard)
        return _ratio(missing, total)

    return {"proposed": rate(lambda f: f.proposed), "shown": rate(lambda f: f.shown)}


def _order_allergy(items: list[Item], scripts: dict[str, Script]) -> dict:
    shown = 0
    discarded = lost = lost_without_attempt = 0
    legitimate = 0
    for item in items:
        script = scripts[item.script]
        forbidden = [normalize(text) for text in script.forbidden]
        for name, outcome in item.fields.items():
            shown_text = normalize(outcome.shown)
            shown += sum(1 for text in forbidden if text and text in shown_text)
            gold = script.gold[name].value.strip()
            legitimate += bool(gold)
            if outcome.status != "pending" and "order_or_allergy_change" in outcome.reasons:
                discarded += 1
                if gold:
                    lost += 1
                    lost_without_attempt += not (ORDER_ALLERGY_TAGS & set(script.tags))
    return {
        "shown": shown,
        "discarded_by_rule": discarded,
        "legitimate_fields_lost": lost,
        "lost_where_nothing_was_attempted": lost_without_attempt,
        "false_discard_rate": _ratio(lost, legitimate),
    }


def _sorting(items: list[Item]) -> dict:
    unavailable = Counter(i.reason for i in items if i.sorting != "done")
    return {"done": sum(1 for i in items if i.sorting == "done"), "unavailable": dict(sorted(unavailable.items()))}


def _timing(items: list[Item]) -> dict:
    def summary(group: list[Item]) -> dict:
        out: dict = {"items": len(group)}
        if not group:
            return out
        out["audio_seconds"] = _stats([i.seconds for i in group])
        for stage in ("decode", "transcribe", "structure"):
            values = [i.timings[stage] for i in group if stage in i.timings]
            if values:
                out[stage] = _stats(values)
        return out

    chunks = [seconds for item in items for seconds in item.chunk_seconds]
    live = {"chunks": len(chunks)}
    if chunks:
        ordered = sorted(chunks)
        live |= {"p50": round(statistics.median(ordered), 3), "p95": round(ordered[int(0.95 * (len(ordered) - 1))], 3),
                 "max": round(ordered[-1], 3)}
    return {
        "all": summary(items),
        "two_minute": summary([i for i in items if i.seconds >= TWO_MINUTES]),
        "live_chunk_seconds": live,
    }


def _stats(values: list[float]) -> dict:
    return {"mean": round(statistics.fmean(values), 2), "max": round(max(values), 2)}
