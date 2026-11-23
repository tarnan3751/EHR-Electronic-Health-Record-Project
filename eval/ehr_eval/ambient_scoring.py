"""The measures of the eval's visits (listening to a visit, the expansion plan's step 16), from what each recording came
to (ambient.py) and each visit's script and gold (conversation_scripts.py); scoring.py's measures where a visit is
measured as a dictation is, and these besides:

Who said each word is the script's: the transcript is aligned with the script word by word (jiwer, as for the word
error rate), and each heard word paired with a word said takes the role of the turn that said it. MedASR gives no time
for a word, so the turns' words, not their times, place a short answer inside a longer passage. A word MedASR invented
has no pair, and nobody said it: it counts in the word error rate, not in attribution. A word said that MedASR dropped
has no label: in the middle of a turn it counts in the word error rate only, but one in a run of dropped words that
touches a change of speaker ("no" between two of the clinician's questions) counts against attribution too, as a word
not labelled right (missed_at_turn_change), since that is where a short answer goes missing.

- wer: word error rate overall, per tag, and by role (clinician, patient, other) overall and on the clean recordings,
  each error counted for the role of the word said (an inserted word for the word said before it).
- misheard_offered (amendment A8): each source of an offered value whose words MedASR heard differently from what
  was said there: what was heard and what said.
- attribution: of the heard words paired with a word said, and the words dropped at a change of speaker, the share
  labelled with the role that said it, the share left unsure (an Unsure passage, or a voice named "unsure"), and the
  words and the passages labelled with another role with confidence; each again over a range of each of diarize.py's
  thresholds, the others as they are, to tune them (by_margin, by_floor, by_drop); the share of each recording's heard
  words in Unsure passages (unsure_by_recording); and how often the voices told apart were as many as spoke and could be
  named.
- fields, fields_clinical, validation, unsupported_content, dates, drugs: scoring.py's. order_allergy: scoring.py's
  too, except that a retracted value or a drug no gold value carries counts as flagged only under a warning that points
  at a passage to check (FLAGS), since every Subjective value of a visit carries patients_own_words.
- safety: what the gate fails on unless the owner has accepted it (ambient_gate.py): a symptom the patient denied
  offered as present (a negation reaches only the list it heads); a diagnosis or finding hedged, denied or reported,
  by either speaker, offered as certain (hedged_as_certain), both read with the sidecar's own cues in the value as
  offered; a question's words as a finding's source (asked_as_finding: every sentence of the script that ends with a
  question mark); an offered value's source holding a word said by someone its field may not quote,
  without a speaker_unsure warning on it; any chart proposal, as the sidecar's own result for a visit has them; and an
  instruction listed as the clinician's that a patient or someone else said. Requests listed from a passage whose
  speaker couldn't be told are counted beside, not gated.
- warnings: how many offered values carry each warning.
- sorting and timing: how many recordings were drafted, left undrafted (and why) or couldn't have their voices named;
  how long decoding, telling the voices apart, transcribing and drafting took, each window, and each five-second chunk
  of a twenty-minute visit.

checked() runs the sidecar's checks over a recording's window proposals and merges them, and builds the visit's result
with the sidecar's own listening.result_json(), so the chart proposals and the instructions listed are the service's;
restored() rebuilds a results file's items from their stored answers, for rescoring (ambient.py --rescore) and for the
gate, which scores them again rather than trust the lists in the file.
"""

from __future__ import annotations

import dataclasses
import re
import statistics
from collections import Counter
from dataclasses import dataclass, field

import jiwer

from ehr_dictation import conversation, diarize, validation
from ehr_dictation.listening import result_json
from ehr_dictation.textnorm import tokens
from ehr_dictation.transcript import Segment

from . import scoring
from .normalize import normalize, words
from .scoring import FieldOutcome

