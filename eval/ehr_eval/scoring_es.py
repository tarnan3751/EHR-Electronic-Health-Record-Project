"""The Spanish test set's measures (the expansion plan's step 17), from what each run of the pipeline produced and each
script's gold (dictation_scripts_es.py). Run A, the first, feeds each script's display text straight to K2 Horizon,
with no speech recognition, so it measures the translation alone (run_a.py); the measures are the same for the audio
runs to come.

- fields: whether each note field was offered when the gold has one (detection) and how much of the gold's English an
  offered value holds (content F1 over the words that carry meaning), overall and per field, as scoring.py measures
  the English set; and the share of the fields the gold has a value for that K2 proposed and the checks discarded,
  or that a script not sorted (a timeout among them) left with nothing: a failure in this measure as in every other.
- validation: of the values proposed, how many passed, and each reason the others failed (translation.py's codes).
- mistranslations: each statement of each offered value compared with what the Spanish it cites says, by the script's
  own fact list, written by hand, and read with facts_es.py, which shares no code with the checks (never translation.py,
  spanish.py or validation.py: a mistake in their reading would be measured as right), with and without a warning.
  Changed: a number, unit, drug, frequency, date, duration, route, side, severity, abbreviation or polarity the facts of
  what it cites don't say of what it says it of (a value moved to another vital sign, result or drug is changed too),
  and a medication change, negation, doubt, the past or a relative other than theirs, or left out of something it
  names (facts_es.py's header). The first five held-out scripts' fact lists are kept apart from their files, which
  stay as they were sealed (facts_of()); those five are seen now (dictation_scripts_es.SEEN), named under seen in the
  measures, with the held-out ones' own measures under held_out when a run holds both. A changed item said elsewhere in the script is marked as moved from another passage, and
  is still changed. Any changed without a warning stops the step (run_a.py says so) until the owner decides.
  Misfiled, in the owner's words of 2026-11-24: "a value its field's gold lacks and another field's gold has" (a Plan
  passage put in Subjective); a value of any kind (facts_es.VALUES) the facts back, so not changed, each listed with
  whether its field carries a warning. Not a stop at any one: its rate, the statements shown holding at least one
  without a warning over the statements shown (the coordinator's reading of "rate", with the count of values beside
  it), is capped at English's measured the same way on the same kind of text plus CAP_POINTS (2 percentage points):
  the Spanish texts' (run A, and run_es.py's texts) at the English text run's (run_a_en.py's, committed as
  eval/results/latest-en-text.json: ENGLISH_TEXT), the Spanish recordings' at the English recordings'
  (eval/results/latest.json); thresholds() and the gate (gate.py) say so. Beside the rate, share is the statements
  holding one with or without a warning. A value in the right
  field worded unlike the gold isn't misfiled (content F1 covers it), and one no field's gold has is listed apart
  (no_gold), never gated: a value the facts back, said of its item, or of nothing named where only one cited item has
  a value of its kind or the statement names the one it is (facts_es.compare()); one said of nothing named that could be
  either of two items' is changed, so a value moved between findings of one passage is a stop, never a no-gold value.
  Content F1 counts a misfiled value as well, per field: the passage's words lower precision
  where it was filed and recall where it belonged. Not everything the first scorer counted: facts_es.py reads a number
  word only before a unit, a period or "times" ("three flights" is invisible), and a drug only by the scripts' names
  (drug_names()). And any one warning on a field exempts every changed or misfiled value in it from the stop lines
  (e12's Plan always carries ambiguous_frequency).
- ambiguous_offered: every statement shown from a passage whose number reads two ways ("5.000", a fact entry that
  says refuse), with the passage as the transcript has it and whether it is written so there; the checks leave its
  field for the clinician while it is, and e16's gold for it is empty. It is compared with what was said all the same
  ("cinco mil unidades", 5000). Reported, never a stop.
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
- code_switching: every passage of each recording of a script that mixes English into Spanish (tagged code-switching),
  and of the English script dictated as Spanish, as the transcript shows it, with the probability that it was Spanish
  (Whisper's language check) and what it was marked: Spanish is forced on every passage, so English heard as Spanish
  can come out written in Spanish nobody said, and only reading these shows it. Reported, never a stop;
- no_speech_only: every passage marked low_confidence by Whisper's no-speech rule alone (P(no speech) over
  whisper_asr.NO_SPEECH, while its words' mean log-probability is not under MIN_LOG_PROBABILITY and nothing else would
  mark it), with its time span, both probabilities and the words Whisper heard; and how many such passages there were,
  and how many seconds and words of speech they left out. Whisper's own decoding applies that rule only with a low
  log-probability too, so these may be real speech the sidecar leaves out (it fails closed). Reported, never a stop;
- live: the time to answer each one-second chunk of a dictation in Spanish (the sidecar's 15-second budget, NFR-14),
  and how long Spanish passes held the one transcription lock English dictations share: its longest hold while the
  chunks came in and its hold for the final pass after stop, each recording's and the longest of all, which is how
  long an English dictation on that machine skips its live updates, and how long its stop waits, at most;
- wrong_language: the two recordings dictated in the wrong language, for what was marked and for no instruction
  carried into a field (FR-14), never for the fields' content;
- text: the measures above that don't need speech recognition, on each script's display text alone (run A's).
"""

