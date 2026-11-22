"""Checks every value K2 Horizon proposes before a clinician can see it (FR-12). A value that fails any check is never
shown and its field is left empty; only the reason codes go out, never the text. A value that passes is held as pending
until a clinician approves it (FR-13), with the positions in the transcript of the passages it came from (FR-08).

Grounding is strict: every word of a value must be in that value's own sources, compared after the shared reading
(case, punctuation, numbers as digits whether written or spoken, a few unit spellings, and plural endings), except for
the articles (FREE_WORDS). Nothing else may be added: not "twice", not "before" for "after", not "right" for "left", not
"and" between two findings, not another drug, not a heading. Nor may words be recombined: the value is cut into
segments at punctuation (not the point in "12.5"), and each segment's words must be found in order in one of its
sources, with at most MAX_GAP of that source's own words between any two of them (articles and doses don't count), and
none at all before a number but an article or a unit: a number stays with the word it follows. So "right knee pain"
can't come from "left knee pain, right knee normal", nor "continue lisinopril 500 mg" from "continue lisinopril 10 mg
and metformin 500 mg". A source with a correction in it is read as corrected.

An instruction to order something or to change the allergy list is never carried into the note (FR-14), but the rest of
a field is: each sentence of a value holding one is cut where the instruction starts, and a source quoting one counts
only for its words outside it. Separately, not_carried_over() lists every such instruction in the transcript, with its
kind and where the clinician said it, so the review screen can say what still has to be placed in Orders or the
allergy list.

Some of those the dictation proposes (the expansion plan's step 15; FR-14 as reworded): an instruction to place an
order, or to add an allergy, said in one plain form, which the clinician may open in the order form or the allergy
form, which fill in nothing from it. proposals() is the one place they are made; a session that must propose none
doesn't ask (sessions.py). An instruction is proposed only when it is said plainly (_plain()), and everything else is
held back, still listed as not carried over. Said plainly is the clinician's own direct instruction, all of its
sentence accounted for:
- it starts its sentence, or follows "and", "so" or "then" (a comma before it or not) after a statement of the plan
  ("Continue omeprazole and refill the omeprazole", "..., so please add allopurinol to his allergy list"), and that
  statement doesn't open with a condition (if, unless, when, once, should, assuming, provided, pending, depending,
  after, before, until, whether, in case, in the event) nor holds one anywhere (if, unless, whether, assuming,
  provided, pending, depending), nor a hedge, someone asking (asked, requested, wants, insisted, suggested), someone
  other than the patient, or words to the software;
- before its verb comes nothing but a closed lead-in: okay, ok, alright, so, and, also, then, now, next, finally,
  lastly, "plan:", please, "go ahead and", and the clinician's own intent (I'll, I will, I'm going to, I am going to,
  we'll, we will, we're going to, we are going to, let's, let us, I'm, I am, we're, we are), then "also" or "just";
  an allergy to add may name its substance first ("penicillin should be added to her allergies");
- from its verb to the end of the sentence, no comma (but in a number), semicolon, colon, dash, bracket or quote,
  and none of: someone other than the patient (a relative, a nurse, a doctor, "the patient", a room, Mr, Mrs, Ms, an
  -ologist), a hedge (probably, perhaps, possibly, maybe, likely, might, could, would, may), a condition (as above,
  but "before" or "after" a meal or bedtime), "or", "either", "nor", "instead", "as needed", "prn", a negation, a
  subject or a word of telling (I, we, you, she, he, they; said, asked, told, insisted, suggested, wants, requested,
  mentioned, thinks), a reflexive, "any", "something", "whatever", "that" (but "call that in"), a note or a request,
  words to the software, or another step of the plan (start, continue, stop, remove, update, take...); only a reason
  after "because" may have its own subject ("because he has felt run down");
- an allergy to add names a substance of one to five words, not a note, a reaction or "that", on her, his, their or
  the list ("your" list, said to the software, is no patient's);
- it doesn't come right after a sentence of a few words that is a bare negation or doubt ("No.", "Not today.", "I'm
  not sure.").
Every rule below holds it back too, a second layer, and weigh() names the first that applies (HELD_BACK): not_final (the transcript isn't final yet); to_the_software (its
sentence tells the software to ignore, disregard or forget its rules, instructions or directions, calls on it
("Assistant, ...", "Dear model, ..."), or is a note to the assistant, the AI or the system); mixed_kinds (an order and an allergy change run together); not_an_addition (an orders section, or an allergy
change other than adding one: removing, taking off, clearing, updating, changing, marking); negated (a negation before
it in its clause: "I don't think we need to order"); hedged (if, unless, when, once, consider, maybe, perhaps,
probably, might, could, would, discuss, think, whether, or "may" that isn't a date, before it in its clause; a sentence
opening with a condition; or if, unless, in case, once, when or depending on after it); for_later (its sentence says
next visit, week, month, year or season, at the follow-up, later, eventually, in the future, down the line, or in so
many days, weeks, months or years); taken_back (a correction cue, or hold off, never mind, forget that, cancel that,
on second thought, let's not or not yet; ignore, disregard, delete, strike, undo, skip, cancel, scratch, drop,
retract or rescind; hold or forget that or it; take that back; wait, no; declined or refused; don't need it; no longer
needed; changed my mind: from its start to the end of the next sentence, within TAKEN_BACK_REACH words after it); in_the_past (a word of the past in its clause, as for the pharmacy: already, last week, ago); unclear (it
overlaps a passage marked unclear); question (its sentence ends in a question mark); quoted (the clause before it ends
in a word of telling: "she asked, can you order an MRI"); not_imperative (anything but an opener, from the start of
its clause or the last "and" or word starting a clause, up to its lead-in or subject: "capillary refill under 2
seconds"); not_plain (not said plainly, as above); too_many (past MAX_PROPOSALS). A proposal is its kind (order or allergy_addition), the place of its listed
instruction, and its warnings, never words: possible_correction, pointing from a correction-like word said after the
instruction, in its words or within WARNING_REACH words after them; and near_unclear_passage, as for a value.

Every value and source is first NFKC-normalized with Unicode format characters (such as a zero-width space) removed,
and every check reads the cleaned text. The checks, each with the reason code recorded when it fails:

- unknown_field: a key outside subjective, objective, assessment and plan (the schema allows none; this is the
  backstop), such as the history of the three fields before the expansion plan's step 14.
- not_dictated: an empty value. Not a failure: the transcript said nothing for that field.
- too_long: over the field's length limit (LIMITS), over 30 sources, or a source over 600 characters.
- bad_format: control or format characters, characters NFKC would change, a non-ASCII letter the transcript never uses
  (a look-alike from another alphabet), markup, a link, code or JSON.
- order_or_allergy_change (FR-14): nothing was left of the value once its instructions were cut, or nothing was left of
  its sources once the instructions they quote were cut out of them. An instruction says to place an order, or that one
  is being placed now ("I'm ordering"), to write, send, refill or renew a prescription, to call in anything ("call in
  something for her nausea", not "call in sick"), to send something to the pharmacy (anywhere on in the sentence: "send
  amoxicillin 500 mg by mouth three times a day for 7 days to her pharmacy"), to make an orders section, or to change
  the allergy list (add, remove, record, clear, update), whoever's list it is called ("her", "his", "their", "my",
  "your": "I'll add penicillin to your allergies", as said to the patient); hyphens are ignored ("re-fill"). A word
  before the verb makes it a noun only right before it ("no order", "the lab order"): after a comma or a full stop the
  verb starts an instruction ("No, order a chest X-ray", "Check the lab. Order a CBC"). A plan stays a plan:
  "start", "continue", "increase" or "stop" a drug, orders in the past or by someone else ("her cardiologist ordered an
  echo last year"), and an instruction not to ("don't order an A1c", "no need to order labs"), pass.
- no_sources, source_too_short (under two words, without a number), source_not_in_transcript (compared word by word,
  ignoring case and punctuation), source_in_unclear_passage.
- source_superseded: the value uses what the clinician corrected ("sorry", "no sorry", "no correction", "I mean",
  "actually", "no,", "make it", "rather", "scratch that"), read across the whole transcript. A correction gives a
  number, date, drug name or side (left, right) within three words of its cue, and supersedes the last expression of the
  same kind said before the cue in its sentence, unless it says all of it again. A cue that starts a sentence looks back
  up to three sentences for the nearest one, in the same unit if the correction gives one ("10 mg daily. Recheck in 2
  weeks. Actually, 20 mg" corrects the 10 mg). One expression is one measured thing: numbers joined only by units,
  articles or "over" ("150 over 90"), with the one word after it when that is all that comes before the cue ("95
  degrees, no sorry, 90 degrees"). So "heart rate 78, no sorry 72" supersedes 78 and not the blood pressure said before
  it; "10 mg, sorry, 20 mg" supersedes 10 mg; and "lisinopril 10 mg, sorry, 20 mg" keeps lisinopril, since no drug
  replaces it. A source is read as corrected: without the cue and what it took back.
- number_not_in_sources, date_not_in_sources, drug_not_in_sources: the specific cases of a word not in the sources, kept
  for what they say: a number, a date word ("may" counts as one after a preposition or before a day), a drug-like name.
- negation_dropped, experiencer_dropped, certainty_dropped, history_dropped: in the transcript, a qualifier reaches
  words of the value's sources that the value uses without it, even where another source has them plain: a negation ("no
  chest pain" as "chest pain"), someone other than the patient having it ("family history of breast cancer", "mother had
  a stroke", "diabetes in her father", "seen with her mother, who has diabetes"), uncertainty ("possible pneumonia",
  "rule out appendicitis", "pneumonia suspected"), or the past ("a history of MI in 2019"). A cue reaches six words on
  (four back, for one that follows what it qualifies, such as "denied"), and stops at its clause: punctuation, a
  contrast ("but"), a new subject ("she", "patient"), a step of the plan or a heading after its first word ("no further
  pancreatitis continue monthly olezarsen"), and for someone else, a word of telling ("mom says the rash started
  Monday" is the patient's rash).
- words_out_of_order: the value's words are all in its sources, but not in order within one of them.
- unsupported_content: any other word of the value isn't in its sources.

A value that passes can still carry warnings, each a code and the place in the transcript it points at, never text; the
value stays pending, and the review screen shows them beside it. possible_correction, for each place where a correction
may have been said that the checks didn't hear, such as a cue speech recognition got wrong ("10 mg no weight 20 mg"):
the value uses a number, date or drug name from its sources, and the transcript says another of the same kind, in
another expression, within WARNING_REACH words after it, past the end of the source if need be, with a correction-like
word between them (no, wait, sorry, scratch, mean, correction, actually, rather, make, change); it points at the later
item. near_unclear_passage, for each passage marked unclear within NEAR_UNCLEAR words of one of the value's sources: the
value was sorted from words right beside something speech recognition couldn't hear; it points at that passage.
one_field_holds_dictation, on the only value offered, whatever became of the other fields (not dictated, or proposed and
discarded), if its sources cover at least WHOLE_DICTATION of the transcript's words: the whole dictation, whatever it
says, has gone into one field, plan and findings alike, to be sorted into the sections it belongs to before it is
accepted; it points at what the sources cover. A short dictation that is all one field, such as an addendum to a plan, covers less once its lead-in and sign-off
are left out.

The eval (eval/) measures what these let through and what they throw away; its gate ties results to a hash of these
checks, so a change to any rule asks for the stored answers to be scored again (eval/ehr_eval/rescore.py).
"""