ROLES = ("clinician", "patient", "other")
MARGINS = (0.0, 0.05, 0.1, 0.15, 0.2, 0.3)
FLOORS = (0.3, 0.4, 0.5, 0.6, 0.7)
DROPS = (0.05, 0.1, 0.15, 0.2, 0.3)
CODES = ("patients_own_words", "speaker_unsure", "other_speaker", "part_not_offered", "possible_correction",
         "near_unclear_passage")
FLAGS = ("possible_correction", "near_unclear_passage")
CLEAN = "noise:none"


@dataclass
class VisitItem:
    """One recording of one visit, through the pipeline."""

    variant: str
    script: str
    tags: list[str]
    seconds: float
    hypothesis: str
    sorting: str  # done, unavailable (reason says why), or unnamed (the voices couldn't be named)
    reason: str | None = None
    # MedASR's passages as heard (as Segment has them), each one's voice, whether it was unsure and what that weighed
    # (diarize.Diarization.doubts()), and the names given to the voices, so the transcript can be rebuilt exactly and
    # checked again offline.
    segments: list[dict] = field(default_factory=list)
    voices: list[int | None] = field(default_factory=list)
    unsure: list[bool] = field(default_factory=list)
    doubts: list[dict | None] = field(default_factory=list)
    names: dict[str, str] = field(default_factory=dict)
    turns: list[dict] = field(default_factory=list)  # the recording's turns: role, start, end (seconds)
    sizes: list[int] = field(default_factory=list)  # each passage's tokens, as its windows were cut by
    # Each window K2 Horizon was asked for, in order, a timed-out one's halves after it: first and last passage, start and
    # end (code points), seconds, K2 Horizon's proposal or None, and why it failed.
    windows: list[dict] = field(default_factory=list)
    fields: dict[str, FieldOutcome] = field(default_factory=dict)
    sources: dict[str, list[list]] = field(default_factory=dict)  # each offered field's sources: start, end, speaker
    unknown: list[str] = field(default_factory=list)
    not_carried_over: list[list] = field(default_factory=list)  # kind, start, end (code points), speaker
    # Whether the sidecar's visit session proposes for the chart (listening.proposes()), and the chart proposals its
    # result had: None where the run didn't say.
    proposes: bool | None = None
    proposals: int | None = None
    timings: dict[str, float] = field(default_factory=dict)
    chunk_seconds: list[float] = field(default_factory=list)


def rebuild(item: VisitItem) -> conversation.Conversation:
    """The recording's transcript, as the sidecar had it, named."""
    heard = conversation.assemble([
        (Segment(**segment), voice, doubt) for segment, voice, doubt in zip(item.segments, item.voices, item.unsure, strict=True)
    ])
    return heard.named({int(voice): role for voice, role in item.names.items()}) if item.names else heard


def checked(item: VisitItem) -> None:
    """The windows' proposals checked and merged, as the sidecar does it, into the item's fields."""
    heard = rebuild(item)
    passages = heard.passages()
    parts = []
    for window in item.windows:
        if window["proposal"] is None:
            continue
        scope = validation.Scope((window["start"], window["end"]), passages)
        parts.append((scope, validation.check(window["proposal"]["fields"], window["proposal"]["unknown"], heard, scope=scope)))
    merged = validation.merge(parts)
    proposed = [window["proposal"]["fields"] for window in item.windows if window["proposal"]]
    item.unknown = list(merged.unknown)
    item.fields = {
        name: FieldOutcome(
            proposed=" ".join(p[name]["value"] for p in proposed if p[name]["value"].strip()),
            sources=[source for p in proposed for source in p[name]["sources"]],
            status="pending" if c.pending else "empty",
            reasons=c.reasons,
            value=c.value if c.pending else "",
            warnings=[list(w) for w in c.warnings],
        )
        for name, c in merged.fields.items()
    }
    item.sources = {name: [[s, e, who] for (s, e), who in zip(c.spans, c.speakers, strict=True)]
                    for name, c in merged.fields.items() if c.pending}
    result = result_json(heard, "done", None, merged, bool(item.proposes), (), {})
    at = _code_points(heard.text)
    item.not_carried_over = [[i["reason"], at[i["start"]], at[i["end"]], i["speaker"]] for i in result["notCarriedOver"]]
    item.proposals = None if item.proposes is None else len(result["proposals"])


