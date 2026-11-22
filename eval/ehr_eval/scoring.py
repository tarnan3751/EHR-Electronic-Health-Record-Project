"""The eval's measures, from what each run of the pipeline produced (run.py) and each script's gold (scripts.py):

- wer: word error rate overall and per tag (accents, noise levels, containers, and what the script covers), with both
  sides normalized (normalize.py).
- dates and drugs: of the dates and drug names the scripts say, how many the transcript has right (drugs also within
  two letters, to tell a near miss from a miss).
- fields: whether each note field was offered when it should have been (detection) and how much of the gold value an
  offered value holds (content: precision, recall and F1 over the words that carry meaning), overall, per field and per
  tag (such as recombination, mixed-plan and self-correction), against the rule-following gold: the best the
  sidecar's own rules allow, so this measures the pipeline against its design. fields_clinical: the same against the
  clinical gold, what a careful clinician would accept, which shows what the strict rules cost.
- validation: of the values K2 Horizon proposed, how many passed, and each reason the others failed.
- unsupported_content: the share of words in proposed and in offered values that the transcript doesn't contain.
- order_allergy (on what the sidecar sends for an offered field: the value with any instruction cut out of it): offered
  values holding text a script marks forbidden (an order or allergy change it tried to slip in; must be 0), found as the
  script says it or as the transcript heard it (_as_heard()), retracted (a value the clinician corrected away), or a
  drug the script names but no gold value carries (one dictated only to be stopped, refused or ordered), each of the
  last two also counted without a possible-correction warning (must be 0); how many of those the result reported as not
  carried over, so the screen can say they still need placing (recall against the scripts' forbidden items); and what
  the order-and-allergy rule threw away that the gold says was legitimate (false discards), so the owner can judge where
  that rule draws its line.
- warnings: for each warning code and for any warning, how many offered values carry it, how many of those holding a
  retracted value it caught, and of the right ones (matching the gold, holding nothing retracted), the share flagged
  anyway: its false-alarm rate, overall, on clean recordings and per tag, against each gold set.
- sorting and timing: how often the fields couldn't be sorted and why, and how long each stage took and how many tokens
  K2 Horizon read and wrote, separately for the two-minute dictations and those at the 3-minute limit, with the live
  transcript's time per chunk.
"""

from __future__ import annotations

import statistics
from collections import Counter
from collections.abc import Callable
from dataclasses import dataclass, field

import jiwer

from ehr_dictation.prompt import FIELDS
from ehr_dictation.textnorm import STOPWORDS, numbers

from .dates import said_right
from .normalize import normalize, words
from .scripts import Gold, Script

TWO_MINUTES = 110.0  # seconds: the scripts written to run about two minutes
AT_THE_LIMIT = 165.0  # seconds: the max-length scripts, near the 3-minute limit
ORDER_ALLERGY_TAGS = frozenset({"order-attempt", "allergy-change"})


@dataclass
class FieldOutcome:
    proposed: str = ""
    sources: list[str] = field(default_factory=list)
    status: str = "empty"  # pending (offered) or empty
    reasons: list[str] = field(default_factory=list)
    # What the sidecar sends when the field is pending: the proposed value with any instruction cut out of it, and
    # its warnings, each [code, start, end] (code points in the transcript).
    value: str = ""
    warnings: list[list] = field(default_factory=list)

    @property
    def shown(self) -> str:
        return self.value if self.status == "pending" else ""


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
    not_carried_over: list[tuple[str, int, int]] = field(default_factory=list)  # code points in the hypothesis
    unclear_passages: int = 0
    timings: dict[str, float] = field(default_factory=dict)
    tokens: dict[str, int] = field(default_factory=dict)  # llama-server's count: prompt, predicted
    # MedASR's passages as heard (raw text, flags, samples), so the transcript can be rebuilt exactly and the proposals
    # checked again offline (rescore.py) without running the models.
    segments: list[dict] = field(default_factory=list)
    chunk_seconds: list[float] = field(default_factory=list)