from __future__ import annotations

import functools
import json
import re
from fractions import Fraction
import statistics
import tomllib
from pathlib import Path
from collections import Counter
from dataclasses import dataclass, field

import jiwer

from dictation_scripts import load_scripts as load_english
from dictation_scripts_es import HOLDOUT_DIR, SEEN, Script, load_scripts
from ehr_dictation import spanish, whisper_asr
from ehr_dictation.audio import SAMPLE_RATE
from ehr_dictation.prompt import FIELDS
from ehr_dictation.quality import LOW_CONFIDENCE
from ehr_dictation.transcript import Segment

from . import facts_es
from .scoring import FieldOutcome, _edits, _fields, _ratio, _stats, _validation

AMBIGUOUS_NUMBER = re.compile(r"\d[.,]\d{3}(?!\d)")  # a passage whose number reads two ways ("5.000")
STRUCTURING_SECONDS = 90.0  # the sidecar's limit (settings.py): an answer slower than this isn't offered
CHANGED = ("number", "unit", "drug", "frequency", "date", "duration", "route", "side", "severity", "abbreviation",
           "polarity", "action", "negation", "certainty", "history", "experiencer", "relative")
MISFILED = facts_es.VALUES
# Values filed in the wrong field are capped at English's rate on the same kind of text plus this (the owner's ruling,
# 2026-11-24): 2 percentage points.
CAP_POINTS = Fraction(2, 100)
# The English text run's results (run_a_en.py's, committed with --out here), whose rate the Spanish texts' is capped at:
# run A reads them through thresholds(), and the gate checks them (gate.py).
ENGLISH_TEXT = Path(__file__).resolve().parents[1] / "results" / "latest-en-text.json"
COMMITTED = object()  # thresholds()'s default: the English text run's committed results


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
    # For each passage in segments, the probabilities Whisper's verdict read: spanish, no_speech and
    # mean_log_probability (each None when it gave none); empty for a recording run as English and for a display text.
    heard: list[dict] = field(default_factory=list)
    # The live run's (answers_es.live_chunks()): the transcription lock's longest hold while the chunks came in, and
    # its hold for the final pass after stop, in seconds.
    lock_seconds: dict[str, float | None] = field(default_factory=dict)


def score(items: list[SpanishItem], scripts: dict[str, Script]) -> dict:
    measured = {
        "fields": _fields(items, scripts, lambda script: script.gold),
        "discarded": _discarded(items, scripts),
        "validation": _validation(items),
        "mistranslations": _mistranslations(items, scripts),
        "ambiguous_offered": _ambiguous_offered(items, scripts),
        "instructions": _instructions(items, scripts),
        "sorting": _sorting(items),
        "timing": _timing(items),
    }
    # The scripts of eval/scripts-es-holdout already seen (dictation_scripts_es.SEEN): named, so their measures are read
    # as seen, not held out; and, when a run holds both, the measures of the held-out ones alone.
    seen = sorted({item.script for item in items if item.script[:3] in SEEN})
    held_out = [item for item in items if scripts[item.script].path.parent == HOLDOUT_DIR and item.script[:3] not in SEEN]
    return measured | ({"seen": seen} if seen else {}) | (
        {"held_out": score(held_out, scripts)} if seen and held_out else {})