from __future__ import annotations

import bisect
import re
import string
import unicodedata
from dataclasses import dataclass, field

from .prompt import FIELDS
from .textnorm import ORDINAL_TENS, ORDINAL_UNITS, STOPWORDS, TENS, UNITS, Token, numbers, stem, tokens
from .transcript import Transcript, utf16_offset

# As the schema says (a test checks): a field can hold all a three-minute dictation says, which is about 2,500
# characters, and a source per sentence of it.
LIMITS = {"subjective": 3000, "objective": 3000, "assessment": 2000, "plan": 2000}
MAX_SOURCES = 30
MAX_QUOTE = 600
CORRECTION_WINDOW = 3  # words after a correction cue within which the corrected value starts
# The possible-correction warning's rule, in one place: how far after a value's number, date or drug another of its
# kind may be a correction, and the words one of which must come between them (None: any gap).
WARNING_REACH = 8
NEAR_UNCLEAR = 5  # words between a source and an unclear passage that put a near_unclear_passage warning on the value
WHOLE_DICTATION = 0.9  # the share of the transcript's words the only value offered may quote before it is warned of
WARNING_GAP_WORDS: frozenset[str] | None = frozenset(
    "no wait sorry scratch mean correction actually rather make change".split()
)
NEGATION_SCOPE = 6  # words after a negation, experiencer, certainty or history cue, within its clause
POST_SCOPE = 4  # words before a cue that qualifies what precedes it ("chest pain denied")
DIRECTIVE_REACH = 14  # at most, words after an instruction that still belong to it, when nothing ends its clause sooner
UNDOES_REACH = 3  # words before an instruction not to within which another negation makes it an instruction again
LEAD_IN_REACH = 5  # words before an instruction that can be its lead-in ("go ahead and"), or its subject ("penicillin
# should be added to her allergies")
MAX_GAP = 1  # a source's own words allowed between two of a value's words (articles and doses are free)

REASONS = (
    "unknown_field",
    "not_dictated",
    "too_long",
    "bad_format",
    "order_or_allergy_change",
    "no_sources",
    "source_too_short",
    "source_not_in_transcript",
    "source_in_unclear_passage",
    "source_superseded",
    "number_not_in_sources",
    "date_not_in_sources",
    "drug_not_in_sources",
    "negation_dropped",
    "experiencer_dropped",
    "certainty_dropped",
    "history_dropped",
    "words_out_of_order",
    "unsupported_content",
)

# The only words a value may add to its sources: the articles. Adding or leaving one out never changes what a sentence
# says ("cough for three days", "a cough for three days"). Nothing else is free, since any other short word can: a time
# (before, after, since, until), a place or side (left, right, over, under), a quantity (more, all, twice, half), a
# negation, "with" or "without", or "and" and "or", which can join a finding to another's negation ("no cough and
# fever") or turn a choice into both ("ibuprofen and acetaminophen").
FREE_WORDS = frozenset({"a", "an", "the"})
# Skipped freely between a value's words, like the articles: a value may leave out a dose ("takes lisinopril daily"
# from "takes lisinopril 10 mg daily").
DOSE_WORDS = frozenset({"mg", "mcg", "ml", "kg", "g", "unit", "percent"})
NUMBER_WORDS = frozenset(UNITS) | frozenset(TENS) | frozenset(ORDINAL_UNITS) | frozenset(ORDINAL_TENS) | {
    "hundred", "thousand"}
UNIT_SPELLINGS = {
    "milligram": "mg", "milligrams": "mg", "microgram": "mcg", "micrograms": "mcg", "milliliter": "ml",
    "milliliters": "ml", "millilitre": "ml", "millilitres": "ml", "kilogram": "kg", "kilograms": "kg", "kilo": "kg",
    "kilos": "kg", "gram": "g", "grams": "g",
}
# Characters a value may use whether or not the transcript does: ASCII letters, digits and punctuation. Any other letter
# must be one the transcript uses, so a look-alike from another alphabet stands out.
NEUTRAL_CHARACTERS = frozenset(string.ascii_lowercase + string.digits + string.whitespace + ".,;:!?'\"()-/%+&’‘“”–—")


@dataclass
class Checked:
    name: str
    value: str
    quotes: list[str]
    reasons: list[str]
    spans: list[tuple[int, int]] = field(default_factory=list)  # code points in the transcript, for pending values
    # For pending values (see the module's header): each warning's code, and the place in the transcript it points at.
    warnings: list[tuple[str, int, int]] = field(default_factory=list)

    @property
    def pending(self) -> bool:
        return not self.reasons

    def to_json(self, transcript: Transcript) -> dict:
        if not self.pending:
            return {"status": "empty", "reasons": self.reasons}
        return {
            "status": "pending",
            "value": self.value,
            "sources": [
                {"start": utf16_offset(transcript.text, start), "end": utf16_offset(transcript.text, end)}
                for start, end in self.spans
            ],
            "warnings": [
                {"code": code, "start": utf16_offset(transcript.text, start), "end": utf16_offset(transcript.text, end)}
                for code, start, end in self.warnings
            ],
        }


@dataclass
class Checks:
    fields: dict[str, Checked]
    unknown: list[str]

    def to_json(self, transcript: Transcript) -> dict:
        return {name: checked.to_json(transcript) for name, checked in self.fields.items()}


def clean(text: str) -> str:
    """NFKC, without Unicode format characters (zero-width spaces and the like)."""
    return "".join(c for c in unicodedata.normalize("NFKC", text) if unicodedata.category(c) != "Cf")


@dataclass(frozen=True)
class _Term:
    """A number, date word, drug name or side in the transcript, and the words it takes up (inclusive)."""

    kind: str
    text: str
    first: int
    last: int


@dataclass(frozen=True)
class _Transcript:
    text: str
    words: list[Token]
    unclear: list[tuple[int, int]]
    directives: list[tuple[int, int]]
    letters: frozenset[str]
    terms: list[_Term]
    cues: set[int]  # word indices
    taken_back: set[int]
    superseded: set[_Term]
    qualified: dict[str, set[int]]  # by reason code: the words each kind of qualifier reaches
    expressions: dict[_Term, int]  # which expression each term belongs to


def not_carried_over(transcript: Transcript) -> list[dict]:
    """Each instruction in the transcript to order something or change the allergy list, which the note never carries:
    its kind (order or allergy_change) and where the clinician said it, as UTF-16 offsets into the transcript."""
    return [
        {"reason": kind, "start": utf16_offset(transcript.text, start), "end": utf16_offset(transcript.text, end)}
        for start, end, kind in directives(transcript.text)
    ]


def check(fields: dict[str, dict], unknown: list[str], transcript: Transcript) -> Checks:
    words = tokens(transcript.text)
    terms = _terms(words)
    cues, taken_back, superseded = _corrections(transcript.text, words, terms)
    context = _Transcript(
        text=transcript.text,
        words=words,
        unclear=transcript.unclear_ranges(),
        directives=directive_spans(transcript.text),
        letters=frozenset(clean(transcript.text).casefold()),
        terms=terms,
        cues=cues,
        taken_back=taken_back,
        superseded=superseded,
        qualified=_qualifiers(transcript.text, words),
        expressions=_expressions(terms, words),
    )
    checked = {name: _check_field(name, fields[name]["value"], fields[name]["sources"], context) for name in FIELDS}
    _warn_of_whole_dictation(checked, context)
    return Checks(checked, list(unknown))


def _warn_of_whole_dictation(checked: dict[str, Checked], transcript: _Transcript) -> None:
    """one_field_holds_dictation (see the module's header), on the only value offered, whatever became of the others,
    when its sources cover at least WHOLE_DICTATION of the transcript's words."""
    offered = [field for field in checked.values() if field.pending]
    if len(offered) != 1:
        return
    field = offered[0]
    covered = sum(any(start <= word.start and word.end <= end for start, end in field.spans) for word in transcript.words)
    if transcript.words and covered >= WHOLE_DICTATION * len(transcript.words):
        field.warnings.append((ONE_FIELD_HOLDS_DICTATION, min(s for s, _ in field.spans), max(e for _, e in field.spans)))


def _check_field(name: str, raw_value: str, raw_quotes: list[str], transcript: _Transcript) -> Checked:
    raw_value = raw_value.strip()
    value = clean(raw_value)
    quotes = [clean(quote).strip() for quote in raw_quotes if clean(quote).strip()]
    if not value:
        return Checked(name, value, quotes, ["not_dictated"])

    reasons: set[str] = set()
    if len(value) > LIMITS[name] or len(quotes) > MAX_SOURCES or any(len(quote) > MAX_QUOTE for quote in quotes):
        reasons.add("too_long")
    if (
        value != raw_value
        or BAD_FORMAT.search(value)
        or any(c not in transcript.letters and c not in NEUTRAL_CHARACTERS for c in value.casefold())
    ):
        reasons.add("bad_format")
    value, _ = _without_instructions(value)
    if not value:
        return Checked(name, value, quotes, [r for r in REASONS if r in reasons | {"order_or_allergy_change"}])
    if not quotes:
        reasons.add("no_sources")

    spans: list[tuple[int, int]] = []
    kept: list[str] = []
    for quote in quotes:
        quote_words = tokens(quote)
        if UNCLEAR_QUOTE.search(quote):
            reasons.add("source_in_unclear_passage")
            continue
        if len(quote_words) < 2 and not any(word.text[0].isdigit() for word in quote_words):
            reasons.add("source_too_short")
            continue
        found = _locate(quote_words, transcript, spans)
        if isinstance(found, str):
            reasons.add(found)
            continue
        # Only the part of a source outside any instruction counts: whatever the value took from the instruction is then
        # ungrounded.
        for piece in _outside_instructions(found, transcript):
            spans.append(piece)
            kept.append(transcript.text[piece[0] : piece[1]])
    if quotes and not kept and not reasons & {"source_too_short", "source_not_in_transcript", "source_in_unclear_passage"}:
        reasons.add("order_or_allergy_change")
    quotes = kept

    sources = " ".join(quotes)
    value_numbers = set(numbers(value))
    inside = [term for term in transcript.terms if any(_holds(span, term, transcript.words) for span in spans)]
    superseded = {(term.kind, term.text) for term in inside if term in transcript.superseded}
    said = {(term.kind, term.text) for term in inside if term.first not in transcript.taken_back}
    if _value_terms(value) & (superseded - said):
        reasons.add("source_superseded")
    if not value_numbers <= set(numbers(sources)):
        reasons.add("number_not_in_sources")
    if not _date_terms(value) <= _date_terms(sources):
        reasons.add("date_not_in_sources")
    if not _drug_terms(value) <= {token.text for token in tokens(sources)}:
        reasons.add("drug_not_in_sources")
    reasons |= _dropped(value, spans, transcript)
    if _ungrounded(value, sources):
        reasons.add("unsupported_content")
    elif quotes and _recombined(value, [_reading(span, transcript) for span in spans]):
        reasons.add("words_out_of_order")

    ordered = [reason for reason in REASONS if reason in reasons]
    if ordered:
        return Checked(name, value, quotes, ordered)
    warnings = [(POSSIBLE_CORRECTION, start, end) for start, end in _possible_corrections(value, spans, transcript)]
    warnings += [(NEAR_UNCLEAR_PASSAGE, start, end) for start, end in _near_unclear(spans, transcript.unclear, transcript.words)]
    return Checked(name, value, quotes, ordered, spans, warnings)