def faults(item: VisitItem) -> list[str]:
    """What in a stored recording isn't what its own parts come to: the transcript its passages make; each passage's
    doubt, as its stored doubts make it (diarize.unsure()); and the windows K2 Horizon was asked for, as the passages'
    token counts cut them, in turn, a window that timed out followed by its halves, every one answered if the recording
    was drafted, and nothing after the one that ended the drafting if it wasn't."""
    found = []
    if len(item.unsure) != len(item.segments) or len(item.doubts) != len(item.segments) or len(item.voices) != len(item.segments):
        return ["its passages, voices, doubts and unsure marks don't match in number"]
    heard = rebuild(item)
    if heard.text != item.hypothesis:
        found.append("its transcript isn't the one its passages make")
    if any(doubt != diarize.unsure(doubts) for doubt, doubts in zip(item.unsure, item.doubts, strict=True)):
        found.append("a passage is marked sure or unsure against what its stored doubts say")
    if not item.names:
        return found + (["it was drafted with no voices named"] if item.windows else [])
    if len(item.sizes) != len(item.segments):
        return [*found, "its passages' token counts are missing"]
    tries = [((w["first"], w["last"], w["start"], w["end"]), w["proposal"] is not None) for w in item.windows]

    def place(window: conversation.Window) -> tuple[int, int, int, int]:
        return window.first, window.last, window.start, window.end

    k, ended = 0, False
    for window in conversation.windows(heard, conversation.WINDOW_TOKENS, item.sizes):
        if k >= len(tries) or tries[k][0] != place(window):
            return [*found, "the windows it was drafted from aren't the ones its passages make"]
        k += 1
        if tries[k - 1][1]:
            continue
        # A window that failed: its halves follow if it was asked for again (a timeout, or an answer cut off), or the
        # drafting ended there.
        halves = conversation.halves(heard, window)
        if not halves or k >= len(tries) or tries[k][0] != place(halves[0]):
            ended = True
            break
        for half in halves:
            if k >= len(tries) or tries[k][0] != place(half):
                return [*found, "the windows it was drafted from aren't the ones its passages make"]
            k += 1
            if not tries[k - 1][1]:
                ended = True
                break
        if ended:
            break
    if k != len(tries) or (item.sorting == "done") == ended:
        found.append("its windows don't end as its sorting says")
    return found


def restored(results_file: dict, visits: dict) -> list[VisitItem]:
    """A results file's items, checked again from their stored window proposals with the current checks."""
    items = []
    for stored in results_file["items"]:
        item = VisitItem(**{key: value for key, value in stored.items() if key != "fields"})
        item.fields = {name: FieldOutcome(**outcome) for name, outcome in stored["fields"].items()}
        item.tags = [*item.tags[:3], *visits[item.script].tags]
        if item.sorting == "done":
            checked(item)
        items.append(item)
    return items


def _code_points(text: str) -> dict[int, int]:
    """Each UTF-16 offset in the text (the sidecar's results count in it) to its code point."""
    at, out = 0, {0: 0}
    for index, character in enumerate(text, 1):
        at += 2 if ord(character) > 0xFFFF else 1
        out[at] = index
    return out


def score(items: list[VisitItem], visits: dict) -> dict:
    drafted = [item for item in items if item.sorting == "done"]
    aligned = {item.variant: _aligned(item, visits[item.script]) for item in items}
    return {
        "wer": _wer(items, visits, aligned),
        "misheard_offered": _misheard(drafted, visits, aligned),
        "attribution": _attribution(items, aligned),
        "dates": scoring._dates(items, visits),
        "drugs": scoring._drugs(items, visits),
        "fields": scoring._fields(drafted, visits, lambda visit: visit.gold_rules),
        "fields_clinical": scoring._fields(drafted, visits, lambda visit: visit.gold),
        "validation": scoring._validation(drafted),
        "unsupported_content": scoring._unsupported(drafted),
        "order_allergy": scoring._order_allergy(_flags_only(drafted), visits),
        "safety": _safety(drafted, visits, aligned),
        "warnings": _warnings(drafted),
        "sorting": _sorting(items),
        "timing": _timing(items),
    }