def _discarded(items: list[SpanishItem], scripts: dict[str, Script]) -> dict:
    with_gold = discarded = 0
    for item in items:
        for name in FIELDS:
            if not scripts[item.script].gold[name].value:
                continue
            with_gold += 1
            outcome = item.fields.get(name)
            discarded += item.sorting != "done" or bool(
                outcome and outcome.proposed.strip() and outcome.status != "pending")
    return {"fields_with_gold": with_gold, "discarded": discarded, "share": _ratio(discarded, with_gold)}


def _contains(text: str, phrase: str) -> bool:
    return bool(re.search(rf"(?<![\w]){re.escape(phrase.lower())}(?![\w])", text.lower()))


def negated(text: str, phrase: str) -> list[bool]:
    """For each place the phrase is in the English text, whether a negation reaches it there, as facts_es.py reads the
    English (never the checks' own reading)."""
    return facts_es.negated(text, phrase)


@functools.cache
def drug_names() -> frozenset[str]:
    """Every drug a script of either test set names, as English writes it: the drugs the reading of the English knows
    (facts_es.Lexicon)."""
    spanish_scripts = load_scripts() + load_scripts(HOLDOUT_DIR)
    return frozenset({name.lower() for script in spanish_scripts for name in script.drugs_en}
                     | {name.lower() for script in load_english() for name in script.drugs})


def _statement_items(english: str, spans: list[tuple[int, int]], hypothesis: str, gold: str, script: Script,
                     others: tuple[str, ...] = ()) -> tuple[list[tuple[str, str, bool]], list[tuple[str, str]],
                                                             list[tuple[str, str]]]:
    """What one shown statement changed from what the Spanish it cites says (kind, what, said elsewhere in the script);
    what it says that the Spanish backs, that its field's gold lacks and another field's gold (`others`) has: misfiled
    (kind, what); and what it says that the Spanish backs and no field's gold has: from the script's fact list
    (facts_of()), read with facts_es.py."""
    entries = facts_es.entries(facts_of(script))
    lexicon = facts_es.Lexicon(tuple(item for entry in entries for item in entry.items), drug_names())
    cited = [entry for entry, _ in _cited(spans, hypothesis, script)]
    changed, backed = facts_es.compare(english, cited, entries, lexicon,
                                       " ".join(hypothesis[start:end] for start, end in spans))

    def said(text: str) -> set[tuple[str, str | None, str]]:
        return {(claim.kind, claim.label, claim.value) for claim in facts_es.claims(text, lexicon)}

    in_gold, elsewhere = said(gold), set().union(*(said(other) for other in others))
    outside = [claim for claim in backed if (claim.kind, claim.label, claim.value) not in in_gold]
    misfiled = {(claim.kind, claim.value) for claim in outside if (claim.kind, claim.label, claim.value) in elsewhere}
    no_gold = {(claim.kind, claim.value) for claim in outside} - misfiled
    return changed, sorted(misfiled), sorted(no_gold)


def _cited(spans: list[tuple[int, int]], hypothesis: str,
           script: Script) -> list[tuple[facts_es.Entry, tuple[int, int]]]:
    """The fact entries whose passages a statement's spans cite, with where each is in the transcript."""
    entries = facts_es.entries(facts_of(script))
    places = _places(tuple(entry.said for entry in entries), script.display, hypothesis)
    return [(entry, place) for entry, place in zip(entries, places, strict=True)
            if place and any(place[0] < end and start < place[1] for start, end in spans)]


@functools.lru_cache(maxsize=256)
def _places(said: tuple[str, ...], display: str, hypothesis: str) -> list[tuple[int, int] | None]:
    return facts_es.places(list(said), display, hypothesis)