# ---- Quotes --------------------------------------------------------------------------------------------------------

UNCLEAR_QUOTE = re.compile(r"\[\s*unclear\s*\]", re.IGNORECASE)


def _locate(quote: list[Token], transcript: _Transcript, earlier: list[tuple[int, int]]) -> tuple[int, int] | str:
    """The quote's place in the transcript, word for word, outside unclear passages. A passage said more than once
    goes to the first occurrence after the field's previous source that no earlier source took, so each is highlighted
    where it was said (FR-08)."""
    size, words = len(quote), transcript.words
    wanted = [token.text for token in quote]
    found = [
        (words[i].start, words[i + size - 1].end)
        for i in range(len(words) - size + 1)
        if [token.text for token in words[i : i + size]] == wanted
    ]
    if not found:
        return "source_not_in_transcript"
    clear = [span for span in found if not any(_overlaps(span, passage) for passage in transcript.unclear)]
    if not clear:
        return "source_in_unclear_passage"
    unused = [span for span in clear if span not in earlier]
    after = earlier[-1][1] if earlier else 0
    return next((span for span in unused if span[0] >= after), unused[0] if unused else clear[0])


def _outside_instructions(span: tuple[int, int], transcript: _Transcript) -> list[tuple[int, int]]:
    """The runs of a span's words that are outside every instruction, each a span of its own."""
    pieces: list[tuple[int, int]] = []
    run: list[Token] = []
    for word in transcript.words:
        if not (span[0] <= word.start and word.end <= span[1]):
            continue
        if any(_overlaps((word.start, word.end), directive) for directive in transcript.directives):
            if run:
                pieces.append((run[0].start, run[-1].end))
            run = []
        else:
            run.append(word)
    if run:
        pieces.append((run[0].start, run[-1].end))
    return pieces


def _overlaps(a: tuple[int, int], b: tuple[int, int]) -> bool:
    return a[0] < b[1] and b[0] < a[1]


# ---- Corrections --------------------------------------------------------------------------------------------------

CORRECTION = re.compile(
    r"\b(?:no sorry|no wait|no correction|wait no|sorry|i mean|i meant|correction|scratch that|strike that|make that|make it|"
    r"i misspoke|actually|rather)\b|\bno\s*,",
    re.IGNORECASE,
)
KINDS = ("number", "date", "drug", "side")
SENTENCES_BACK = 3  # how far back a correction that starts a sentence looks for what it corrects
# Units that tell one measured thing from another, as read (_word_key).
MEASURES = DOSE_WORDS | frozenset(
    "day week month year hour minute second degree cm mm mmhg liter tablet puff drop pound step beat".split()
)
WARNED_KINDS = ("number", "date", "drug")
POSSIBLE_CORRECTION = "possible_correction"
NEAR_UNCLEAR_PASSAGE = "near_unclear_passage"
ONE_FIELD_HOLDS_DICTATION = "one_field_holds_dictation"
SIDES = frozenset({"left", "right"})
# What can sit inside one expression of a kind: "150 over 90", "10 mg", "the 20".
RUN_GLUE = FREE_WORDS | DOSE_WORDS | {"over"}


def _terms(words: list[Token]) -> list[_Term]:
    """Every number, date word, drug name and side in the words, in order."""
    texts = [word.text for word in words]
    dates = set(_date_words(texts))
    found: list[_Term] = []
    i = 0
    while i < len(texts):
        word = texts[i]
        if word in NUMBER_WORDS:
            j = i
            while j + 1 < len(texts) and (
                texts[j + 1] in NUMBER_WORDS or (texts[j + 1] == "and" and texts[j] in ("hundred", "thousand"))
            ):
                j += 1
            found += [_Term("number", number, i, j) for number in numbers(" ".join(texts[i : j + 1]))]
            i = j + 1
            continue
        if word[0].isdigit():
            found += [_Term("number", number, i, i) for number in numbers(word)]
        elif i in dates:
            found.append(_Term("date", word, i, i))
        elif _is_drug(word):
            found.append(_Term("drug", word, i, i))
        elif word in SIDES:
            found.append(_Term("side", word, i, i))
        i += 1
    return found


def _value_terms(value: str) -> set[tuple[str, str]]:
    found = {("number", number) for number in numbers(value)}
    found |= {("date", word) for word in _date_terms(value)} | {("drug", word) for word in _drug_terms(value)}
    return found | {("side", token.text) for token in tokens(value) if token.text in SIDES}


def _run(terms: list[_Term], words: list[Token]) -> list[_Term]:
    """The first terms of the list that make one expression: nothing but RUN_GLUE between each and the next. The list
    can run backwards, for the expression that ends last."""
    run = [terms[0]]
    for term in terms[1:]:
        earlier, later = sorted((run[-1], term), key=lambda t: t.first)
        if any(_word_key(words[i].text) not in RUN_GLUE for i in range(earlier.last + 1, later.first)):
            break
        run.append(term)
    return run


def _corrections(text: str, words: list[Token], terms: list[_Term]) -> tuple[set[int], set[int], set[_Term]]:
    """What the clinician's corrections change (see the module's header): the words of each cue that corrected
    something, the words each took back, and the terms in them. A value may still use a term it took back that a source
    says elsewhere ("150 over 90, sorry, 140 over 90" says 90 again)."""
    marks = [mark.end() for mark in SENTENCE_MARK.finditer(text)]
    sentence = [bisect.bisect_right(marks, word.start) for word in words]
    cues: set[int] = set()
    taken_back: set[int] = set()
    superseded: set[_Term] = set()
    for cue in CORRECTION.finditer(text):
        inside = [i for i, word in enumerate(words) if cue.start() <= word.start < cue.end()]
        if not inside:
            continue
        first, after = inside[0], inside[-1] + 1
        before = {i for i in range(first) if sentence[i] == sentence[first]}
        starts_sentence = not before
        if starts_sentence and first:
            before = {i for i in range(first) if sentence[first - 1] - SENTENCES_BACK < sentence[i] <= sentence[first - 1]}
        for kind in KINDS:
            following = [term for term in terms if term.kind == kind and term.first >= after]
            preceding = [term for term in terms if term.kind == kind and term.last < first and term.first in before]
            if not following or following[0].first >= after + CORRECTION_WINDOW or not preceding:
                continue
            correction = _run(following, words)
            said_again = {term.text for term in correction}
            replaced = _run(preceding[::-1], words)
            if starts_sentence:
                # The nearest expression of the kind said before, in the same unit if the correction gives one
                # ("Start lisinopril 10 mg daily. Recheck in 2 weeks. Actually, make that 20 mg." corrects the 10 mg).
                unit = _unit(correction[-1], words)
                runs = _runs_backward(preceding, words)
                replaced = next((run for run in runs if unit is None or _unit(run[0], words) == unit), runs[0])
            if all(term.text in said_again for term in replaced):
                continue  # said again unchanged: "lisinopril 10 mg, sorry, lisinopril 20 mg" keeps lisinopril
            # With the one word between it and the cue, if there is just one: its unit ("95 degrees, no sorry, 90").
            end = replaced[0].last + 1
            taken_back.update(range(replaced[-1].first, end + 1 if first - end == 1 else end))
            superseded.update(replaced)
            cues.update(inside)
    return cues, taken_back, superseded


def _runs_backward(terms: list[_Term], words: list[Token]) -> list[list[_Term]]:
    """The terms' expressions, the last first, each from its last term back (as _run gives it)."""
    runs: list[list[_Term]] = []
    remaining = terms[::-1]
    while remaining:
        runs.append(_run(remaining, words))
        remaining = remaining[len(runs[-1]) :]
    return runs


def _unit(term: _Term, words: list[Token]) -> str | None:
    """The unit said right after a number ("mg", "week"), if the word after it is one."""
    following = _word_key(words[term.last + 1].text) if term.last + 1 < len(words) else None
    return following if following in MEASURES else None


def _expressions(terms: list[_Term], words: list[Token]) -> dict[_Term, int]:
    """Numbers a run of glue joins ("150 over 90", "10 mg") are one expression, one measured thing; every other term is
    its own."""
    found: dict[_Term, int] = {}
    previous: dict[str, _Term] = {}
    for term in terms:
        last = previous.get(term.kind)
        joined = last is not None and all(_word_key(words[i].text) in RUN_GLUE for i in range(last.last + 1, term.first))
        found[term] = found[last] if joined else len(found)
        previous[term.kind] = term
    return found


def _possible_corrections(value: str, spans: list[tuple[int, int]], transcript: _Transcript) -> list[tuple[int, int]]:
    """Where a correction the checks didn't hear may be (see the module's header): for each number, date or drug name the
    value uses from its sources, the first of its kind, in another expression, that the transcript says within
    WARNING_REACH words after it with a correction-like word between them (WARNING_GAP_WORDS)."""
    used = {term for term in _value_terms(value) if term[0] in WARNED_KINDS}
    words = transcript.words
    found: set[tuple[int, int]] = set()
    for term in transcript.terms:
        if (term.kind, term.text) not in used or not any(_holds(span, term, words) for span in spans):
            continue
        for later in transcript.terms:
            # A correction the checks heard is theirs to handle: its cue doesn't count as a correction-like word.
            gap = [i for i in range(term.last + 1, later.first) if i not in transcript.cues]
            if (
                later.kind == term.kind and later.text != term.text and term.last < later.first <= term.last + WARNING_REACH
                and transcript.expressions[later] != transcript.expressions[term]
                and (WARNING_GAP_WORDS is None or any(words[i].text in WARNING_GAP_WORDS for i in gap))
            ):
                found.add((words[later.first].start, words[later.last].end))
                break  # the first one after it: what may have replaced it
    return sorted(found)


def _near_unclear(spans: list[tuple[int, int]], unclear: list[tuple[int, int]], words: list[Token]) -> list[tuple[int, int]]:
    """Each unclear passage within NEAR_UNCLEAR words of one of the spans, a value's sources or a proposal's words (see
    the module's header)."""
    found = []
    for start, end in unclear:
        unclear = [i for i, word in enumerate(words) if start <= word.start < end]
        for span in spans:
            source = [i for i, word in enumerate(words) if span[0] <= word.start and word.end <= span[1]]
            if not unclear or not source:
                continue
            between = unclear[0] - source[-1] - 1 if unclear[0] > source[-1] else source[0] - unclear[-1] - 1
            if between <= NEAR_UNCLEAR:
                found.append((start, end))
                break
    return found


