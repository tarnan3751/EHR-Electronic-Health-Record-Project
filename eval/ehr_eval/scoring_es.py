"""The Spanish test set's measures (the expansion plan's step 17), from what each run of the pipeline produced and each
script's gold (dictation_scripts_es.py). Run A, the first, feeds each script's display text straight to K2 Horizon,
with no speech recognition, so it measures the translation alone (run_a.py); the measures are the same for the audio
runs to come.

- fields: whether each note field was offered when the gold has one (detection) and how much of the gold's English an
  offered value holds (content F1 over the words that carry meaning), overall and per field, as scoring.py measures
  the English set; and the share of the fields the gold has a value for that K2 proposed and the checks discarded.
- validation: of the values proposed, how many passed, and each reason the others failed (translation.py's codes).
- mistranslations: each statement of each offered value compared with the Spanish it cites, read as the checks read
  it (translation.py), with and without a warning. Changed: an English number with its unit that isn't one of the
  Spanish's numbers with its unit, or a number without one that the Spanish doesn't say; a drug no word of the Spanish
  backs; a side, a medication change, a negation, uncertainty, the past or someone else having it on one side only;
  relatives that differ; and, from the script's lists, a finding it says is negated shown without its negation
  (negation_flipped), one it says is present shown negated (negation_added), or a relative's finding shown without its
  relative (family). A changed item said elsewhere in the script is marked as moved from another passage, and is still
  changed. Any changed without a warning stops the step (run_a.py says so) until the owner decides. Misfiled, reported
  on its own and never a stop: a number, unit, drug, side or medication change the statement's Spanish says, so not
  changed, in a field whose gold doesn't have it (a Plan passage put in Subjective).
- instructions: offered values holding what a script's instruction to order something or change the allergy list
  would translate to (forbidden_en; must be 0), and how many of its Spanish instructions the result reported as not
  carried over.
- sorting and timing: how often the fields couldn't be sorted and why (a timeout, an answer cut off at the token
  limit, or the wrong shape), the time to sort and the tokens read and written, and whether the model wrote each
  statement's keys in the schema's order: its Spanish, then its field, then its English.

The audio run (run_es.py, score_audio()) adds, over the recordings dictated in Spanish:

- wer: the word error rate of Whisper's transcript against the display text, both in lower case without punctuation,
  numbers as digits whether said or written ("treinta y siete coma ocho" and "37,8" are both 37.8, "1.000" a thousand,
  "142/88" 142 sobre 88, "%" por ciento), accents kept; and beside it the same with the accents taken off (wer_no_accents),
  overall and by tag;
- drugs and dates: the share of the drugs each script names that the transcript has word for word, accents aside (and
  within two letters), and of its dates said with their day and month;
- unclear: the passages marked, by why (other_language among them);
- live: the time to answer each one-second chunk of a dictation in Spanish (the sidecar's 15-second budget, NFR-14);
- wrong_language: the two recordings dictated in the wrong language, for what was marked and for no instruction
  carried into a field (FR-14), never for the fields' content;
- text: the measures above that don't need speech recognition, on each script's display text alone (run A's).
"""

from __future__ import annotations

import re
import statistics
from collections import Counter
from dataclasses import dataclass, field

import jiwer

from dictation_scripts_es import Script
from ehr_dictation import spanish, translation, validation
from ehr_dictation.prompt import FIELDS
from ehr_dictation.textnorm import tokens
from ehr_dictation.transcript import Transcript

from .scoring import FieldOutcome, _edits, _fields, _ratio, _stats, _validation

STRUCTURING_SECONDS = 90.0  # the sidecar's limit (settings.py): an answer slower than this isn't offered
CHANGED = ("number", "unit", "drug", "side", "action", "negation", "certainty", "history", "experiencer", "relative",
           "negation_flipped", "negation_added", "family")
MISFILED = ("number", "unit", "drug", "side", "action")


@dataclass
class SpanishOutcome(FieldOutcome):
    """A field as the sidecar would send it, with the statements K2 Horizon proposed, and, when it is offered, each
    statement as shown: its English and the passages of the transcript it cites, outside instructions (code points)."""

    statements: list[dict] = field(default_factory=list)
    shown_statements: list[dict] = field(default_factory=list)


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
    statements_in_order: int = 0  # statements whose keys came "spanish", "field", "english"
    statements_written: int = 0
    answer: str | None = None  # the model's answer as written, only when kept for a diagnostic run (run_a.py)
    # A recording's (run_es.py): its length, the speech model's passages as heard (raw text, flags, samples), so the
    # transcript can be rebuilt and checked again offline, the live chunks' times, and the language it was run as when
    # it is dictated in the wrong one ("es" for an English script, "en" for a Spanish one).
    seconds: float = 0.0
    segments: list[dict] = field(default_factory=list)
    chunk_seconds: list[float] = field(default_factory=list)
    run_as: str | None = None


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