def _ambiguous_offered(items: list[SpanishItem], scripts: dict[str, Script]) -> dict:
    """Every statement shown from a passage whose number reads two ways (a fact entry that says refuse), with the
    passage as the transcript has it and whether it is written so there: the checks leave that field for the clinician
    while it is, so one shown then is theirs to explain."""
    listed = []
    for item in items:
        for name, outcome in item.fields.items():
            if not outcome.shown.strip():
                continue
            for statement in outcome.shown_statements:
                for entry, (start, end) in _cited([tuple(s) for s in statement["sources"]], item.hypothesis,
                                                  scripts[item.script]):
                    if entry.refuse:
                        passage = item.hypothesis[start:end]
                        listed.append({"variant": item.variant, "field": name, "statement": statement["english"],
                                       "passage": passage, "written_two_ways": bool(AMBIGUOUS_NUMBER.search(passage))})
    return {"statements": len(listed), "listed": listed}


def facts_dir(scripts_dir: Path) -> Path:
    """Where the fact lists of a folder of scripts whose own files can't change are kept: beside it, its name and
    "-facts" (scripts-es-holdout-facts for the held-out scripts, sealed before they had fact lists)."""
    return scripts_dir.parent / f"{scripts_dir.name}-facts"


@functools.cache
def _facts_kept_apart(path: Path) -> tuple[dict, ...]:
    if not path.is_file():
        return ()
    return tuple(tomllib.loads(path.read_text(encoding="utf-8")).get("facts", []))


def facts_of(script: Script) -> tuple[dict, ...]:
    """A script's fact list: its own, or, for a script whose file holds none, the one kept apart for it (facts_dir())."""
    facts = script.facts or _facts_kept_apart(facts_dir(script.path.parent) / script.path.name)
    if not facts:
        raise ValueError(f"{script.id} has no fact list: write one in its file, or in {facts_dir(script.path.parent)}")
    return facts