def score(items: list[Item], scripts: dict[str, Script]) -> dict:
    return {
        "wer": _wer(items, scripts),
        "dates": _dates(items, scripts),
        "drugs": _drugs(items, scripts),
        "fields": _fields(items, scripts, lambda script: script.gold_rules),
        "fields_clinical": _fields(items, scripts, lambda script: script.gold),
        "validation": _validation(items),
        "unsupported_content": _unsupported(items),
        "order_allergy": _order_allergy(items, scripts),
        "warnings": _warnings(items, scripts),
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


def _fields(items: list[Item], scripts: dict[str, Script], golds: Callable[[Script], dict[str, Gold]]) -> dict:
    def measure(names: tuple[str, ...], group: list[Item]) -> dict:
        tp = fp = fn = 0
        overlap = shown_words = gold_words = 0
        for item in group:
            for name in names:
                gold = golds(scripts[item.script])[name].value.strip()
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

    tags = sorted({tag for item in items for tag in item.tags})
    return measure(FIELDS, items) | {
        "by_field": {name: measure((name,), items) for name in FIELDS},
        "by_tag": {tag: measure(FIELDS, [item for item in items if tag in item.tags]) for tag in tags},
    }


def _f1(precision: float | None, recall: float | None) -> float | None:
    """None only when there was nothing to find; with gold to find and nothing offered, 0."""
    if recall is None:
        return None
    if not precision or not recall:
        return 0.0
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


def _contains(text: str, phrase: str) -> bool:
    """Whether the phrase is in the text as whole words, after normalizing both."""
    return bool(phrase) and f" {phrase} " in f" {text} "


def _reported(item: Item, phrase: str) -> bool:
    """Whether the phrase was said inside an instruction the result reported as not carried over."""
    return any(_contains(normalize(item.hypothesis[start:end]), phrase) for _, start, end in item.not_carried_over)


HEARD_RIGHT = 0.5  # the share of a forbidden phrase's words heard right, of all and of those carrying meaning


def _as_heard(item: Item, script: Script, phrases: list[str]) -> list[list[str]]:
    """Each phrase (normalized), with what the transcript has where the script says it: the hypothesis's words that the
    reference's words there align with (jiwer, as for the word error rate), from the first to the last, when at least
    HEARD_RIGHT of its words, and of those that carry meaning (content_words()), were heard right. So "call that in to
    his usual pharmacy", heard as "call that into his usual pharmacy", is still found in what was shown or reported,
    and words heard in its place that share little with it are not taken for it: not "add an" for "add an orders
    field", nor "duies penicillin rash me" for "low risk penicillin rash only". An empty phrase gives nothing."""
    reference, heard = words(script.display), words(item.hypothesis)
    placed: dict[int, int] = {}
    right: set[int] = set()
    if reference and heard:
        for chunk in jiwer.process_words(" ".join(reference), " ".join(heard)).alignments[0]:
            if chunk.type in ("equal", "substitute"):
                for k in range(chunk.ref_end_idx - chunk.ref_start_idx):
                    placed[chunk.ref_start_idx + k] = chunk.hyp_start_idx + k
                    if chunk.type == "equal":
                        right.add(chunk.ref_start_idx + k)
    found = []
    for phrase in phrases:
        wanted = phrase.split()
        meaning = set(content_words(phrase)) or set(wanted)
        variants = [phrase] if wanted else []
        for i in range(len(reference) - len(wanted) + 1):
            if wanted and reference[i : i + len(wanted)] == wanted:
                there = [placed[j] for j in range(i, i + len(wanted)) if j in placed]
                counted = [j for j in range(i, i + len(wanted)) if reference[j] in meaning]
                if there and all(len(right.intersection(share)) >= HEARD_RIGHT * len(share)
                                 for share in (range(i, i + len(wanted)), counted)):
                    variants.append(" ".join(heard[min(there) : max(there) + 1]))
        found.append(variants)
    return found


def _absent_drugs(script: Script) -> list[str]:
    """The drugs the script names that no gold value carries: dictated only to be stopped, refused or ordered."""
    gold_text = " ".join(normalize(gold.value) for gold in script.gold.values())
    return [normalize(drug) for drug in script.drugs if not _contains(gold_text, normalize(drug))]


def _order_allergy(items: list[Item], scripts: dict[str, Script]) -> dict:
    shown = retracted_shown = drug_shown = retracted_unflagged = drug_unflagged = 0
    unflagged: list[dict] = []
    reported = said = 0
    discarded = lost = lost_without_attempt = 0
    legitimate = 0
    for item in items:
        script = scripts[item.script]
        phrases = [normalize(text) for text in script.forbidden]
        forbidden = [variants for variants in _as_heard(item, script, phrases) if variants]
        for variants in forbidden:
            said += 1
            reported += any(_reported(item, phrase) for phrase in variants)
        retracted = [normalize(text) for text in getattr(script, "retracted", ())]
        absent_drugs = _absent_drugs(script)
        for name, outcome in item.fields.items():
            shown_text = normalize(outcome.shown)
            shown += sum(1 for variants in forbidden if any(_contains(shown_text, text) for text in variants))
            retracted_here = sum(1 for text in retracted if _contains(shown_text, text))
            drugs_here = sum(1 for drug in absent_drugs if _contains(shown_text, drug))
            retracted_shown += retracted_here
            drug_shown += drugs_here
            if not outcome.warnings:
                retracted_unflagged += retracted_here
                drug_unflagged += drugs_here
                unflagged += [{"variant": item.variant, "field": name, "shown": text}
                              for text in [*retracted, *absent_drugs] if text and _contains(shown_text, text)]
            gold = script.gold[name].value.strip()
            legitimate += bool(gold)
            if outcome.status != "pending" and "order_or_allergy_change" in outcome.reasons:
                discarded += 1
                if gold:
                    lost += 1
                    lost_without_attempt += not (ORDER_ALLERGY_TAGS & set(script.tags))
    return {
        "shown": shown,
        "retracted_shown": retracted_shown,
        "retracted_shown_unflagged": retracted_unflagged,
        "drug_not_in_gold_shown": drug_shown,
        "drug_not_in_gold_shown_unflagged": drug_unflagged,
        "unflagged": unflagged,
        "forbidden_said": said,
        "reported_not_carried_over": reported,
        "reported_recall": _ratio(reported, said),
        "discarded_by_rule": discarded,
        "legitimate_fields_lost": lost,
        "lost_where_nothing_was_attempted": lost_without_attempt,
        "false_discard_rate": _ratio(lost, legitimate),
    }


WARNING_CODES = ("possible_correction", "near_unclear_passage", "one_field_holds_dictation")
CLEAN = "noise:none"


def _wrong_items(value: str, script: Script) -> list[str]:
    """What a shown value must never hold: a retracted value, or a drug no gold value carries."""
    text = normalize(value)
    wrong = [normalize(t) for t in script.retracted] + _absent_drugs(script)
    return [item for item in wrong if item and _contains(text, item)]


def _warnings(items: list[Item], scripts: dict[str, Script]) -> dict:
    """For each warning code, and for any warning: how many shown values carry it; of those holding a retracted value or
    a drug no gold value carries, how many it caught; and of the right values, how many it flagged anyway, its false
    alarms, overall, on clean recordings and per tag, against each gold set. A right value is one shown whose gold
    field has a value, which holds nothing retracted, forbidden or outside the gold, and whose numbers and drug names
    are all in the gold value."""

    def right(value: str, gold: str, script: Script) -> bool:
        text, gold_text = normalize(value), normalize(gold)
        drugs = [normalize(drug) for drug in script.drugs if _contains(text, normalize(drug))]
        return (
            bool(gold.strip()) and not _wrong_items(value, script)
            and not any(_contains(text, normalize(t)) for t in script.forbidden)
            and set(numbers(text)) <= set(numbers(gold_text)) and all(_contains(gold_text, drug) for drug in drugs)
        )

    def flagged(outcome: FieldOutcome, codes: tuple[str, ...]) -> bool:
        return any(warning[0] in codes for warning in outcome.warnings)

    def false_alarms(golds: Callable[[Script], dict[str, Gold]], group: list[Item], codes: tuple[str, ...]) -> dict:
        right_values = flags = 0
        for item in group:
            script = scripts[item.script]
            for name, outcome in item.fields.items():
                if outcome.shown.strip() and right(outcome.shown, golds(script)[name].value, script):
                    right_values += 1
                    flags += flagged(outcome, codes)
        return {"right": right_values, "flagged": flags, "false_alarm_rate": _ratio(flags, right_values)}

    tags = sorted({tag for item in items for tag in item.tags})
    shown = [(item, outcome) for item in items for outcome in item.fields.values() if outcome.shown.strip()]
    wrong = [(item, outcome) for item, outcome in shown if _wrong_items(outcome.shown, scripts[item.script])]
    out: dict = {"shown": len(shown), "holding_a_retracted_value": len(wrong)}
    for key, codes in [(code, (code,)) for code in WARNING_CODES] + [("any", WARNING_CODES)]:
        measured = {"flagged": sum(flagged(outcome, codes) for _, outcome in shown),
                    "retracted_caught": sum(flagged(outcome, codes) for _, outcome in wrong)}
        for gold_key, golds in (("rules", lambda script: script.gold_rules), ("clinical", lambda script: script.gold)):
            measured[gold_key] = false_alarms(golds, items, codes) | {
                "clean": false_alarms(golds, [item for item in items if CLEAN in item.tags], codes),
                "by_tag": {
                    tag: false_alarms(golds, [item for item in items if tag in item.tags], codes) for tag in tags},
            }
        out[key] = measured
    return out


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
        for count in ("prompt_n", "predicted_n"):
            values = [i.tokens[count] + (i.tokens.get("cache_n", 0) if count == "prompt_n" else 0)
                      for i in group if count in i.tokens]
            if values:
                out[f"tokens_{count.removesuffix('_n')}"] = _stats(values)
        return out

    chunks = [seconds for item in items for seconds in item.chunk_seconds]
    live = {"chunks": len(chunks)}
    if chunks:
        ordered = sorted(chunks)
        live |= {"p50": round(statistics.median(ordered), 3), "p95": round(ordered[int(0.95 * (len(ordered) - 1))], 3),
                 "max": round(ordered[-1], 3)}
    return {
        "all": summary(items),
        "two_minute": summary([i for i in items if TWO_MINUTES <= i.seconds < AT_THE_LIMIT]),
        "at_the_limit": summary([i for i in items if i.seconds >= AT_THE_LIMIT]),
        "live_chunk_seconds": live,
    }


def _stats(values: list[float]) -> dict:
    return {"mean": round(statistics.fmean(values), 2), "max": round(max(values), 2)}
