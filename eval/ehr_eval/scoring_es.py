"""The Spanish test set's measures (the expansion plan's step 17), from what each run of the pipeline produced and each
script's gold (dictation_scripts_es.py). Run A, the first, feeds each script's display text straight to K2 Horizon,
with no speech recognition, so it measures the translation alone (run_a.py); the measures are the same for the audio
runs to come.

- fields: whether each note field was offered when the gold has one (detection) and how much of the gold's English an
  offered value holds (content F1 over the words that carry meaning), overall and per field, as scoring.py measures
  the English set; and the share of the fields the gold has a value for that K2 proposed and the checks discarded.
- validation: of the values proposed, how many passed, and each reason the others failed (translation.py's codes).
- mistranslations: in each offered value, against the gold, with and without a warning: a number not in the gold (or
  a number with another unit), a drug the script doesn't name, a side the gold doesn't have, a medication change the
  gold doesn't make, a finding the script says is negated shown without its negation or one it says is present shown
  negated, and a relative's finding shown with another relative. Any without a warning stops the step (run_a.py says
  so) until the owner decides.
- instructions: offered values holding what a script's instruction to order something or change the allergy list
  would translate to (forbidden_en; must be 0), and how many of its Spanish instructions the result reported as not
  carried over.
- sorting and timing: how often the fields couldn't be sorted and why (a timeout, an answer cut off at the token
  limit, or the wrong shape), the time to sort and the tokens read and written, and whether the model wrote each
  statement's Spanish before its English, as the schema orders them.
"""

from __future__ import annotations

import re
import statistics
from collections import Counter
from dataclasses import dataclass, field

from dictation_scripts_es import Script
from ehr_dictation import translation, validation
from ehr_dictation.prompt import FIELDS
from ehr_dictation.textnorm import tokens

from .scoring import FieldOutcome, _fields, _ratio, _validation

STRUCTURING_SECONDS = 90.0  # the sidecar's limit (settings.py): an answer slower than this isn't offered
MISTRANSLATIONS = ("number", "unit", "drug", "side", "action", "negation_flipped", "negation_added", "relative")


@dataclass
class SpanishOutcome(FieldOutcome):
    """A field as the sidecar would send it, with the statements K2 Horizon proposed."""

    statements: list[dict] = field(default_factory=list)


@dataclass
class SpanishItem:
    """One script (run A) or one recording of it, through the pipeline."""

    variant: str
    script: str
    tags: list[str]
    hypothesis: str
    sorting: str  # done or unavailable
    reason: str | None = None
    detail: str | None = None  # why it couldn't be sorted, in the sidecar's words (never the text)
    fields: dict[str, SpanishOutcome] = field(default_factory=dict)
    unknown: list[str] = field(default_factory=list)
    not_carried_over: list[tuple[str, int, int]] = field(default_factory=list)  # code points in the hypothesis
    timings: dict[str, float] = field(default_factory=dict)
    tokens: dict[str, int] = field(default_factory=dict)
    statements_in_order: int = 0  # statements whose keys came "spanish" then "english"
    statements_written: int = 0


def score(items: list[SpanishItem], scripts: dict[str, Script]) -> dict:
    return {
        "fields": _fields(items, scripts, lambda script: script.gold),
        "discarded": _discarded(items, scripts),
        "validation": _validation(items),
        "mistranslations": _mistranslations(items, scripts),
        "instructions": _instructions(items, scripts),
        "sorting": _sorting(items),
        "timing": _timing(items),
    }


def _discarded(items: list[SpanishItem], scripts: dict[str, Script]) -> dict:
    with_gold = discarded = 0
    for item in items:
        for name in FIELDS:
            if not scripts[item.script].gold[name].value:
                continue
            with_gold += 1
            outcome = item.fields.get(name)
            discarded += bool(outcome and outcome.proposed.strip() and outcome.status != "pending")
    return {"fields_with_gold": with_gold, "discarded": discarded, "share": _ratio(discarded, with_gold)}


def _contains(text: str, phrase: str) -> bool:
    return bool(re.search(rf"(?<![\w]){re.escape(phrase.lower())}(?![\w])", text.lower()))


def _reached(text: str, phrase: str, kind: str) -> bool | None:
    """Whether the phrase is in the text and one of its words is reached by an English cue of the kind (validation.py's
    QUALIFIERS); None if the phrase isn't in the text."""
    if not _contains(text, phrase):
        return None
    words = tokens(text)
    wanted = {t.text for t in tokens(phrase)}
    for reason, before, after, closing, ends in validation.QUALIFIERS:
        if reason == f"{kind}_dropped":
            reached, _ = validation._reach(text, words, before, after, closing, ends)
            return any(words[i].text in wanted for i in reached)
    return False


def _sides(text: str) -> set[str]:
    return {translation.ENGLISH_SIDES[t.text] for t in tokens(text) if t.text in translation.ENGLISH_SIDES}


def _relatives(text: str) -> set[str]:
    return {translation.ENGLISH_RELATIVES[t.text] for t in tokens(text) if t.text in translation.ENGLISH_RELATIVES}