def _ratio(numerator: float, denominator: float) -> float | None:
    return round(numerator / denominator, 4) if denominator else None


@dataclass
class _Aligned:
    """A recording's heard words against the words its script says (see the header)."""

    heard: list[str]
    roles: list[str]  # each word said, its turn's role
    asked: set[int]  # the words said in a question (a sentence of the script that ends with a question mark)
    to_said: dict[int, int]  # a heard word to the word said it is paired with
    passage: list[int]  # each heard word's passage
    errors: Counter  # by role: words said that were misheard or missed, and words heard that nobody said
    missed: int  # words said that MedASR dropped, in a run of dropped words that touches a change of speaker


def _aligned(item: VisitItem, visit) -> _Aligned:  # noqa: ANN001
    said, roles, asked = [], [], set()
    for turn in visit.turns:
        for sentence in re.split(r"(?<=[.!?])\s+", turn.display):
            these = words(sentence)
            if sentence.rstrip().endswith("?"):
                asked |= set(range(len(said), len(said) + len(these)))
            said += these
            roles += [turn.role] * len(these)
    heard = words(item.hypothesis)
    to_said: dict[int, int] = {}
    errors: Counter = Counter()
    missed = 0
    if said and heard:
        for chunk in jiwer.process_words(" ".join(said), " ".join(heard)).alignments[0]:
            if chunk.type in ("equal", "substitute"):
                for k in range(chunk.hyp_end_idx - chunk.hyp_start_idx):
                    to_said[chunk.hyp_start_idx + k] = chunk.ref_start_idx + k
            if chunk.type in ("substitute", "delete"):
                errors.update(roles[chunk.ref_start_idx : chunk.ref_end_idx])
            elif chunk.type == "insert":
                errors[roles[max(chunk.ref_start_idx - 1, 0)]] += chunk.hyp_end_idx - chunk.hyp_start_idx
            if chunk.type == "delete" and len(set(roles[max(chunk.ref_start_idx - 1, 0) : chunk.ref_end_idx + 1])) > 1:
                missed += chunk.ref_end_idx - chunk.ref_start_idx
    elif said:
        errors.update(roles)
    passage: list[int] = []
    for index, placed in enumerate(rebuild(item).placed):
        passage += [index] * (len(words(item.hypothesis[: placed.end])) - len(passage))
    return _Aligned(heard, roles, asked, to_said, passage, errors, missed)


def _asked(item: VisitItem, aligned: _Aligned, start: int, end: int) -> bool:
    """Whether a word heard from start to end (code points) was said in a question."""
    first, last = len(words(item.hypothesis[:start])), len(words(item.hypothesis[:end]))
    return any(aligned.to_said.get(i) in aligned.asked for i in range(first, last))


def _said_by(item: VisitItem, aligned: _Aligned, start: int, end: int) -> set[str]:
    """Who said the words heard from start to end (code points)."""
    first, last = len(words(item.hypothesis[:start])), len(words(item.hypothesis[:end]))
    return {aligned.roles[aligned.to_said[i]] for i in range(first, last) if i in aligned.to_said}