def _holds(span: tuple[int, int], term: _Term, words: list[Token]) -> bool:
    return span[0] <= words[term.first].start and words[term.last].end <= span[1]


def _reading(span: tuple[int, int], transcript: _Transcript) -> list[str]:
    """A source as compared, read as corrected: without the cues and what they took back."""
    return _sequence([
        word.text
        for i, word in enumerate(transcript.words)
        if span[0] <= word.start and word.end <= span[1] and i not in transcript.cues and i not in transcript.taken_back
    ])


# ---- Format --------------------------------------------------------------------------------------------------------

BAD_FORMAT = re.compile(r"[\x00-\x08\x0b-\x1f\x7f]|<[^>]*>|https?://|www\.|```|[\[\]{}]")

# Where a sentence or a clause ends: never at the point or comma inside a number ("12.5 mg", "38.9", "1,000").
SENTENCE_MARK = re.compile(r"[!?\n]|\.(?!\d)|(?<!\d)\.")
CLAUSE_MARK = re.compile(r"[!?;\n]|[.,](?!\d)|(?<!\d)[.,]")


def _sentence_end(text: str, start: int) -> int:
    found = SENTENCE_MARK.search(text, start)
    return found.start() if found else len(text)


# ---- Orders and allergy-list changes (FR-14) ----------------------------------------------------------------------

_DET = r"(?:an?|the|her|his|their|this|that|my|our|its|your)"
# Each instruction is an order or an allergy change (its reason in notCarriedOver), and, finer, which a dictation may
# propose: an order, unless it asks the software for an orders section, and an allergy added (ALLERGY_ADDITION).
ORDERS_SECTION = "orders_section"
ALLERGY_ADDITION = "allergy_addition"
REASON = {"order": "order", ORDERS_SECTION: "order", "allergy_change": "allergy_change", ALLERGY_ADDITION: "allergy_change"}
_SAME_SENTENCE = r"(?:(?![.\n]).)"
# Sent to the pharmacy, said however far on in the sentence (not past what ends it, as SENTENCE_MARK reads it: "2.5 mg"
# doesn't): "send amoxicillin 500 mg by mouth three times a day for 7 days to her pharmacy". Within a few words, the
# pharmacy is enough without "to".
_TO_THE_PHARMACY = rf"(?:[^.!?\n]|(?<=\d)\.(?=\d))*?\b(?:to|into)\s+(?:{_DET}\s+)?(?:\w+\s+){{0,2}}pharmacy\b"
# Placing an order, or a prescription, now: the verb as an instruction ("order", "renew") or under way ("ordering").
_ORDER_VERB = re.compile(
    r"\b(?:order|ordering|e-?prescribe|prescribe|prescribing|refill|refilling|renew|renewing|e-?rx)\b", re.IGNORECASE
)
# Words that make the verb a noun or an adjective ("the order", "a refill", "the ordering physician"), or "in order to",
# when nothing but a space comes between: "No, order a chest X-ray" and "...the lab. Order a CBC" are instructions.
_NOUN_BEFORE = frozenset(
    "a an the this that her his their its my our your no any standing verbal written phone telephone in of lab".split()
)
_ORDERS = [
    re.compile(
        rf"\b(?:place|put in|putting in|enter|send in|send over|send|call in|phone in|fax|write|writing|give|giving)\s+"
        rf"(?:(?:{_DET}|him|them|me|us)\s+)?(?:{_DET}\s+)?(?:\w+\s+)?(?:order|prescription|script|rx|refill)s?\b",
        re.IGNORECASE,
    ),
    re.compile(rf"\b(?:put|putting|get|getting)\s+(?:{_DET}\s+)?(?:\w+\s+)?orders?\s+in\b", re.IGNORECASE),
    # "In to" as speech recognition often writes it: "call that into his usual pharmacy".
    re.compile(r"\b(?:call|phone)\s+(?:it|that|this|them|those|these)\s+in(?:to)?\b", re.IGNORECASE),
    re.compile(rf"\b(?:send|sending)\b(?:{_SAME_SENTENCE}{{0,60}}?\bpharmacy\b|{_TO_THE_PHARMACY})", re.IGNORECASE),
]
# The model told to make an orders section: "add an orders field with an MRI". Never proposed.
_ORDERS_SECTION = re.compile(
    rf"\b(?:add|create|make|include|insert|start|open|put)\b{_SAME_SENTENCE}{{0,30}}?\borders?\s+"
    r"(?:section|field|tab|entry)\b",
    re.IGNORECASE,
)
# Adding to the allergy list, which a dictation may propose (the expansion plan's step 15), and every other change to
# it, which it never does.
_ALLERGY_ADDITIONS = [
    re.compile(rf"\badd\b{_SAME_SENTENCE}{{0,40}}?\b(?:to|as|on)\s+(?:{_DET}\s+)?(?:drug\s+|medication\s+)?allerg", re.I),
    re.compile(rf"\badd\s+(?:{_DET}\s+)?(?:new\s+)?(?:\w+\s+)?allerg", re.IGNORECASE),
    re.compile(rf"\b(?:enter|document)\s+(?:{_DET}\s+)?(?:new\s+)?(?:\w+\s+)?allerg", re.IGNORECASE),
    re.compile(rf"\blist\b{_SAME_SENTENCE}{{0,40}}?\bas\s+(?:an?\s+)?allerg", re.IGNORECASE),
    re.compile(rf"\bput\b{_SAME_SENTENCE}{{0,40}}?\b(?:on|in|into)\s+(?:{_DET}\s+)?allerg", re.IGNORECASE),
    re.compile(
        rf"\b(?:make sure|ensure|confirm|check that)\b{_SAME_SENTENCE}{{0,40}}?\bon\s+(?:{_DET}\s+)?(?:drug\s+)?allerg\w*\s+list",
        re.IGNORECASE,
    ),
]
_ALLERGY_CHANGES = [
    re.compile(rf"\b(?:remove|delete|take)\b{_SAME_SENTENCE}{{0,40}}?\b(?:from|off)\s+(?:{_DET}\s+)?(?:drug\s+)?allerg", re.I),
    re.compile(rf"\b(?:remove|delete)\s+(?:{_DET}\s+)?(?:\w+\s+)?allerg", re.IGNORECASE),
    re.compile(rf"\b(?:update|change|edit|correct|amend)\s+(?:{_DET}\s+)?(?:drug\s+|medication\s+)?allerg", re.IGNORECASE),
    re.compile(rf"\b(?:mark|flag)\b{_SAME_SENTENCE}{{0,40}}?\ballerg", re.IGNORECASE),
    re.compile(rf"\b(?:discontinue|inactivate|deactivate)\s+(?:{_DET}\s+)?(?:new\s+)?(?:\w+\s+)?allerg", re.IGNORECASE),
    re.compile(rf"\bclear\s+{_DET}\s+(?:drug\s+|medication\s+)?allerg", re.IGNORECASE),
    re.compile(r"\ballerg\w*(?:\s+list)?\s*[:,-]\s*(?:add|remove|update|change|delete)\b", re.IGNORECASE),
    # "Not allergic to sulfa after all, please update that": the instruction is the update, not what precedes it.
    re.compile(rf"\ballerg\w*\b{_SAME_SENTENCE}{{0,120}}?\b(?P<at>(?:please\s+)?update\s+(?:that|this|it))\b", re.I),
    re.compile(rf"\ballerg\w*\s+list\b{_SAME_SENTENCE}{{0,30}}?\b(?:add|remove|update|change|delete)\b", re.IGNORECASE),
]
# Instructions only in some places, each with the test of its place:
# - "call in" anything ("call in an antibiotic", "call in something about her nausea"), but not calling in sick, about
#   something, to say something or for an appointment, nor yesterday, said right after "call in", or after a word for
#   when ("she will call in sick", "mom will call in tomorrow about the rash"); nor someone else calling in, in the past
#   ("her husband did call in", "she was calling in from work"), though someone asked to is an instruction ("the nurse
#   was asked to call in amoxicillin");
# - "note" or "record" an allergy as an instruction: at the start of its clause, or after asking for it ("we need to
#   record", "make sure to note"); not after a subject that does the noting ("her records note a sulfa allergy", "I note
#   a penicillin allergy in her chart" are documentation).
_CALL_IN = re.compile(r"\b(?:call|calling|phone|phoning)\s+in\b", re.IGNORECASE)
_NOT_AN_ORDER = re.compile(
    r"\s+(?:(?:today|tomorrow|later)\s+)?(?:sick|about|yesterday|to say|for an? appointment)\b",
    re.IGNORECASE,
)
_SOMEONE_ELSE_DID = re.compile(r"\b(?:did|was|were|had been)\s+$", re.IGNORECASE)
_SPEAKER = frozenset("i we you i'll we'll i'm we're i've we've you'll let's".split())
_NOTE_ALLERGY = re.compile(rf"\b(?:record|note)\s+(?:{_DET}\s+)?(?:new\s+)?(?:\w+\s+)?allerg", re.IGNORECASE)
_ASKING = re.compile(r"\b(?:need to|make sure(?: to)?|remember to|be sure to|don't forget to|do not forget to)\b", re.I)
_NOTING = frozenset({"i", "we"})


def _calls_in_an_order(text: str, match: re.Match, clause: int) -> bool:
    if _NOT_AN_ORDER.match(text, match.end()):
        return False
    before = text[clause : match.start()]
    return not (_SOMEONE_ELSE_DID.search(before) and not any(token.text in _SPEAKER for token in tokens(before)))


def _at_clause_start(text: str, match: re.Match, clause: int) -> bool:
    before = tokens(text[clause : match.start()])
    if before and before[-1].text in _NOTING:
        return False
    return all(token.text in LEAD_IN for token in tokens(_ASKING.sub(" ", text[clause : match.start()])))


# A past-tense statement about the pharmacy is documentation: "her prescriptions were sent to the pharmacy last week".
PAST = re.compile(
    r"\b(?:yesterday|last (?:week|month|year|night|visit|time)|ago|earlier|previously|already|(?:was|were|had been|has been|"
    r"have been) sent)\b",
    re.IGNORECASE,
)


def _not_in_the_past(text: str, match: re.Match, clause: int) -> bool:
    sentence = max((mark.end() for mark in SENTENCE_MARK.finditer(text, 0, match.start())), default=0)
    return not PAST.search(text[sentence : _sentence_end(text, match.end())])