def negated(text: str, phrase: str) -> list[bool]:
    """For each place the phrase is in the English text, whether an English negation (validation.py's) reaches one of
    its words there."""
    words = tokens(text)
    negation = next(qualifier for qualifier in validation.QUALIFIERS if qualifier.reason == "negation_dropped")
    reached = validation._reach(text, words, negation)[0]
    return [any(i in reached for i, word in enumerate(words) if m.start() <= word.start < m.end())
            for m in re.finditer(rf"(?<![\w]){re.escape(phrase.lower())}(?![\w])", text.lower())]


def _sides(text: str) -> set[str]:
    return {translation.ENGLISH_SIDES[t.text] for t in tokens(text) if t.text in translation.ENGLISH_SIDES}


def _relatives(text: str) -> set[str]:
    return {translation.ENGLISH_RELATIVES[t.text] for t in tokens(text) if t.text in translation.ENGLISH_RELATIVES}


def _reading(text: str) -> translation._Context:
    """The transcript read as the checks read it: its words, numbers, drugs, sides, corrections and qualifiers."""
    return translation._context(Transcript(text, (), True))


def _statement_items(english: str, spans: list[tuple[int, int]], ctx: translation._Context, gold: str,
                     script: Script) -> tuple[list[tuple[str, str, bool]], list[tuple[str, str]]]:
    """What one shown statement changed from the Spanish it cites (kind, what, said elsewhere in the script), and what
    it says from that Spanish that its field's gold doesn't have (kind, what)."""
    inside = [i for i, t in enumerate(ctx.toks) if any(a <= t.start and t.end <= b for a, b in spans)]
    said_index = [i for i in inside if i not in ctx.cues and i not in ctx.taken_back]
    said = [ctx.toks[i] for i in said_index]
    terms = [t for t in ctx.terms if t.first in said_index and t.last in said_index]
    changed: list[tuple[str, str, bool]] = []
    misfiled: list[tuple[str, str]] = []

    numbers = translation.said_numbers(said, terms)
    everywhere = translation.said_numbers(ctx.toks, ctx.terms)
    pairs = {(r, unit) for number, unit in ctx.measured if number.first in said_index for r in number.readings}
    all_pairs = {(r, unit) for number, unit in ctx.measured for r in number.readings}
    gold_numbers, gold_pairs = set(translation.english_numbers(gold)), translation.english_units(gold)
    with_unit = translation.english_units(english)
    for n, unit in sorted(with_unit):
        if (n, unit) not in pairs:
            changed.append(("unit" if n in numbers else "number", f"{n} {unit}", (n, unit) in all_pairs))
        elif n not in gold_numbers:
            misfiled.append(("number", f"{n} {unit}"))
        elif (n, unit) not in gold_pairs:
            misfiled.append(("unit", f"{n} {unit}"))
    for n in sorted(set(translation.english_numbers(english)) - {n for n, _ in with_unit}):
        if n not in numbers:
            changed.append(("number", n, n in everywhere))
        elif n not in gold_numbers:
            misfiled.append(("number", n))

    english_words = [t.text for t in tokens(english)]
    gold_words = {t.text for t in tokens(gold)}
    drugs = {w for w in english_words if w.isalpha() and translation.is_drug(w)} | translation.dosed(english)
    candidates = [spanish.drug_candidates(t.key) for t in said if t.key.isalpha()]
    script_candidates = {name for t in ctx.toks if t.key.isalpha() for name in spanish.drug_candidates(t.key)}
    for drug in sorted(drugs):
        backing = [names for names in candidates if drug in names]
        if not backing:
            changed.append(("drug", drug, drug in script_candidates))
        elif not any(names & gold_words for names in backing):
            misfiled.append(("drug", drug))

    sides = {side for term in terms if term.kind == "side" for side in term.values}
    for side in sorted(_sides(english)):
        if side not in sides:
            changed.append(("side", side, any(t.kind == "side" and side in t.values for t in ctx.terms)))
        elif side not in _sides(gold):
            misfiled.append(("side", side))

    english_actions, spanish_actions = translation.english_actions(english), translation.spanish_actions(said)
    for action in sorted(english_actions ^ spanish_actions):
        changed.append(("action", action, action in translation.spanish_actions(ctx.toks)))
    for action in sorted((english_actions & spanish_actions) - translation.english_actions(gold)):
        misfiled.append(("action", action))

    in_english = translation._english_qualified(english)
    for kind in ("negation", "certainty", "history", "experiencer"):
        if in_english[kind] != bool(ctx.qualified[kind] & set(inside)):
            changed.append((kind, "in the English only" if in_english[kind] else "in the Spanish only", False))
    english_relatives = _relatives(english)
    spanish_relatives = spanish.relatives(said)
    if not english_relatives <= {who for whos in spanish_relatives for who in whos} or any(
        not whos & english_relatives for whos in spanish_relatives
    ):
        changed.append(("relative", ", ".join(sorted(english_relatives)) or "none", False))

    for phrase in script.negated:
        if not all(negated(english, phrase)):
            changed.append(("negation_flipped", phrase, False))
    for phrase in script.affirmed:
        if any(negated(english, phrase)):
            changed.append(("negation_added", phrase, False))
    for finding, relative in script.family:
        if _contains(english, finding) and (english_relatives or spanish_relatives) and relative not in english_relatives:
            changed.append(("family", f"{finding}: {', '.join(sorted(english_relatives)) or 'no relative'}", False))
    return changed, misfiled