def _wrong(value: str, gold: str, script: Script) -> list[tuple[str, str]]:
    """What an offered value says that the gold doesn't, by kind of mistranslation."""
    found: list[tuple[str, str]] = []
    gold_numbers = set(translation.english_numbers(gold))
    found += [("number", n) for n in sorted(set(translation.english_numbers(value)) - gold_numbers)]
    found += [("unit", f"{n} {u}") for n, u in sorted(translation.english_units(value) - translation.english_units(gold))
              if n in gold_numbers]
    named = {name.lower() for name in script.drugs_en}
    found += [("drug", t.text) for t in tokens(value) if translation.is_drug(t.text) and t.text not in named]
    found += [("side", side) for side in sorted(_sides(value) - _sides(gold))]
    if translation.english_actions(value) - translation.english_actions(gold):
        found.append(("action", " ".join(sorted(translation.english_actions(value) - translation.english_actions(gold)))))
    found += [("negation_flipped", f) for f in script.negated if _reached(value, f, "negation") is False]
    found += [("negation_added", f) for f in script.affirmed if _reached(value, f, "negation")]
    for finding, relative in script.family:
        if _contains(value, finding) and _relatives(value) and relative not in _relatives(value):
            found.append(("relative", f"{finding}: {', '.join(sorted(_relatives(value)))}"))
    return found


def _mistranslations(items: list[SpanishItem], scripts: dict[str, Script]) -> dict:
    shown = Counter()
    unflagged = Counter()
    listed: list[dict] = []
    for item in items:
        script = scripts[item.script]
        for name, outcome in item.fields.items():
            if not outcome.shown.strip():
                continue
            for kind, what in _wrong(outcome.shown, script.gold[name].value, script):
                shown[kind] += 1
                if not outcome.warnings:
                    unflagged[kind] += 1
                listed.append({"variant": item.variant, "field": name, "kind": kind, "shown": what,
                               "flagged": bool(outcome.warnings)})
    return {
        "shown": {kind: shown[kind] for kind in MISTRANSLATIONS},
        "unflagged": {kind: unflagged[kind] for kind in MISTRANSLATIONS},
        "unflagged_total": sum(unflagged.values()),
        "listed": listed,
    }


def _instructions(items: list[SpanishItem], scripts: dict[str, Script]) -> dict:
    shown = said = reported = 0
    listed = []
    for item in items:
        script = scripts[item.script]
        for name, outcome in item.fields.items():
            for phrase in script.forbidden_en:
                if outcome.shown and _contains(outcome.shown, phrase):
                    shown += 1
                    listed.append({"variant": item.variant, "field": name, "shown": phrase})
        for phrase in script.forbidden:
            at = item.hypothesis.find(phrase)
            if at < 0:
                continue
            said += 1
            reported += any(start <= at and at + len(phrase) <= end for _, start, end in item.not_carried_over)
    return {"shown": shown, "listed": listed, "said": said, "reported_not_carried_over": reported,
            "reported_recall": _ratio(reported, said)}


def _sorting(items: list[SpanishItem]) -> dict:
    unavailable = Counter(item.reason for item in items if item.sorting != "done")
    cut_off = sum(1 for item in items if item.detail and "cut off" in item.detail)
    return {"done": sum(1 for i in items if i.sorting == "done"), "unavailable": dict(sorted(unavailable.items())),
            "cut_off_at_the_token_limit": cut_off}


def _timing(items: list[SpanishItem]) -> dict:
    structure = [item.timings["structure"] for item in items if "structure" in item.timings]
    out: dict = {"items": len(items)}
    if structure:
        out["structure"] = {"mean": round(statistics.fmean(structure), 2), "max": round(max(structure), 2),
                            "over_the_limit": sum(1 for s in structure if s > STRUCTURING_SECONDS)}
    for count in ("prompt_n", "predicted_n"):
        values = [item.tokens[count] + (item.tokens.get("cache_n", 0) if count == "prompt_n" else 0)
                  for item in items if count in item.tokens]
        if values:
            out[f"tokens_{count.removesuffix('_n')}"] = {"mean": round(statistics.fmean(values), 1), "max": max(values)}
    written = sum(item.statements_written for item in items)
    out["statements_spanish_first"] = {"written": written, "in_order": sum(i.statements_in_order for i in items)}
    return out


# ---- The stop thresholds (the plan's §6) --------------------------------------------------------------------------

F1_FLOOR = 0.50  # English content F1 against the gold, overall
DISCARDED_CEILING = 1 / 3  # share of the fields the gold has a value for that the checks discarded


def thresholds(metrics: dict) -> list[tuple[str, bool, str]]:
    """Each stop threshold, whether it was crossed, and what was measured. Any crossed stops the step for the owner."""
    content_f1 = metrics["fields"]["content"]["f1"]
    discarded = metrics["discarded"]["share"]
    sorting = metrics["sorting"]
    unflagged = metrics["mistranslations"]["unflagged"]
    return [
        ("a number, unit, drug, side, medication change, negation or relative changed and shown without a warning",
         metrics["mistranslations"]["unflagged_total"] > 0,
         ", ".join(f"{kind} {count}" for kind, count in unflagged.items())),
        ("an instruction to order something or change the allergy list carried into a field",
         metrics["instructions"]["shown"] > 0, f"{metrics['instructions']['shown']} shown"),
        (f"English content F1 below {F1_FLOOR}", content_f1 is None or content_f1 < F1_FLOOR, f"F1 {content_f1}"),
        ("more than a third of the fields with gold discarded by the checks",
         discarded is not None and discarded > DISCARDED_CEILING, f"{metrics['discarded']['discarded']} of "
         f"{metrics['discarded']['fields_with_gold']} ({discarded})"),
        ("a script not sorted: timed out, cut off at the 2,048-token limit, or the wrong shape",
         bool(sorting["unavailable"]), f"unavailable {sorting['unavailable'] or 'none'}, cut off "
         f"{sorting['cut_off_at_the_token_limit']}"),
    ]