_PLACED = [
    ("order", _CALL_IN, _calls_in_an_order),
    (ALLERGY_ADDITION, _NOTE_ALLERGY, _at_clause_start),
]
# Instructions whose subject comes first ("penicillin should be added to her allergies"): their span starts with it.
_PASSIVE = [
    (ALLERGY_ADDITION, re.compile(
        rf"\b(?:added|listed|put)\s+(?:to|from|on|as|off|in|into)\s+(?:{_DET}\s+)?(?:drug\s+|medication\s+)?allerg",
        re.IGNORECASE,
    )),
    ("allergy_change", re.compile(
        rf"\b(?:removed|deleted|taken)\s+(?:to|from|on|as|off|in|into)\s+(?:{_DET}\s+)?(?:drug\s+|medication\s+)?allerg",
        re.IGNORECASE,
    )),
    ("allergy_change", re.compile(
        rf"\ballerg\w*\b{_SAME_SENTENCE}{{0,40}}?\b(?:come|comes|coming|go|goes|be taken|be removed)\s+off\b", re.I
    )),
    ("order", re.compile(rf"\bsent\b(?:{_SAME_SENTENCE}{{0,40}}?\bpharmacy\b|{_TO_THE_PHARMACY})", re.IGNORECASE)),
]
_PASSIVE_PLACES = {"order": _not_in_the_past}
# What can lead up to an instruction ("go ahead and", "please", "I'll"); anything else ends its span, so the plan said
# just before it ("start lisinopril 10 mg daily and order...") stays out of it.
LEAD_IN = frozenset(
    "go ahead and please let's let us i'll we'll i'm we're i we am are will can could would you also just now then".split()
)


def directive_spans(text: str) -> list[tuple[int, int]]:
    return [(start, end) for start, end, _ in directives(text)]


def directives(text: str) -> list[tuple[int, int, str]]:
    """Each instruction to place an order ("order") or change the allergy list ("allergy_change"), from its lead-in, or
    its subject when that comes first, to the end of its clause (see _directive_end()). Overlapping ones are merged, an
    allergy change winning. Hyphens are ignored ("re-fill")."""
    return [(start, end, reason) for start, end, reason, _, _ in _instructions(text)]


def _instructions(text: str) -> list[tuple[int, int, str, frozenset[str], int]]:
    """directives(), each with the finer kinds of the instructions merged into it (REASON's keys) and where the first
    of them was matched, which is after its lead-in or subject."""
    found = _directives(text)
    letters = [i for i, character in enumerate(text) if character != "-"]
    if len(letters) < len(text):
        plain = "".join(text[i] for i in letters)
        found += [(letters[start], letters[end - 1] + 1, kind, letters[at]) for start, end, kind, at in _directives(plain)]
    merged: list[tuple[int, int, str, frozenset[str], int]] = []
    for start, end, kind, at in sorted(found):
        if merged and start < merged[-1][1]:
            first, last, reason, kinds, matched = merged[-1]
            reason = "allergy_change" if "allergy_change" in (REASON[kind], reason) else reason
            merged[-1] = (first, max(last, end), reason, kinds | {kind}, min(matched, at))
        else:
            merged.append((start, end, REASON[kind], frozenset({kind}), at))
    return merged


def _directives(text: str) -> list[tuple[int, int, str, int]]:
    words = tokens(text)
    matches: list[tuple[int, int, str]] = []
    for match in _ORDER_VERB.finditer(text):
        before = [token for token in words if token.end <= match.start()][-1:]
        after = text[match.end() : match.end() + 4].lower()
        if (before and before[0].text in _NOUN_BEFORE and not text[before[0].end : match.start()].strip()) or after.startswith(" of"):
            continue
        matches.append((match.start(), match.end(), "order"))
    for kind, patterns in (("order", _ORDERS), (ORDERS_SECTION, [_ORDERS_SECTION]), (ALLERGY_ADDITION, _ALLERGY_ADDITIONS),
                           ("allergy_change", _ALLERGY_CHANGES)):
        for pattern in patterns:
            matches += [(match.start("at" if "at" in pattern.groupindex else 0), match.end(), kind)
                        for match in pattern.finditer(text)]
    for kind, pattern, placed in _PLACED:
        for match in pattern.finditer(text):
            clause = max((mark.end() for mark in CLAUSE_MARK.finditer(text, 0, match.start())), default=0)
            if placed(text, match, clause):
                matches.append((match.start(), match.end(), kind))
    passive = [
        (match.start(), match.end(), kind)
        for kind, pattern in _PASSIVE
        for match in pattern.finditer(text)
        if kind not in _PASSIVE_PLACES or _PASSIVE_PLACES[kind](text, match, 0)
    ]

    spans = []
    for start, end, kind in sorted(matches + passive):
        clause = max((mark.end() for mark in CLAUSE_MARK.finditer(text, 0, start)), default=0)
        negated = NEGATED_INSTRUCTION.search(text[clause:start])
        before = text[clause : clause + negated.start()] if negated else ""
        if negated and not NO_REASON.search(before) and not any(
            ANY_NEGATION.search(token.text) for token in tokens(before)[-UNDOES_REACH:]
        ):
            continue
        span_start = start
        subject = (start, end, kind) in passive
        for token in reversed([token for token in words if token.end <= start][-LEAD_IN_REACH:]):
            if token.start < clause or (token.text in CLAUSE_STARTS | {"and"} if subject else token.text not in LEAD_IN):
                break
            span_start = token.start
        spans.append((span_start, _directive_end(text, words, end), kind, start))
    return spans


# An instruction not to: "don't order an A1c", "no need to order labs", "hold off on ordering an MRI", "sulfa should not
# be added". It is not an instruction, so nothing is cut or reported; the negation rule keeps it in the value. Unless
# another negation comes right before it, within UNDOES_REACH words, which makes it one again: "no reason not to order
# an MRI", "I can't not order an MRI" (ANY_NEGATION), or "no reason" with whoever should do it ("there's no reason we
# should not order an MRI", NO_REASON); not one further back that negates something else ("she has no chest pain so no
# need to order a troponin").
NEGATED_INSTRUCTION = re.compile(
    r"\b(?:not|don't|dont|never|won't|no need(?: to| for)?|hold off(?: on)?|no)\s+(?:(?:be|to|on|going|any|further|yet)\s+)"
    r"{0,3}$",
    re.IGNORECASE,
)
ANY_NEGATION = re.compile(r"\b(?:no|not|never|nothing|none|cannot)\b|n't\b", re.IGNORECASE)
NO_REASON = re.compile(
    r"\bno (?:good |real )?reason\s+(?:\w+\s+){0,3}(?:should|would|could|can)\s+$", re.IGNORECASE
)
# Words that start another clause, which ends an instruction's span.
CLAUSE_STARTS = frozenset("then but so because although though while whereas also".split())
# After "and" or a comma, a new subject or another step of the plan starts another clause; anything else ("order a CBC
# and a BMP") is more of what is being ordered.
NEW_CLAUSE = frozenset(
    """i we she he they you patient let's i'll we'll i'm we're she's he's start continue stop increase decrease reduce
    resume hold recheck return schedule refer avoid take use apply keep switch discontinue monitor counsel discuss
    review encourage advise advised explained recommend recommended come""".split()
)
# After the allergy list or a pharmacy, these carry an instruction on ("add an allergy to codeine"); anything else ends it.
CARRIES_ON = frozenset("to for of include includes including with as".split())
TARGET = re.compile(r"(?:allerg\w*|list|record|pharmacy)$", re.IGNORECASE)
# What can follow "allergy" and still name the list: "allergy list", "allergy record".
LIST_WORDS = frozenset("list record".split())


def _directive_end(text: str, words: list[Token], end: int) -> int:
    """Where an instruction matched up to `end` stops: at the end of its sentence or a semicolon, at a word that starts
    another clause, or DIRECTIVE_REACH words on, whichever comes first. One that already names the allergy list or the
    pharmacy it goes to stops there ("take amoxicillin off her allergy list"), unless a word carries it on."""
    stop = min(_sentence_end(text, end), (text.find(";", end) + 1 or len(text) + 1) - 1)
    end += len(re.match(r"[\w'’]*", text[end:]).group())  # the whole of the word the match ended in ("allergies")
    following = [word for word in words if word.start >= end and word.end <= stop]
    if following and following[0].text in LIST_WORDS:
        end, following = following[0].end, following[1:]
    if TARGET.search(text[:end]) and not (following and following[0].text in CARRIES_ON):
        return end
    joined = False
    for word in following[:DIRECTIVE_REACH]:
        if word.text in CLAUSE_STARTS or (word.text in NEW_CLAUSE and (joined or "," in text[end : word.start])):
            break
        joined = word.text == "and"
        if not joined:
            end = word.end
    return end


SENTENCE = re.compile(r"(?<=[.!?;\n])\s+")
TRAILING_CONNECTOR = re.compile(r"(?:[\s,;:-]|\b(?:and|or|then|also|so)\b)+$", re.IGNORECASE)
# Words a cut sentence can't end on: what came before them depends on the instruction cut after them ("Do not start
# metformin until we order an A1c" is not "Do not start metformin"), so the whole sentence goes.
DANGLING = frozenset(
    """until till before after unless if when whenever once because since while as that which who whom whose where to
    for of with without by from than about not no never""".split()
)


def _without_instructions(value: str) -> tuple[str, int]:
    """The value with each instruction cut out: a sentence is cut where its instruction starts ("Start metformin, and
    order an A1c." keeps "Start metformin."), and dropped if the instruction is all it holds or what is left would end
    on a word that needs it (DANGLING). Also how many were cut."""
    kept: list[str] = []
    cut = 0
    for sentence in SENTENCE.split(value):
        found = directives(sentence)
        if not found:
            kept.append(sentence)
            continue
        cut += len(found)
        head = TRAILING_CONNECTOR.sub("", sentence[: found[0][0]])
        words = tokens(head)
        if words and words[-1].text not in DANGLING and any(
            token.text not in LEAD_IN and token.text not in STOPWORDS for token in words
        ):
            kept.append(head + ".")
    return " ".join(kept).strip(), cut


# ---- Grounding -----------------------------------------------------------------------------------------------------

MONTHS = frozenset(
    "january february march april may june july august september october november december sept".split()
)
WEEKDAYS = frozenset("monday tuesday wednesday thursday friday saturday sunday".split())
RELATIVE = frozenset("today yesterday tomorrow tonight ago".split())
PERIOD_STARTS = frozenset("last next this past".split())
PERIODS = frozenset("week weeks month months year years weekend night morning evening".split()) | WEEKDAYS
# "may" is a month after one of these, or before a day: "since May", "on May 3".
MAY_BEFORE = frozenset("in on since until till by from of during early late mid before after".split())


def _date_words(words: list[str]) -> list[int]:
    """Where the words name a month, a weekday or a day relative to today."""
    found = []
    for i, word in enumerate(words):
        previous = words[i - 1] if i else ""
        following = words[i + 1] if i + 1 < len(words) else ""
        if word == "may":
            if previous in MAY_BEFORE or following[:1].isdigit() or following in NUMBER_WORDS:
                found.append(i)
        elif word in MONTHS or word in WEEKDAYS or word in RELATIVE:
            found.append(i)
    return found


def _date_terms(text: str) -> set[str]:
    words = [token.text for token in tokens(text)]
    found = {words[i] for i in _date_words(words)}
    return found | {f"{previous} {word}" for previous, word in zip(words, words[1:]) if previous in PERIOD_STARTS
                    and word in PERIODS}