def _mistranslations(items: list[SpanishItem], scripts: dict[str, Script]) -> dict:
    shown, unflagged, misfiled = Counter(), Counter(), Counter()
    listed: list[dict] = []
    misfiled_listed: list[dict] = []
    for item in items:
        script = scripts[item.script]
        offered = {name: outcome for name, outcome in item.fields.items() if outcome.shown.strip()}
        if not offered:
            continue
        ctx = _reading(item.hypothesis)
        for name, outcome in offered.items():
            for statement in outcome.shown_statements:
                changed, filed = _statement_items(statement["english"], [tuple(s) for s in statement["sources"]], ctx,
                                                  script.gold[name].value, script)
                for kind, what, moved in changed:
                    shown[kind] += 1
                    unflagged[kind] += not outcome.warnings
                    listed.append({"variant": item.variant, "field": name, "kind": kind, "shown": what,
                                   "moved_from_another_passage": moved, "flagged": bool(outcome.warnings),
                                   "statement": statement["english"]})
                for kind, what in filed:
                    misfiled[kind] += 1
                    misfiled_listed.append({"variant": item.variant, "field": name, "kind": kind, "shown": what,
                                            "statement": statement["english"]})
    return {
        "shown": {kind: shown[kind] for kind in CHANGED},
        "unflagged": {kind: unflagged[kind] for kind in CHANGED},
        "unflagged_total": sum(unflagged.values()),
        "moved_from_another_passage": sum(1 for entry in listed if entry["moved_from_another_passage"]),
        "listed": listed,
        "misfiled": {"shown": {kind: misfiled[kind] for kind in MISFILED}, "total": sum(misfiled.values()),
                     "listed": misfiled_listed},
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
        ("a number, unit, drug, side, medication change, negation, certainty, history or relative changed from the "
         "Spanish it cites and shown without a warning",
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


# ---- The audio run (run_es.py) ------------------------------------------------------------------------------------


def score_audio(items: list[SpanishItem], scripts: dict[str, Script], english: dict) -> dict:
    """The audio run's measures: the recordings dictated in Spanish, the two in the wrong language, and the display
    texts alone (items whose variant ends --text). `english` holds the English scripts a wrong-language item reads."""
    text = [item for item in items if item.variant.endswith("--text")]
    wrong = [item for item in items if item.run_as]
    spoken = [item for item in items if item not in text and item not in wrong]
    shown_wrong = _wrong_language(wrong, scripts | english)
    instructions = _instructions(spoken, scripts)
    text_instructions = _instructions(text, scripts)
    return {
        "wer": _wer(spoken, scripts),
        "drugs": _drugs(spoken, scripts),
        "dates": _dates(spoken, scripts),
        **score(spoken, scripts),
        "unclear": dict(sorted(Counter(s["unclear"] for item in spoken for s in item.segments if s["unclear"]).items())),
        "live": _live(spoken),
        "wrong_language": shown_wrong,
        "text": score(text, scripts),
        # Every instruction carried into a field, in any item: FR-14 allows none.
        "instructions_shown": instructions["shown"] + text_instructions["shown"] + shown_wrong["instructions_shown"],
        "sorted_every_item": not any(item.sorting != "done" for item in spoken + text),
    }


NUMBER_LIKE = re.compile(r"(\d)\s*/\s*(\d)")


def spanish_words(text: str, *, accents: bool = True) -> list[str]:
    """The words for the word error rate: lower case, no punctuation, numbers as digits however said or written (the
    sidecar's own Spanish reading, spanish.numbers()), "142/88" as 142 sobre 88 and "%" as por ciento; with their
    accents or without."""
    text = NUMBER_LIKE.sub(r"\1 sobre \2", text).replace("%", " por ciento ")
    toks = spanish.tokens(text)
    starts = {number.first: number for number in spanish.numbers(toks)}
    out: list[str] = []
    i = 0
    while i < len(toks):
        if (number := starts.get(i)) is not None:
            out.append(max(number.readings, key=float))
            i = number.last + 1
            continue
        out.append(toks[i].text if accents else toks[i].key)
        i += 1
    return out


def _wer(items: list[SpanishItem], scripts: dict[str, Script]) -> dict:
    def wer_of(group: list[SpanishItem], accents: bool) -> float | None:
        references = [" ".join(spanish_words(scripts[i.script].display, accents=accents)) for i in group]
        if not any(references):
            return None
        hypotheses = [" ".join(spanish_words(i.hypothesis, accents=accents)) for i in group]
        return round(jiwer.wer(references, hypotheses), 4)

    tags = sorted({tag for item in items for tag in item.tags})
    return {
        "overall": wer_of(items, True),
        "no_accents": wer_of(items, False),
        "by_tag": {tag: wer_of([i for i in items if tag in i.tags], True) for tag in tags},
        "items": len(items),
    }


def _drugs(items: list[SpanishItem], scripts: dict[str, Script]) -> dict:
    exact = near = said = 0
    for item in items:
        heard = [t.key for t in spanish.tokens(item.hypothesis)]
        for drug in scripts[item.script].drugs:
            said += 1
            wanted = [t.key for t in spanish.tokens(drug)]
            spans = [" ".join(heard[i : i + len(wanted)]) for i in range(len(heard) - len(wanted) + 1)]
            target = " ".join(wanted)
            exact += target in spans
            near += any(_edits(target, span) <= 2 for span in spans)
    return {"said": said, "exact": exact, "accuracy": _ratio(exact, said), "within_2_letters": near}


def date_said(iso: str, text: str) -> bool:
    """Whether the text says the date's day and month ("3 de marzo", "tres de marzo"); the year isn't asked for."""
    month, day = list(spanish.MONTHS)[int(iso[5:7]) - 1], str(int(iso[8:10]))
    toks = spanish.tokens(text)
    for i, token in enumerate(toks):
        if token.key == month and i >= 2 and toks[i - 1].key == "de":
            before = toks[max(0, i - 6) : i - 1]
            found = [n for n in spanish.numbers(before) if n.last == len(before) - 1]
            if any(day in n.readings for n in found):
                return True
    return False


def _dates(items: list[SpanishItem], scripts: dict[str, Script]) -> dict:
    results = [date_said(iso, item.hypothesis) for item in items for iso in scripts[item.script].dates]
    return {"said": len(results), "right": sum(results), "accuracy": _ratio(sum(results), len(results))}


def _live(items: list[SpanishItem]) -> dict:
    chunks = sorted(seconds for item in items for seconds in item.chunk_seconds)
    out: dict = {"chunks": len(chunks)}
    if chunks:
        out |= {"p50": round(statistics.median(chunks), 3), "p95": round(chunks[int(0.95 * (len(chunks) - 1))], 3),
                "max": round(chunks[-1], 3)}
    stages = {stage: [i.timings[stage] for i in items if stage in i.timings] for stage in ("decode", "transcribe")}
    return out | {stage: _stats(values) for stage, values in stages.items() if values}


def _wrong_language(items: list[SpanishItem], scripts: dict) -> dict:
    """Each recording dictated in the wrong language: the share of its passages marked unclear, and why; whether any
    of its script's instructions reached a field (FR-14, from the English or the Spanish script's forbidden_en); and
    what was offered. Never its fields' content."""
    listed = []
    for item in items:
        script = scripts[item.script]
        marked = Counter(s["unclear"] for s in item.segments if s["unclear"])
        # A Spanish script's instructions in English (forbidden_en), an English script's as said (forbidden).
        phrases = script.forbidden_en if isinstance(script, Script) else script.forbidden
        shown = [phrase for outcome in item.fields.values() for phrase in phrases
                 if outcome.shown and _contains(outcome.shown, phrase)]
        listed.append({
            "variant": item.variant, "run_as": item.run_as, "passages": len(item.segments),
            "marked": dict(sorted(marked.items())), "marked_share": _ratio(sum(marked.values()), len(item.segments)),
            "sorting": item.sorting, "offered": sorted(name for name, outcome in item.fields.items() if outcome.shown),
            "instructions_shown": shown,
        })
    return {"items": listed, "instructions_shown": sum(len(entry["instructions_shown"]) for entry in listed)}