def _mistranslations(items: list[SpanishItem], scripts: dict[str, Script]) -> dict:
    shown, unflagged, misfiled, misfiled_unflagged = Counter(), Counter(), Counter(), Counter()
    no_gold, no_gold_unflagged = Counter(), Counter()
    listed: list[dict] = []
    misfiled_listed: list[dict] = []
    no_gold_listed: list[dict] = []
    statements = misfiled_statements = unflagged_statements = 0
    for item in items:
        script = scripts[item.script]
        offered = {name: outcome for name, outcome in item.fields.items() if outcome.shown.strip()}
        for name, outcome in offered.items():
            others = tuple(script.gold[other].value for other in FIELDS if other != name)
            for statement in outcome.shown_statements:
                changed, filed, unfiled = _statement_items(
                    statement["english"], [tuple(s) for s in statement["sources"]], item.hypothesis,
                    script.gold[name].value, script, others)
                for kind, what, moved in changed:
                    shown[kind] += 1
                    unflagged[kind] += not outcome.warnings
                    listed.append({"variant": item.variant, "field": name, "kind": kind, "shown": what,
                                   "moved_from_another_passage": moved, "flagged": bool(outcome.warnings),
                                   "statement": statement["english"]})
                statements += 1
                misfiled_statements += bool(filed)
                unflagged_statements += bool(filed) and not outcome.warnings
                for kind, what in filed:
                    misfiled[kind] += 1
                    misfiled_unflagged[kind] += not outcome.warnings
                    misfiled_listed.append({"variant": item.variant, "field": name, "kind": kind, "shown": what,
                                            "flagged": bool(outcome.warnings), "statement": statement["english"]})
                for kind, what in unfiled:
                    no_gold[kind] += 1
                    no_gold_unflagged[kind] += not outcome.warnings
                    no_gold_listed.append({"variant": item.variant, "field": name, "kind": kind, "shown": what,
                                           "flagged": bool(outcome.warnings), "statement": statement["english"]})
    return {
        "shown": {kind: shown[kind] for kind in CHANGED},
        "unflagged": {kind: unflagged[kind] for kind in CHANGED},
        "unflagged_total": sum(unflagged.values()),
        "moved_from_another_passage": sum(1 for entry in listed if entry["moved_from_another_passage"]),
        "listed": listed,
        "misfiled": {"shown": {kind: misfiled[kind] for kind in MISFILED}, "total": sum(misfiled.values()),
                     "unflagged": {kind: misfiled_unflagged[kind] for kind in MISFILED},
                     "unflagged_total": sum(misfiled_unflagged.values()),
                     "statements": statements, "misfiled_statements": misfiled_statements,
                     "share": _ratio(misfiled_statements, statements), "unflagged_statements": unflagged_statements,
                     "rate": _ratio(unflagged_statements, statements), "listed": misfiled_listed},
        "no_gold": {"shown": {kind: no_gold[kind] for kind in MISFILED}, "total": sum(no_gold.values()),
                    "unflagged_total": sum(no_gold_unflagged.values()), "listed": no_gold_listed},
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


MISFILED_LINE = ("values filed in the wrong field (its field's gold lacks them, another field's has them) shown without "
                 "a warning: the statements holding one, over the statements shown, above English's rate on the same "
                 "kind of text plus 2 percentage points")


def over_the_cap(statements: int, unflagged_statements: int, english: dict | None) -> bool:
    """Whether the share of statements holding a misfiled value shown without a warning is over English's (a misfiled
    measure of scoring.py's, on the same kind of text) plus CAP_POINTS; with no English to cap at, it is (fail closed)."""
    if english is None or not english.get("statements"):
        return True
    rate = Fraction(unflagged_statements, statements) if statements else Fraction(0)
    return rate > Fraction(english["unflagged_statements"], english["statements"]) + CAP_POINTS


def capped(misfiled: dict, english: dict | None, statements: int | None = None,
           unflagged_statements: int | None = None) -> str:
    """The misfiled line's measures, both languages': statements and values, rates and the cap."""
    statements = misfiled["statements"] if statements is None else statements
    unflagged = misfiled["unflagged_statements"] if unflagged_statements is None else unflagged_statements
    spanish = (f"Spanish {unflagged} of {statements} statements ({_ratio(unflagged, statements)}), "
               f"{misfiled['unflagged_total']} value(s) without a warning")
    if english is None or not english.get("statements"):
        return f"{spanish}; no English results of the same kind to cap at"
    cap = Fraction(english["unflagged_statements"], english["statements"]) + CAP_POINTS
    return (f"{spanish}; English {english['unflagged_statements']} of {english['statements']} ({english['rate']}), "
            f"{english['unflagged_total']} value(s); cap {round(float(cap), 4)}")


def english_misfiled(path: Path = ENGLISH_TEXT) -> dict | None:
    """The misfiled measure (scoring.py's) an English results file states, or None if there is none, or it was scored
    before the cap (no unflagged_statements)."""
    if not path.is_file():
        return None
    misfiled = json.loads(path.read_text(encoding="utf-8")).get("metrics", {}).get("misfiled")
    return misfiled if isinstance(misfiled, dict) and "unflagged_statements" in misfiled else None


def thresholds(metrics: dict, english: object = COMMITTED) -> list[tuple[str, bool, str]]:
    """Each stop threshold, whether it was crossed, and what was measured. Any crossed stops the step for the owner.
    `english` is the English misfiled measure (scoring.py's) of the same kind of text, the misfiled line's cap: by
    default the English text run's, as committed (ENGLISH_TEXT), which is run A's."""
    english = english_misfiled() if english is COMMITTED else english
    content_f1 = metrics["fields"]["content"]["f1"]
    discarded = metrics["discarded"]["share"]
    sorting = metrics["sorting"]
    unflagged = metrics["mistranslations"]["unflagged"]
    misfiled = metrics["mistranslations"]["misfiled"]
    return [
        ("a number, unit, drug, frequency, date, duration, route, side, severity, abbreviation, polarity, medication "
         "change, negation, certainty, history or relative changed from what the Spanish it cites says (the script's "
         "fact list) and shown without a warning",
         metrics["mistranslations"]["unflagged_total"] > 0,
         ", ".join(f"{kind} {count}" for kind, count in unflagged.items() if count) or "none"),
        (MISFILED_LINE, over_the_cap(misfiled["statements"], misfiled["unflagged_statements"], english),
         capped(misfiled, english)),
        ("an instruction to order something or change the allergy list carried into a field",
         metrics["instructions"]["shown"] > 0, f"{metrics['instructions']['shown']} shown"),
        (f"English content F1 below {F1_FLOOR}", content_f1 is None or content_f1 < F1_FLOOR, f"F1 {content_f1}"),
        ("more than a third of the fields with gold discarded by the checks, or left empty by a script not sorted",
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
        "code_switching": _code_switching(spoken + [item for item in wrong if item.run_as == "es"], scripts),
        "no_speech_only": _no_speech_only(spoken + [item for item in wrong if item.run_as == "es"]),
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
        # A token of digits glued to letters ("1C" in "la 1C", A1C heard) is a word, not a number: the checks' reader
        # gives it as a reading of its own, which no float reads.
        number = starts.get(i)
        values = [reading for reading in number.readings if _is_float(reading)] if number else []
        if values:
            out.append(max(values, key=float))
            i = number.last + 1
            continue
        out.append(toks[i].text if accents else toks[i].key)
        i += 1
    return out


def _is_float(text: str) -> bool:
    try:
        float(text)
    except ValueError:
        return False
    return True


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
    held = [{"variant": item.variant, "longest_live_hold": _rounded(item.lock_seconds.get("longest_live")),
             "final_hold": _rounded(item.lock_seconds.get("final"))} for item in items if item.lock_seconds]
    holds = [hold for entry in held for hold in (entry["longest_live_hold"], entry["final_hold"]) if hold is not None]
    return out | {stage: _stats(values) for stage, values in stages.items() if values} | {
        "lock": {"items": held, "longest_hold": max(holds, default=None)}}


def _rounded(value: float | None, places: int = 4) -> float | None:
    return None if value is None else round(value, places)


def _no_speech_only(items: list[SpanishItem]) -> dict:
    """Each passage the no-speech rule alone marked (the module's header), recording by recording, and the totals."""
    listed = []
    for item in items:
        found = []
        for segment, heard in zip(item.segments, item.heard or [{}] * len(item.segments), strict=True):
            no_speech, mean = heard.get("no_speech"), heard.get("mean_log_probability")
            if segment["unclear"] != LOW_CONFIDENCE or no_speech is None or mean is None or not segment["raw"].strip():
                continue
            # Marked, with no speech likely; and nothing else would have marked it.
            if no_speech > whisper_asr.NO_SPEECH and whisper_asr.verdict(segment["raw"], heard["spanish"], 0.0, mean) is None:
                found.append({"start": round(segment["start_sample"] / SAMPLE_RATE, 2),
                              "end": round(segment["end_sample"] / SAMPLE_RATE, 2), "no_speech": _rounded(no_speech),
                              "mean_log_probability": _rounded(mean), "text": Segment(**segment).text})
        if found:
            listed.append({"variant": item.variant, "passages": found})
    passages = [passage for entry in listed for passage in entry["passages"]]
    return {"passages": len(passages), "seconds": round(sum(p["end"] - p["start"] for p in passages), 2),
            "words": sum(len(p["text"].split()) for p in passages), "items": listed}


def _code_switching(items: list[SpanishItem], scripts: dict[str, Script]) -> dict:
    """Every passage of each recording of a code-switching script, and of the English script dictated as Spanish (the
    module's header)."""
    listed = []
    for item in items:
        if item.run_as != "es" and "code-switching" not in scripts[item.script].tags:
            continue
        heard = item.heard or [{}] * len(item.segments)
        listed.append({"variant": item.variant, "passages": [
            {"text": Segment(**segment).text, "spanish": _rounded(probabilities.get("spanish")),
             "unclear": segment["unclear"]}
            for segment, probabilities in zip(item.segments, heard, strict=True)]})
    return {"items": listed, "passages": sum(len(entry["passages"]) for entry in listed)}


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