# USAN stems: most drug names end in one, including new ones no list has heard of yet; and a few endings of common
# older drugs no stem covers (metformin, warfarin, paracetamol, prednisone, tacrolimus, levetiracetam, lamotrigine). Any
# other drug name a value adds is caught as ungrounded content.
DRUG_STEMS = tuple(
    """mab nib tinib ciclib parib rafenib pril sartan olol alol dipine statin gliflozin gliptin glutide glinide tide
    parin xaban gatran prazole tidine azole conazole cillin cycline floxacin mycin micin vir navir previr asvir buvir
    tegravir semide thiazide lukast terol sonide olone betasol pam zolam oxetine triptan setron profen coxib caine
    dronate lutamide relin tropin kinra cept umab ximab zumab tecan platin rubicin mustine fungin bactam penem
    entan sentan gepant tirom vastatin dapsone zepine apine idone peridol pramine triptyline afil lol formin farin
    amol sone olimus racetam trigine""".split()
)
# Ordinary words that happen to end in a stem.
NOT_DRUGS = frozenset("except accept concept intercept precept april cholesterol riptide".split())


def _is_drug(word: str) -> bool:
    return word.isalpha() and len(word) >= 5 and word.endswith(DRUG_STEMS) and word not in NOT_DRUGS


def _drug_terms(text: str) -> set[str]:
    return {token.text for token in tokens(text) if _is_drug(token.text)}


def _word_key(word: str) -> str:
    return stem(UNIT_SPELLINGS.get(word, word))


def _ungrounded(value: str, sources: str) -> bool:
    """Whether the value has a word its sources don't: numbers are left to the number check, and the articles are
    allowed."""
    available = {_word_key(token.text) for token in tokens(sources)}
    for token in tokens(value):
        word = token.text
        if word[0].isdigit() or word in NUMBER_WORDS or word in FREE_WORDS:
            continue
        if _word_key(word) not in available:
            return True
    return False


SEGMENT_BREAK = re.compile(r"(?:[;:!?()\n]|[.,](?!\d)|(?<!\d)[.,])+")


def _sequence(words: list[str]) -> list[str]:
    """The words as compared: number words read together as digits, units in one spelling, words stemmed, and
    "over" between two numbers left out, since "140 over 90" is "140/90"."""
    out: list[str] = []
    run: list[str] = []

    def flush() -> None:
        if run:
            trailing = run[-1] == "and"
            out.extend(numbers(" ".join(run[:-1] if trailing else run)))
            if trailing:
                out.append("and")
            run.clear()

    for word in words:
        if word in NUMBER_WORDS or (word == "and" and run and run[-1] in ("hundred", "thousand")):
            run.append(word)
            continue
        flush()
        out.extend(numbers(word) if word[0].isdigit() else [_word_key(word)])
    flush()
    return [
        word for i, word in enumerate(out)
        if not (word == "over" and 0 < i < len(out) - 1 and out[i - 1][:1].isdigit() and out[i + 1][:1].isdigit())
    ]


def _recombined(value: str, readings: list[list[str]]) -> bool:
    """Whether a segment of the value can't be found in order in any one source, each read as corrected (see the
    module's header)."""
    for segment in SEGMENT_BREAK.split(value):
        wanted = [word for word in _sequence([token.text for token in tokens(segment)]) if word not in FREE_WORDS]
        if wanted and not any(_in_order(wanted, reading) for reading in readings):
            return True
    return False




def _skippable(word: str) -> bool:
    return word in FREE_WORDS or word in DOSE_WORDS or word[:1].isdigit()


LIST_JOINS = frozenset({"and", "or"})


def _in_order(wanted: list[str], source: list[str]) -> bool:
    """Whether `wanted` can be found in order in `source`, with at most MAX_GAP non-skippable words between two of them,
    and only articles and units before a number. Before a drug name, whole other items of a list may be left out, their
    drug, dose and "and" ("continue metformin 500 mg" from "continue lisinopril 10 mg and metformin 500 mg"), but then
    nothing else ("stop metformin" can't come from "stop lisinopril and start metformin"). Each (word, position) pair is
    tried once, so repeated words can't make the search blow up."""
    tried: set[tuple[int, int]] = set()

    def onward(k: int, position: int) -> bool:
        if k == len(wanted):
            return True
        if (k, position) in tried:
            return False
        tried.add((k, position))
        skipped = 0
        number = wanted[k][:1].isdigit()
        drug = _is_drug(wanted[k])
        skipped_item = False
        for j in range(position + 1, len(source)):
            if source[j] == wanted[k] and onward(k + 1, j):
                return True
            if number and source[j] not in FREE_WORDS and source[j] not in DOSE_WORDS:
                return False
            if drug and (_is_drug(source[j]) or source[j] in LIST_JOINS):
                skipped_item = True
            elif not _skippable(source[j]):
                skipped += 1
            if skipped > MAX_GAP or (skipped_item and skipped):
                return False
        return False

    return any(onward(1, i) for i, word in enumerate(source) if word == wanted[0])


# ---- Negation, who has it, and how sure ----------------------------------------------------------------------------

NEGATION = re.compile(
    r"\b(?:no(?![\s,]+(?:sorry|wait|correction)\b)(?!\s*,)|not|denies|denied|deny|without|never|none|negative for|absence of|"
    r"free of|ruled out|rules out|doesn't|does not|didn't|did not|isn't|is not|wasn't|was not|hasn't|has not|haven't|"
    r"have not|don't|do not|won't|will not|shouldn't|should not|non)\b",
    re.IGNORECASE,
)
# Cues that negate what comes before them: "chest pain denied", "fracture ruled out".
POST_NEGATION = re.compile(r"\b(?:denied|ruled out|negative|absent|not present|not seen)\b", re.IGNORECASE)
FAMILY = (
    r"mother|mom|father|dad|brothers?|sisters?|siblings?|sons?|daughters?|parents?|grandmothers?|grandfathers?|"
    r"grandparents?|grandma|grandpa|aunts?|uncles?|cousins?|wife|husband|partner|spouse|nieces?|nephews?"
)
# Someone other than the patient: "family history of breast cancer", "mother had a stroke", "diabetes in her father".
# Not a family member someone is with or speaks to ("lives with her husband and has had a cough"), who has nothing,
# unless a relative clause says what they have: "seen with her mother(,) who has diabetes" (WHO_HAS).
NOT_EXPERIENCER_AFTER = frozenset("with by to from for at about".split())
WHO_HAS = re.compile(r"\s*,?\s+(?:who|that)\s+(?=(?:has|had|is|was)\b)", re.IGNORECASE)
EXPERIENCER = re.compile(rf"\b(?:family (?:history|hx)(?: of)?|maternal|paternal|{FAMILY})\b", re.IGNORECASE)
POST_EXPERIENCER = re.compile(
    rf"\b(?:in|on)\s+(?:her|his|their|the|a|both)\s+(?:maternal\s+|paternal\s+)?(?:{FAMILY})\b", re.IGNORECASE
)
# Not certain: "possible community acquired pneumonia", "rule out appendicitis", "pneumonia suspected".
CERTAINTY = re.compile(
    r"\b(?:possible|possibly|probable|probably|suspected|suspect|suspicious for|suspicion of|likely|unlikely|"
    r"rule out|r/o|risk of|risk for|concern for|concerning for|questionable|presumed)\b",
    re.IGNORECASE,
)
POST_CERTAINTY = re.compile(
    r"\b(?:suspected|likely|possible|probable|unlikely|questionable|presumed)\b(?=\s*(?:[.,;:!?\n]|$))", re.IGNORECASE
)
# In the past: "a history of MI in 2019", "status post appendectomy".
# Not the heading "history of present illness", said without its colon.
HISTORY = re.compile(r"(?<!family )\b(?:history of(?! present illness)|hx of|h/o|status post|s/p)\b", re.IGNORECASE)
# Where a cue's reach ends, besides punctuation: a contrast, or a new subject ("no fever, she is eating well").
TERMINATORS = frozenset(
    """but however although though except apart aside yet which who whereas he she they i we you patient he's she's
    they're i'm we're i've""".split()
)
# Someone telling, not having: "mom says the rash started Monday".
REPORTING = frozenset(
    "says said reports reported states stated notes noted thinks feels felt tells told mentions mentioned asks asked "
    "worries worried brought brings called calls".split()
)
# Without punctuation, where the next clause starts: a step of the plan or a heading ("no further pancreatitis continue
# monthly olezarsen", "no other injury on exam swelling"). Not right after the cue or "to", where the word is what it
# negates ("not take aspirin", "not able to take aspirin").
NEXT_CLAUSE = (NEW_CLAUSE - {"patient"}) | frozenset("exam examination assessment impression plan vitals".split())
# Each kind of qualifier, with its reason code: cues before the words they qualify, cues after them, and what ends
# their reach.
QUALIFIERS = (
    ("negation_dropped", NEGATION, POST_NEGATION, TERMINATORS),
    ("experiencer_dropped", EXPERIENCER, POST_EXPERIENCER, TERMINATORS | REPORTING),
    ("certainty_dropped", CERTAINTY, POST_CERTAINTY, TERMINATORS),
    ("history_dropped", HISTORY, None, TERMINATORS),
)


def _reach(text: str, words: list[Token], before: re.Pattern, after: re.Pattern | None,
           ends: frozenset[str]) -> tuple[set[int], set[int]]:
    """The words the text's cues of one kind reach, and the cues' own words (which are neither reached nor plain:
    "mother" in "mother had a stroke" is who had it)."""
    reached: set[int] = set()
    cue_words: set[int] = set()

    def stops(between: str, word: str) -> bool:
        return word in ends or any(mark in between for mark in ".,;:?!\n")

    for cue in before.finditer(text):
        relative = WHO_HAS.match(text, cue.end()) if before is EXPERIENCER else None
        if before is EXPERIENCER and not relative and _object_of_a_preposition(text, cue.start()):
            continue
        cue_words |= {i for i, token in enumerate(words) if cue.start() <= token.start < cue.end()}
        edge = relative.end() if relative else cue.end()
        for k, i in enumerate([i for i, token in enumerate(words) if token.start >= edge][:NEGATION_SCOPE]):
            next_clause = k and words[i].text in NEXT_CLAUSE and words[i - 1].text != "to"
            if stops(text[edge : words[i].start], words[i].text) or next_clause:
                break
            reached.add(i)
            edge = words[i].end
    for cue in after.finditer(text) if after else ():
        cue_words |= {i for i, token in enumerate(words) if cue.start() <= token.start < cue.end()}
        edge = cue.start()
        for i in reversed([i for i, token in enumerate(words) if token.end <= cue.start()][-POST_SCOPE:]):
            if stops(text[words[i].end : edge], words[i].text):
                break
            reached.add(i)
            edge = words[i].start
    return reached, cue_words


def _object_of_a_preposition(text: str, start: int) -> bool:
    """Whether the words before `start` are a preposition, and maybe a possessive: "with her", "by his"."""
    before = [token.text for token in tokens(text[:start])][-2:]
    if before and before[-1] in {"her", "his", "their", "the", "a"}:
        before = before[:-1]
    return bool(before) and before[-1] in NOT_EXPERIENCER_AFTER