def _wer(items: list[VisitItem], visits: dict, aligned: dict[str, _Aligned]) -> dict:
    def wer(pairs: list[tuple[str, str]]) -> float | None:
        pairs = [(normalize(r), normalize(h)) for r, h in pairs if normalize(r)]
        return round(jiwer.wer([r for r, _ in pairs], [h for _, h in pairs]), 4) if pairs else None

    def by_role(group: list[VisitItem], role: str) -> float | None:
        errors = said = 0
        for item in group:
            errors += aligned[item.variant].errors[role]
            said += aligned[item.variant].roles.count(role)
        return _ratio(errors, said)

    tags = sorted({tag for item in items for tag in item.tags})
    clean = [item for item in items if CLEAN in item.tags]
    return {
        "overall": wer([(visits[i.script].display, i.hypothesis) for i in items]),
        "by_role": {role: by_role(items, role) for role in ROLES},
        "by_role_clean": {role: by_role(clean, role) for role in ROLES},
        "by_tag": {tag: wer([(visits[i.script].display, i.hypothesis) for i in items if tag in i.tags]) for tag in tags},
        "items": len(items),
    }


def _misheard(items: list[VisitItem], visits: dict, aligned: dict[str, _Aligned]) -> dict:
    found: list[dict] = []
    sources = 0
    for item in items:
        reference, heard = words(visits[item.script].display), words(item.hypothesis)
        if not reference or not heard:
            continue
        to_reference = aligned[item.variant].to_said
        for name, spans in item.sources.items():
            for start, end, _ in spans:
                sources += 1
                first, last = len(words(item.hypothesis[:start])), len(words(item.hypothesis[:end]))
                placed = [to_reference[i] for i in range(first, last) if i in to_reference]
                said = reference[min(placed) : max(placed) + 1] if placed else []
                if heard[first:last] != said:
                    found.append({"variant": item.variant, "field": name, "heard": " ".join(heard[first:last]),
                                  "said": " ".join(said)})
    return {"sources": sources, "misheard": len(found), "share": _ratio(len(found), sources), "passages": found}


def _label(item: VisitItem, index: int, thresholds: dict | None) -> str:
    """Who the passage says spoke it: its voice's name, or unsure, at the thresholds given or as the run had it."""
    voice = item.voices[index]
    name = item.names.get(str(voice), "unsure") if voice is not None else "unsure"
    doubt = item.unsure[index] if thresholds is None else diarize.unsure(item.doubts[index], **thresholds)
    return "unsure" if doubt else name


def _attribution(items: list[VisitItem], every: dict[str, _Aligned]) -> dict:
    def measure(group: list[VisitItem], thresholds: dict | None) -> dict:
        right = unsure = wrong = total = passages = wrong_passages = missed = 0
        for item in group:
            if not item.names:
                continue
            aligned = every[item.variant]
            labels = [_label(item, index, thresholds) for index in range(len(item.segments))]
            confidently_wrong = set()
            missed += aligned.missed
            total += aligned.missed
            for heard, said in aligned.to_said.items():
                total += 1
                label = labels[aligned.passage[heard]]
                if label == "unsure":
                    unsure += 1
                elif label == aligned.roles[said]:
                    right += 1
                else:
                    wrong += 1
                    confidently_wrong.add(aligned.passage[heard])
            passages += len(item.segments)
            wrong_passages += len(confidently_wrong)
        return {"accuracy": _ratio(right, total), "unsure": _ratio(unsure, total), "wrong_words": wrong,
                "wrong_share": _ratio(wrong, total), "missed_at_turn_change": missed, "words": total,
                "wrong_passages": wrong_passages, "passages": passages}

    def unsure_share(item: VisitItem) -> float | None:
        labels = [_label(item, index, None) for index in range(len(item.segments))]
        passage = every[item.variant].passage
        return _ratio(sum(labels[p] == "unsure" for p in passage), len(passage))

    tags = sorted({tag for item in items for tag in item.tags})
    return measure(items, None) | {
        "by_tag": {tag: measure([i for i in items if tag in i.tags], None) for tag in tags},
        "by_margin": {str(t): measure(items, {"margin": t}) for t in MARGINS},
        "by_floor": {str(t): measure(items, {"floor": t}) for t in FLOORS},
        "by_drop": {str(t): measure(items, {"drop": t}) for t in DROPS},
        "unsure_by_recording": {i.variant: unsure_share(i) for i in items if i.names},
        "voices_as_many_as_spoke": sum(1 for i in items if len({v for v in i.voices if v}) == len({t["role"] for t in i.turns})),
        "named": sum(1 for i in items if i.names),
    }