def _qualifiers(text: str, words: list[Token]) -> dict[str, set[int]]:
    """For each kind of qualifier, by its reason code, the words its cues reach in the text."""
    return {reason: _reach(text, words, before, after, ends)[0] for reason, before, after, ends in QUALIFIERS}


def _dropped(value: str, spans: list[tuple[int, int]], transcript: _Transcript) -> set[str]:
    """The qualifiers that reach words of the value's sources, in the transcript, which the value uses without them,
    even if another source has those words plain: a negation, someone else having it, uncertainty, or the past. Read
    in the transcript, so a source can't leave its qualifier out ("chest pain" quoted from "no chest pain")."""
    words = tokens(value)
    found = set()
    for reason, before, after, ends in QUALIFIERS:
        reached, cues = _reach(value, words, before, after, ends)
        plain = {stem(word.text) for i, word in enumerate(words) if _is_content(word.text) and i not in reached | cues}
        plain -= {stem(words[i].text) for i in reached if _is_content(words[i].text)}
        qualified = {
            stem(transcript.words[i].text)
            for i in transcript.qualified[reason]
            if _is_content(transcript.words[i].text)
            and any(start <= transcript.words[i].start and transcript.words[i].end <= end for start, end in spans)
        }
        if qualified & plain:
            found.add(reason)
    return found


def _is_content(word: str) -> bool:
    """A word a qualifier can apply to: not a number or a function word, but "MI" or "PE" is one."""
    return word.isalpha() and len(word) >= 2 and word not in STOPWORDS


# ---- Proposals (the expansion plan's step 15; FR-14 as reworded) --------------------------------------------------

MAX_PROPOSALS = 20  # at most, in one dictation, as the on-prem app stages them
TAKEN_BACK_REACH = 25  # at most, words after an instruction in which taking it back holds it back
# Why a listed instruction isn't proposed, in the order the rules are tried: the first that applies is the one given.
HELD_BACK = (
    "not_final", "to_the_software", "mixed_kinds", "not_an_addition", "negated", "hedged", "for_later", "taken_back",
    "in_the_past", "unclear", "question", "quoted", "not_imperative", "not_plain", "too_many",
)  # fmt: skip
# What may come between the start of an instruction's clause and the instruction: its lead-in (LEAD_IN) and the words
# a clinician starts a step of the plan with.
OPENERS = LEAD_IN | frozenset("okay ok alright so plan next finally lastly first second third going to now".split())
HEDGE = re.compile(
    r"\b(?:if|unless|when|whenever|once|consider|considering|maybe|perhaps|possibly|probably|might|could|would|"
    r"discuss|discussed|discussing|think|thinking|wonder|wondering|whether)\b",
    re.IGNORECASE,
)
CONDITIONS = frozenset("if unless when whenever once".split())
CONDITION_AFTER = re.compile(r"\b(?:if|unless|in case|once|when|whenever|depending on)\b", re.IGNORECASE)
_UNITS = "|".join(sorted(UNITS, key=len, reverse=True))
_COUNT = rf"\d+|a|a few|a couple of|several|(?:{'|'.join(TENS)})(?:[\s-](?:{_UNITS}))?|{_UNITS}"
FOR_LATER = re.compile(
    r"\bnext\s+(?:visit|time|appointment|week|month|year|spring|summer|fall|autumn|winter)\b"
    r"|\bat\s+(?:the\s+|her\s+|his\s+|their\s+)?(?:next\s+)?(?:follow[\s-]?up|visit|review|appointment)\b"
    r"|\b(?:later|eventually|in the future|down the line)\b"
    rf"|\bin\s+(?:{_COUNT})\s+(?:days?|weeks?|months?|years?)\b",
    re.IGNORECASE,
)
TAKEN_BACK = re.compile(
    CORRECTION.pattern + r"|\b(?:hold off|never ?mind|forget (?:that|it|about it)|cancel that|on second thought|"
    r"let's not|let us not|not yet)\b"
    r"|\b(?:hold|forget)\s+(?:that|it|this|those|them)\b"
    r"|\b(?:ignore|disregard|delete|strike|undo|skip|cancel|cancelled|scratch|drop|retract|rescind)\b"
    r"|\btake\s+(?:that|it)\s+back\b|\bwait\s*,?\s*no\b|\b(?:declined|declines|refused|refuses)\b"
    r"|\b(?:don't|doesn't|do not|does not|won't|will not)\s+(?:need|want)\s+(?:it|that|this|one|them)\b"
    r"|\bno longer need\w*|\bchanged? (?:my|our|her|his) minds?\b",
    re.IGNORECASE,
)
TO_THE_SOFTWARE = re.compile(
    rf"\b(?:ignore|disregard|forget)\b{_SAME_SENTENCE}{{0,60}}?\b(?:rules|instructions|prompt|guidelines|directions)\b"
    r"|\b(?:note|instruction|message|request)s?\s+(?:to|for)\s+(?:the\s+)?(?:\w+\s+)?"
    r"(?:assistant|ai|model|system|software|scribe|computer)\b",
    re.IGNORECASE,
)
# The software called on, at the start of a sentence: "Assistant, order a CT", "Dear model, ...".
VOCATIVE = re.compile(r"(?:dear\s+)?(?:assistant|ai|model|system|computer|scribe|software|chatbot)\s*[,:]", re.IGNORECASE)
QUOTING = REPORTING | frozenset("wondered wonders wondering asking saying".split())


@dataclass(frozen=True)
class Weighed:
    """One listed instruction (not_carried_over()) weighed as a proposal: what it would propose (order or
    allergy_addition), or the listed reason when it is neither; the rule that holds it back (HELD_BACK), None when it is
    proposed; and its warnings, as for a value. Code points in the transcript."""

    start: int
    end: int
    kind: str
    held_back: str | None
    warnings: tuple[tuple[str, int, int], ...] = ()


def proposals(transcript: Transcript) -> list[dict]:
    """What the dictation proposes (see the module's header): each order or allergy to add the clinician dictated
    plainly, as its kind, the place of its listed instruction and its warnings, in UTF-16 offsets. Never words."""
    text = transcript.text
    return [
        {
            "kind": weighed.kind,
            "start": utf16_offset(text, weighed.start),
            "end": utf16_offset(text, weighed.end),
            "warnings": [
                {"code": code, "start": utf16_offset(text, start), "end": utf16_offset(text, end)}
                for code, start, end in weighed.warnings
            ],
        }
        for weighed in weigh(transcript)
        if weighed.held_back is None
    ]


def weigh(transcript: Transcript) -> list[Weighed]:
    """Every listed instruction, in order, as proposed or held back (see the module's header)."""
    text = transcript.text
    words = tokens(text)
    unclear = transcript.unclear_ranges()
    weighed: list[Weighed] = []
    for start, end, reason, kinds, at in _instructions(text):
        kind = "order" if kinds == {"order"} else ALLERGY_ADDITION if kinds == {ALLERGY_ADDITION} else reason
        held_back = "not_final" if not transcript.final else _held_back(text, words, unclear, start, end, kinds, at)
        if held_back is None and sum(1 for w in weighed if w.held_back is None) == MAX_PROPOSALS:
            held_back = "too_many"
        warnings = () if held_back else _proposal_warnings(text, words, unclear, start, end, at)
        weighed.append(Weighed(start, end, kind, held_back, warnings))
    return weighed


def _held_back(text: str, words: list[Token], unclear: list[tuple[int, int]], start: int, end: int,
               kinds: frozenset[str], at: int) -> str | None:
    """The first rule that holds an instruction back from being proposed (HELD_BACK), or None. `at` is where it was
    matched: what comes before it in its clause is its lead-in or subject."""
    clause = max((mark.end() for mark in CLAUSE_MARK.finditer(text, 0, start)), default=0)
    clause_end = next((mark.start() for mark in CLAUSE_MARK.finditer(text, at)), len(text))
    sentence = max((mark.end() for mark in SENTENCE_MARK.finditer(text, 0, start)), default=0)
    sentence_end = _sentence_end(text, at)
    lead = [token for token in words if clause <= token.start and token.end <= at]
    if TO_THE_SOFTWARE.search(text, sentence, sentence_end) or VOCATIVE.match(text[sentence:sentence_end].lstrip()):
        return "to_the_software"
    if kinds & {"order", ORDERS_SECTION} and kinds & {"allergy_change", ALLERGY_ADDITION}:
        return "mixed_kinds"
    if kinds not in ({"order"}, {ALLERGY_ADDITION}):
        return "not_an_addition"
    if NEGATION.search(text, clause, at):
        return "negated"
    sentence_words = [token.text for token in words if sentence <= token.start < sentence_end]
    dates = set(_date_words([token.text for token in lead]))
    if (
        HEDGE.search(text, clause, at)
        or any(token.text == "may" and i not in dates for i, token in enumerate(lead))
        or (sentence_words and sentence_words[0] in CONDITIONS)
        or text[sentence:sentence_end].lstrip().lower().startswith("in case")
        or CONDITION_AFTER.search(text, at, sentence_end)
    ):
        return "hedged"
    if FOR_LATER.search(text, sentence, sentence_end):
        return "for_later"
    after = [token for token in words if token.start >= end][:TAKEN_BACK_REACH]
    next_sentence_end = _sentence_end(text, sentence_end + 1) if sentence_end < len(text) else len(text)
    reach = min(next_sentence_end, after[-1].end if len(after) == TAKEN_BACK_REACH else len(text))
    if TAKEN_BACK.search(text, start, max(reach, end)):
        return "taken_back"
    if PAST.search(text, clause, clause_end):
        return "in_the_past"
    if any(_overlaps((start, end), passage) for passage in unclear):
        return "unclear"
    if sentence_end < len(text) and text[sentence_end] == "?":
        return "question"
    previous = [token.text for token in words if token.end <= clause]
    if clause and previous and previous[-1] in QUOTING:
        return "quoted"
    # Said as an instruction: from the start of its clause, or the last "and" or word starting a clause before it,
    # nothing but openers up to its lead-in or subject (not "capillary refill under 2 seconds").
    joins = [i for i, token in enumerate(lead) if token.text in CLAUSE_STARTS or token.text == "and"]
    if any(token.text not in OPENERS for token in lead[joins[-1] + 1 if joins else 0 :] if token.start < start):
        return "not_imperative"
    if not _plain(text, sentence, sentence_end, start, at, kinds):
        return "not_plain"
    return None


# ---- A plain instruction: the one form a proposal is made from ----------------------------------------------------