def _occurrences(value: str, term: str) -> list[list[int]]:
    """Each place the term, or its plural, is in the value: its word indices."""
    words = [token.text for token in tokens(value)]
    wanted = tokens(term.lower())
    stems = [w.text[:-1] if w.text.endswith("s") and not w.text.endswith("ss") else w.text for w in wanted]
    found = []
    for i in range(len(words) - len(stems) + 1):
        if all(re.fullmatch(rf"{re.escape(stem)}(?:e?s)?", words[i + k]) for k, stem in enumerate(stems)):
            found.append(list(range(i, i + len(stems))))
    return found


def _qualified(value: str, kinds: tuple) -> set[int]:
    """The value's words its own cues of these kinds reach, as the sidecar reads a conversation (validation._reach())."""
    words = tokens(value)
    return {i for qualifier in kinds for i in validation._reach(value, words, qualifier)[0]}


# A negation heads a list when what follows it, to the end of its sentence, is short items and a closing "or", "nor" or
# "and": "no fever, cough or chest pain" denies all three; "denies fever, reports chest pain" and "no cough, chest pain
# on exertion" deny one.
NEGATED_LIST = re.compile(
    r"\b(?:no|not|denies|denied|without|never|negative\s+for)\s+[\w'-]+(?:\s+[\w'-]+){0,2}(?:\s*,\s*[\w'-]+(?:\s+[\w'-]+){0,2})*"
    r",?\s+(?:or|nor|and)\s+[\w'-]+(?:\s+[\w'-]+){0,2}\s*(?=[.;!?\n]|$)",
    re.IGNORECASE,
)
# Denied after it, past a colon the sidecar's own reach stops at: "chest pain: denied".
DENIED_AFTER = re.compile(r"\s*:\s*(?:denied|none|negative|absent|no)\b", re.IGNORECASE)
_NEGATION_KINDS = tuple(q for q in validation.CONVERSATION_QUALIFIERS if q.reason == "negation_dropped")
_DOUBT_KINDS = tuple(q for q in validation.CONVERSATION_QUALIFIERS
                     if q.reason in ("negation_dropped", "certainty_dropped", "reported_dropped"))


def _offered_as_present(value: str, symptom: str) -> bool:
    """Whether the value offers the symptom as present: somewhere no negation of its own reaches it, nor a list one
    heads (the sidecar's own cues, read in the value as offered)."""
    reached = _qualified(value, _NEGATION_KINDS)
    lists = [(m.start(), m.end()) for m in NEGATED_LIST.finditer(value)]
    words = tokens(value)
    return any(not set(found) & reached and not any(a <= words[found[0]].start < b for a, b in lists)
               and not DENIED_AFTER.match(value, words[found[-1]].end)
               for found in _occurrences(value, symptom))


def _offered_as_certain(value: str, diagnosis: str) -> bool:
    """Whether the value offers the diagnosis as certain: somewhere no negation, doubt or report of its own reaches it
    (the sidecar's own cues): "Pneumonia, not a cold." offers pneumonia."""
    reached = _qualified(value, _DOUBT_KINDS)
    return any(not set(found) & reached for found in _occurrences(value, diagnosis))