# What may come before the verb, from the instruction's start (the sentence's, or a join's): words that open a step of
# the plan, "please", the clinician's own intent, "go ahead and". Nothing else: not a subject, a condition, a name or
# a negation.
PLAIN_LEAD = re.compile(
    r"(?:(?:okay|ok|alright|so|and|also|then|now|next|finally|lastly|plan)\s*[,:]?\s+)*(?:please\s+)?"
    r"(?:(?:i'll|i will|i'm going to|i am going to|we'll|we will|we're going to|we are going to|let's|let us|i'm|i am|"
    r"we're|we are)\s+)?(?:please\s+)?(?:go ahead and\s+)?(?:also\s+|just\s+)?"
)
# Where a plain instruction may start after a statement of the plan in its sentence: "Continue omeprazole and refill
# it", "..., so please add ...", "Stop lisinopril, then order a BMP".
PLAIN_JOIN = re.compile(r"(?:,\s*)?\b(?:and|so|then)\s+", re.IGNORECASE)
# The subject of an allergy to add said first, and its verb: "penicillin should be added to her allergies".
PLAIN_PASSIVE = re.compile(r"(?P<x>[^,;:]+?)\s+(?:should|needs to|must|has to|is to|will)\s+be\s+", re.IGNORECASE)
_PEOPLE = frozenset(
    """mother mom father dad brother brothers sister sisters sibling siblings son sons daughter daughters parent parents
    grandmother grandfather grandparent grandparents grandma grandpa aunt uncle cousin wife husband partner spouse niece
    nephew friend roommate caregiver nurse nurses colleague resident doctor dr provider physician specialist surgeon
    mr mrs ms miss mister room bed""".split()
)
_HEDGES = frozenset("probably perhaps possibly maybe likely unlikely might could would may".split())
_CONDITIONS = frozenset(
    "if unless when whenever once until till after before assuming provided providing pending depending should whether "
    "event case otherwise".split()
)
_TELLING = frozenset(
    """said says say asked asks ask told tells insisted insists suggested suggests wants want wanted requested request
    requests mentioned mentions thinks think thought believes wondered wonders""".split()
)
_SOFTWARE = frozenset("ignore disregard forget instructions instruction rules rule prompt directions guidelines "
                      "assistant ai model system computer scribe software chatbot".split())
# Never in a plain instruction's words, from its verb to the end of its sentence: someone else, a patient by name or
# place, a hedge, a condition, an alternative or "as needed", a negation, a subject or word of telling, a reflexive, a
# vague or reported object, the software, or another step of the plan.
_NOT_PLAIN_AFTER = (
    _PEOPLE | {"patient", "patients"} | _HEDGES | _CONDITIONS | _TELLING | _SOFTWARE
    | frozenset(
        """or either nor instead needed prn no not nothing none never nobody neither i we you she he they yourself
        himself herself themselves myself ourselves itself any that which whatever something anything everything note
        notes comment comments""".split()
    )
)
_PLAN_VERBS = frozenset(
    """start continue stop increase decrease reduce resume hold recheck return schedule refer avoid use apply keep
    switch discontinue monitor counsel discuss review encourage advise come remove delete add put call mark update
    change clear take cancel skip give make send renew prescribe see check follow""".split()
)
# A meal or bedtime a dose is taken by, which "before" and "after" may name.
_DOSE_TIME = re.compile(r"\b(?:before|after)\s+(?:meals?|food|breakfast|lunch|dinner|supper|bed|bedtime|exercise)\b", re.I)
_CALLED_IN = re.compile(r"\b(?:call|phone)\s+(?:it|that|this|them|those|these)\s+in", re.IGNORECASE)
# Before a join: a condition, a hedge, someone asking, someone else, or the software.
_NOT_PLAIN_BEFORE = (
    frozenset("if unless whether assuming provided providing pending depending".split()) | _HEDGES
    | frozenset("consider considering think thinking discuss discussed discussing wonder wondered wondering".split())
    | frozenset("asked asks asking requested requests requesting wants wanted insisted insists suggested suggests".split())
    | _PEOPLE | _SOFTWARE
)
_OPENING_CONDITIONS = _CONDITIONS - {"event", "case", "otherwise"}
# A step of the plan said with its verb, which an instruction may follow after a comma alone; and what may come before
# that verb.
_PLAN_STEPS = frozenset(
    "start continue stop increase decrease reduce resume hold recheck refer avoid use apply keep switch discontinue "
    "monitor carry add".split()
)
_STEP_LEAD = frozenset("okay ok alright so and also then now next finally lastly plan please".split())
# An allergy to add names a substance, of one to five words, on the patient's own list.
_ALLERGEN = [
    re.compile(r"add\s+(?P<x>.+?)\s+(?:to|on|as)\s+(?:(?:her|his|their|the)\s+)?(?:drug\s+|medication\s+)?(?:an?\s+)?allerg", re.I),
    re.compile(r"add\s+(?:an?\s+|the\s+)?(?:new\s+)?allerg\w*\s+(?:to|for)\s+(?P<x>.+?)\s*$", re.I),
    re.compile(r"add\s+(?:an?\s+|the\s+)?(?:new\s+)?(?P<x>\w+)\s+allerg", re.I),
    re.compile(r"put\s+(?P<x>.+?)\s+(?:on|in|into)\s+(?:(?:her|his|their|the)\s+)?allerg", re.I),
    re.compile(r"list\s+(?P<x>.+?)\s+as\s+(?:an?\s+)?allerg", re.I),
    re.compile(r"(?:make sure|ensure|confirm)\s+(?P<x>.+?)\s+is\s+on\s+(?:(?:her|his|their|the)\s+)?(?:drug\s+)?allerg", re.I),
    re.compile(r"(?:record|note|enter|document)\s+(?:an?\s+|the\s+)?(?:new\s+)?allerg\w*\s+to\s+(?P<x>.+?)\s*$", re.I),
    re.compile(r"(?:record|note|enter|document)\s+(?:an?\s+|the\s+)?(?:new\s+)?(?P<x>\w+)\s+allerg", re.I),
    re.compile(r"(?:added|put|listed)\s+(?:to|on|as)\s+(?:(?:her|his|their|the)\s+)?(?:drug\s+|medication\s+)?(?:an?\s+)?allerg", re.I),
]
_NOT_AN_ALLERGEN = frozenset(
    """note notes comment comments that which reaction reactions severity was is were are been details detail
    information info history it allergy allergies""".split()
)


def _plain(text: str, sentence: int, sentence_end: int, start: int, at: int, kinds: frozenset[str]) -> bool:
    """Whether the instruction is said in the one plain form a proposal is made from (see the module's header)."""
    passive = PLAIN_PASSIVE.fullmatch(text, start, at)
    lead_end = start if passive else at
    # Its start: the sentence's, or the last join before it, the rest of the sentence before that being a statement of
    # the plan; and from there to the verb (or to the subject said first), only a plain lead-in.
    begin = before_end = None
    if _lead_in(text[sentence:lead_end]):
        begin = sentence
    else:
        for join in reversed(list(PLAIN_JOIN.finditer(text, sentence, lead_end))):
            if _lead_in(text[join.end() : lead_end]):
                begin, before_end = join.end(), join.start()
                break
        # Or a comma after a step of the plan said with its verb, when speech recognition kept no "and": "Continue the
        # ointments as needed, cephalexin should be added to her allergies".
        comma = text.rfind(",", sentence, lead_end)
        if begin is None and comma >= 0 and _lead_in(text[comma + 1 : lead_end]):
            step = [token.text for token in tokens(text[sentence:comma]) if token.text not in _STEP_LEAD]
            if step and step[0] in _PLAN_STEPS:
                begin, before_end = comma + 1, comma
        if before_end is not None:
            before = tokens(text[sentence:before_end])
            if not before or before[0].text in _OPENING_CONDITIONS or _plainly(text[sentence:before_end]).startswith(
                ("in case", "in the event")
            ) or any(_bare(token.text) in _NOT_PLAIN_BEFORE or _bare(token.text).endswith("ologist") for token in before):
                return False
    if begin is None:
        return False
    # Nor right after a bare negation or doubt, a sentence of a few words: "No. Order a CBC", "I'm not sure. Order ...".
    previous = tokens(text[max((mark.end() for mark in SENTENCE_MARK.finditer(text, 0, max(sentence - 1, 0))), default=0) : sentence])
    if previous and len(previous) <= 4 and any(
        token.text in {"no", "not", "nothing", "none", "never", "sure"} | _HEDGES or token.text.endswith("n't") for token in previous
    ):
        return False

    # From the verb to the end of the sentence: no clause after it, nothing but the instruction's own words.
    after = text[at:sentence_end]
    if re.search(r"[;:()\[\]\"“”—–]|,(?!\d)|(?<!\d),|\s-\s", after):
        return False
    # Its words after the verb, which may be hyphenated ("e-prescribe"); a reason may have its own subject ("because he
    # has felt run down").
    said = _CALLED_IN.sub("call in", after)
    verb = re.match(r"\S+", said)
    words = tokens(_DOSE_TIME.sub(" ", said[verb.end() if verb else 0 :]))
    reason = next((i for i, token in enumerate(words) if token.text == "because"), len(words))
    for i, token in enumerate(words):
        word = _bare(token.text)
        if i > reason and word in {"i", "we", "you", "she", "he", "they"}:
            continue
        if word in _NOT_PLAIN_AFTER or word.endswith("ologist") or token.text.endswith("n't") or word in _PLAN_VERBS:
            return False

    if kinds == {ALLERGY_ADDITION}:
        allergen = passive.group("x") if passive else next(
            (m.group("x") for pattern in _ALLERGEN if (m := pattern.match(_plainly(after)))), None
        )
        if allergen is None or not 1 <= len(tokens(allergen)) <= 5 or any(
            _bare(token.text) in _NOT_AN_ALLERGEN for token in tokens(allergen)
        ):
            return False
        if passive and not _ALLERGEN[-1].match(_plainly(after)):
            return False
    return True


def _lead_in(text: str) -> bool:
    """Whether the words before an instruction's verb are a plain lead-in (PLAIN_LEAD), or none."""
    lead = _plainly(text)
    return not lead or PLAIN_LEAD.fullmatch(lead + " ") is not None


def _plainly(text: str) -> str:
    """Lower case, one space between words, a straight apostrophe."""
    return " ".join(text.lower().replace("’", "'").split())


def _bare(word: str) -> str:
    """A word without its possessive: "mother's" is "mother"."""
    return word.removesuffix("'s")


def _proposal_warnings(text: str, words: list[Token], unclear: list[tuple[int, int]], start: int, end: int,
                       at: int) -> tuple[tuple[str, int, int], ...]:
    """A proposal's warnings: possible_correction, pointing from a correction-like word (WARNING_GAP_WORDS) said after
    the instruction, in its words or within WARNING_REACH words after them, to the end of those or of its sentence; and
    near_unclear_passage, as for a value."""
    warnings: list[tuple[str, int, int]] = []
    reach = [token for token in words if at < token.start < end] + [token for token in words if token.start >= end][:WARNING_REACH]
    cue = next((token for token in reach if token.text in (WARNING_GAP_WORDS or ())), None)
    if cue is not None:
        warnings.append((POSSIBLE_CORRECTION, cue.start, max(cue.end, min(reach[-1].end, _sentence_end(text, cue.start)))))
    warnings += [(NEAR_UNCLEAR_PASSAGE, first, last) for first, last in _near_unclear([(start, end)], unclear, words)]
    return tuple(warnings)