def _safety(items: list[VisitItem], visits: dict, every: dict[str, _Aligned]) -> dict:
    denied: list[dict] = []
    hedged: list[dict] = []
    asked: list[dict] = []
    unwarned: list[dict] = []
    requests: list[dict] = []
    requests_unsure: list[dict] = []
    for item in items:
        visit = visits[item.script]
        heard = rebuild(item)
        aligned = every[item.variant]
        for name, outcome in item.fields.items():
            denied += [{"variant": item.variant, "field": name, "shown": normalize(symptom)}
                       for symptom in visit.denied if _offered_as_present(outcome.shown, symptom)]
            hedged += [{"variant": item.variant, "field": name, "shown": normalize(diagnosis)}
                       for diagnosis in visit.hedged if _offered_as_certain(outcome.shown, diagnosis)]
            warned = {(w[1], w[2]) for w in outcome.warnings if w[0] == "speaker_unsure"}
            asked += [{"variant": item.variant, "field": name, "shown": normalize(heard.text[start:end])}
                      for start, end, _ in item.sources.get(name, []) if _asked(item, aligned, start, end)]
            for start, end, _ in item.sources.get(name, []):
                if _said_by(item, aligned, start, end) - validation.SPEAKERS[name] and (start, end) not in warned:
                    unwarned.append({"variant": item.variant, "field": name, "shown": normalize(heard.text[start:end])})
        for _, start, end, speaker in item.not_carried_over:
            said = normalize(heard.text[start:end])
            if _said_by(item, aligned, start, end) & {"patient", "other"} or any(normalize(r) in said for r in visit.requests):
                (requests if speaker == "clinician" else requests_unsure).append(
                    {"variant": item.variant, "field": "notCarriedOver", "shown": said})
    return {
        "denied_as_present": denied,
        "hedged_as_certain": hedged,
        "asked_as_finding": asked,
        "wrong_speaker_unwarned": unwarned,
        "chart_proposals": None if any(item.proposals is None for item in items) else sum(item.proposals for item in items),
        "requests_as_instructions": requests,
        "requests_listed_unsure": requests_unsure,
    }


def _flags_only(items: list[VisitItem]) -> list[VisitItem]:
    """The items as scoring.py's order and allergy measure reads a dictation's: only the warnings that flag a value
    (FLAGS), and each instruction listed as kind, start and end."""
    return [
        dataclasses.replace(item, fields={
            name: dataclasses.replace(outcome, warnings=[w for w in outcome.warnings if w[0] in FLAGS])
            for name, outcome in item.fields.items()
        }, not_carried_over=[(kind, start, end) for kind, start, end, *_ in item.not_carried_over])
        for item in items
    ]


def _warnings(items: list[VisitItem]) -> dict:
    offered = [outcome for item in items for outcome in item.fields.values() if outcome.shown.strip()]
    return {"shown": len(offered)} | {code: sum(any(w[0] == code for w in o.warnings) for o in offered) for code in CODES}


def _sorting(items: list[VisitItem]) -> dict:
    return {"done": sum(1 for i in items if i.sorting == "done"),
            "unavailable": dict(sorted(Counter(i.reason for i in items if i.sorting != "done").items()))}


def _timing(items: list[VisitItem]) -> dict:
    def stats(values: list[float]) -> dict:
        return {"mean": round(statistics.fmean(values), 2), "max": round(max(values), 2)} if values else {}

    def summary(group: list[VisitItem]) -> dict:
        out: dict = {"items": len(group), "audio_seconds": stats([i.seconds for i in group])}
        for stage in ("decode", "diarize", "transcribe", "draft"):
            out[stage] = stats([i.timings[stage] for i in group if stage in i.timings])
        out["windows"] = stats([float(len(i.windows)) for i in group if i.windows])
        out["window_seconds"] = stats([w["seconds"] for i in group for w in i.windows])
        return out

    chunks = sorted(seconds for item in items for seconds in item.chunk_seconds)
    live: dict = {"chunks": len(chunks)}
    if chunks:
        live |= {"p50": round(statistics.median(chunks), 3), "p95": round(chunks[int(0.95 * (len(chunks) - 1))], 3),
                 "max": round(chunks[-1], 3),
                 "at_20_minutes": stats([s for item in items for s in item.chunk_seconds[-6:]])}
    return {
        "all": summary(items),
        "long": summary([i for i in items if "long" in i.tags]),
        "regular": summary([i for i in items if "long" not in i.tags]),
        "chunk_seconds": live,
    }
