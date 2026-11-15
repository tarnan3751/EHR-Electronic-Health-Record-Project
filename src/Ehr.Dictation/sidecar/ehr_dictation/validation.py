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

Every value and source is first NFKC-normalized with Unicode format characters (such as a zero-width space) removed,
and every check reads the cleaned text. The checks, each with the reason code recorded when it fails:

- unknown_field: a key outside history, assessment and plan (the schema allows none; this is the backstop).
- not_dictated: an empty value. Not a failure: the transcript said nothing for that field.
- too_long: over the field's length limit (LIMITS), over 30 sources, or a source over 600 characters.
- bad_format: control or format characters, characters NFKC would change, a non-ASCII letter the transcript never uses
  (a look-alike from another alphabet), markup, a link, code or JSON.
- order_or_allergy_change (FR-14): nothing was left of the value once its instructions were cut, or nothing was left of
  its sources once the instructions they quote were cut out of them. An instruction says to place an order, or that one
  is being placed now ("I'm ordering"), to write, send, call in, refill or renew a prescription, to send something to
  the pharmacy, to make an orders section, or to change the allergy list (add, remove, record, clear, update); hyphens
  are ignored ("re-fill"). A plan stays a plan: "start", "continue", "increase" or "stop" a drug, orders in the past or
  by someone else ("her cardiologist ordered an echo last year"), and an instruction not to ("don't order an A1c", "no
  need to order labs"), pass.
- no_sources, source_too_short (under two words, without a number), source_not_in_transcript (compared word by word,
  ignoring case and punctuation), source_in_unclear_passage.
- source_superseded: the value uses what the clinician corrected ("sorry", "no sorry", "no correction", "I mean",
  "actually", "no,", "make it", "rather", "scratch that"), read across the whole transcript. A correction gives a
  number, date, drug name or side (left, right) within three words of its cue, and supersedes the last expression of the
  same kind said before the cue in its sentence (or in the sentence before, when the cue starts one), unless it says all
  of it again. One expression is one measured thing: numbers joined only by units, articles or "over" ("150 over 90"),
  with the one word after it when that is all that comes before the cue ("95 degrees, no sorry, 90 degrees").
  So "heart rate 78, no sorry 72" supersedes 78 and not the blood pressure said before it; "10 mg, sorry, 20 mg"
  supersedes 10 mg; and "lisinopril 10 mg, sorry, 20 mg" keeps lisinopril, since no drug replaces it. A source is read
  as corrected: without the cue and what it took back.
- number_not_in_sources, date_not_in_sources, drug_not_in_sources: the specific cases of a word not in the sources, kept
  for what they say: a number, a date word ("may" counts as one after a preposition or before a day), a drug-like name.
- negation_dropped, experiencer_dropped, certainty_dropped, history_dropped: in the transcript, a qualifier reaches
  words of the value's sources that the value uses without it, even where another source has them plain: a negation ("no
  chest pain" as "chest pain"), someone other than the patient having it ("family history of breast cancer", "mother had
  a stroke", "diabetes in her father"), uncertainty ("possible pneumonia", "rule out appendicitis", "pneumonia
  suspected"), or the past ("a history of MI in 2019"). A cue reaches six words on (four back, for one that follows what
  it qualifies, such as "denied"), and stops at its clause: punctuation, a contrast ("but"), a new subject ("she",
  "patient"), a step of the plan or a heading after its first word ("no further pancreatitis continue monthly
  olezarsen"), and for someone else, a word of telling ("mom says the rash started Monday" is the patient's rash).
- words_out_of_order: the value's words are all in its sources, but not in order within one of them.
- unsupported_content: any other word of the value isn't in its sources.

The eval (eval/) measures what these let through and what they throw away; its gate ties results to a hash of this
package's code, so a change to any rule asks for a new run.
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
LIMITS = {"history": 3000, "assessment": 2000, "plan": 2000}
MAX_SOURCES = 30
MAX_QUOTE = 600
CORRECTION_WINDOW = 3  # words after a correction cue within which the corrected value starts
NEGATION_SCOPE = 6  # words after a negation, experiencer, certainty or history cue, within its clause
POST_SCOPE = 4  # words before a cue that qualifies what precedes it ("chest pain denied")
DIRECTIVE_REACH = 14  # at most, words after an instruction that still belong to it, when nothing ends its clause sooner
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
    )
    checked = {name: _check_field(name, fields[name]["value"], fields[name]["sources"], context) for name in FIELDS}
    return Checks(checked, list(unknown))


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
    return Checked(name, value, quotes, ordered, spans if not ordered else [])


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
        before = [i for i in range(first) if sentence[i] == sentence[first]]
        if not before and first:
            before = [i for i in range(first) if sentence[i] == sentence[first - 1]]
        for kind in KINDS:
            following = [term for term in terms if term.kind == kind and term.first >= after]
            preceding = [term for term in terms if term.kind == kind and term.last < first and term.first in before]
            if not following or following[0].first >= after + CORRECTION_WINDOW or not preceding:
                continue
            said_again = {term.text for term in _run(following, words)}
            replaced = _run(preceding[::-1], words)
            if all(term.text in said_again for term in replaced):
                continue  # said again unchanged: "lisinopril 10 mg, sorry, lisinopril 20 mg" keeps lisinopril
            # With the one word between it and the cue, if there is just one: its unit ("95 degrees, no sorry, 90").
            end = replaced[0].last + 1
            taken_back.update(range(replaced[-1].first, end + 1 if first - end == 1 else end))
            superseded.update(replaced)
            cues.update(inside)
    return cues, taken_back, superseded


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

_DET = r"(?:an?|the|her|his|their|this|that|my|our|its)"
_SAME_SENTENCE = r"(?:(?![.\n]).)"
# Placing an order, or a prescription, now: the verb as an instruction ("order", "renew") or under way ("ordering").
_ORDER_VERB = re.compile(
    r"\b(?:order|ordering|e-?prescribe|prescribe|prescribing|refill|refilling|renew|renewing|e-?rx)\b", re.IGNORECASE
)
# Words that make the verb a noun or an adjective ("the order", "a refill", "the ordering physician"), or "in order to".
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
    re.compile(r"\b(?:call|calling|called|phone|phoning)\s+in\b", re.IGNORECASE),
    re.compile(r"\b(?:call|phone)\s+(?:it|that|this|them|those|these)\s+in\b", re.IGNORECASE),
    re.compile(rf"\b(?:send|sending)\b{_SAME_SENTENCE}{{0,60}}?\bpharmacy\b", re.IGNORECASE),
    # The model told to make an orders section: "add an orders field with an MRI".
    re.compile(
        rf"\b(?:add|create|make|include|insert|start|open|put)\b{_SAME_SENTENCE}{{0,30}}?\borders?\s+"
        r"(?:section|field|tab|entry)\b",
        re.IGNORECASE,
    ),
]
_ALLERGY_CHANGES = [
    re.compile(rf"\badd\b{_SAME_SENTENCE}{{0,40}}?\b(?:to|as|on)\s+(?:{_DET}\s+)?(?:drug\s+|medication\s+)?allerg", re.I),
    re.compile(rf"\badd\s+(?:{_DET}\s+)?(?:new\s+)?(?:\w+\s+)?allerg", re.IGNORECASE),
    re.compile(rf"\b(?:remove|delete|take)\b{_SAME_SENTENCE}{{0,40}}?\b(?:from|off)\s+(?:{_DET}\s+)?(?:drug\s+)?allerg", re.I),
    re.compile(rf"\b(?:remove|delete)\s+(?:{_DET}\s+)?(?:\w+\s+)?allerg", re.IGNORECASE),
    re.compile(rf"\b(?:update|change|edit|correct|amend)\s+(?:{_DET}\s+)?(?:drug\s+|medication\s+)?allerg", re.IGNORECASE),
    re.compile(rf"\b(?:mark|flag)\b{_SAME_SENTENCE}{{0,40}}?\ballerg", re.IGNORECASE),
    re.compile(
        rf"(?<!of )\b(?:enter|document|record|note|discontinue|inactivate|deactivate)\s+(?:{_DET}\s+)?(?:new\s+)?"
        r"(?:\w+\s+)?allerg",
        re.IGNORECASE,
    ),
    re.compile(rf"\bclear\s+{_DET}\s+(?:drug\s+|medication\s+)?allerg", re.IGNORECASE),
    re.compile(r"\ballerg\w*(?:\s+list)?\s*[:,-]\s*(?:add|remove|update|change|delete)\b", re.IGNORECASE),
    # "Not allergic to sulfa after all, please update that": the instruction is the update, not what precedes it.
    re.compile(rf"\ballerg\w*\b{_SAME_SENTENCE}{{0,120}}?\b(?P<at>(?:please\s+)?update\s+(?:that|this|it))\b", re.I),
    re.compile(rf"\blist\b{_SAME_SENTENCE}{{0,40}}?\bas\s+(?:an?\s+)?allerg", re.IGNORECASE),
    re.compile(rf"\bput\b{_SAME_SENTENCE}{{0,40}}?\b(?:on|in|into)\s+(?:{_DET}\s+)?allerg", re.IGNORECASE),
    re.compile(
        rf"\b(?:make sure|ensure|confirm|check that)\b{_SAME_SENTENCE}{{0,40}}?\bon\s+(?:{_DET}\s+)?(?:drug\s+)?allerg\w*\s+list",
        re.IGNORECASE,
    ),
    re.compile(rf"\ballerg\w*\s+list\b{_SAME_SENTENCE}{{0,30}}?\b(?:add|remove|update|change|delete)\b", re.IGNORECASE),
]
# Instructions whose subject comes first ("penicillin should be added to her allergies"): their span starts with it.
_PASSIVE = [
    ("allergy_change", re.compile(
        rf"\b(?:added|removed|deleted|listed|taken|put)\s+(?:to|from|on|as|off|in|into)\s+(?:{_DET}\s+)?"
        r"(?:drug\s+|medication\s+)?allerg",
        re.IGNORECASE,
    )),
    ("allergy_change", re.compile(
        rf"\ballerg\w*\b{_SAME_SENTENCE}{{0,40}}?\b(?:come|comes|coming|go|goes|be taken|be removed)\s+off\b", re.I
    )),
    ("order", re.compile(rf"\bsent\b{_SAME_SENTENCE}{{0,40}}?\bpharmacy\b", re.IGNORECASE)),
]
# What can lead up to an instruction ("go ahead and", "please", "I'll"); anything else ends its span, so the plan said
# just before it ("start lisinopril 10 mg daily and order...") stays out of it.
LEAD_IN = frozenset(
    "go ahead and please let's let us i'll we'll i'm we're i we am are will can could would you also just now then".split()
)


def directive_spans(text: str) -> list[tuple[int, int]]:
    return [(start, end) for start, end, _ in directives(text)]


def directives(text: str) -> list[tuple[int, int, str]]:
    """Each instruction to place an order ("order") or change the allergy list ("allergy_change"), from its lead-in, or
    its subject when that comes first, to the end of its clause (see _directive_end()). Overlapping ones are merged, an allergy
    change winning. Hyphens are ignored ("re-fill")."""
    found = _directives(text)
    letters = [i for i, character in enumerate(text) if character != "-"]
    if len(letters) < len(text):
        plain = "".join(text[i] for i in letters)
        found += [(letters[start], letters[end - 1] + 1, kind) for start, end, kind in _directives(plain)]
    merged: list[tuple[int, int, str]] = []
    for start, end, kind in sorted(found):
        if merged and start < merged[-1][1]:
            last = merged[-1]
            merged[-1] = (last[0], max(last[1], end), "allergy_change" if "allergy_change" in (kind, last[2]) else kind)
        else:
            merged.append((start, end, kind))
    return merged


def _directives(text: str) -> list[tuple[int, int, str]]:
    words = tokens(text)
    matches: list[tuple[int, int, str]] = []
    for match in _ORDER_VERB.finditer(text):
        before = [token.text for token in words if token.end <= match.start()][-1:]
        after = text[match.end() : match.end() + 4].lower()
        if (before and before[0] in _NOUN_BEFORE) or after.startswith(" of"):
            continue
        matches.append((match.start(), match.end(), "order"))
    for kind, patterns in (("order", _ORDERS), ("allergy_change", _ALLERGY_CHANGES)):
        for pattern in patterns:
            matches += [(match.start("at" if "at" in pattern.groupindex else 0), match.end(), kind)
                        for match in pattern.finditer(text)]
    passive = [(match.start(), match.end(), kind) for kind, pattern in _PASSIVE for match in pattern.finditer(text)]

    spans = []
    for start, end, kind in sorted(matches + passive):
        clause = max((mark.end() for mark in CLAUSE_MARK.finditer(text, 0, start)), default=0)
        if NEGATED_INSTRUCTION.search(text[clause:start]):
            continue
        span_start = start
        subject = (start, end, kind) in passive
        for token in reversed([token for token in words if token.end <= start][-LEAD_IN_REACH:]):
            if token.start < clause or (token.text in CLAUSE_STARTS | {"and"} if subject else token.text not in LEAD_IN):
                break
            span_start = token.start
        spans.append((span_start, _directive_end(text, words, end), kind))
    return spans


# An instruction not to: "don't order an A1c", "no need to order labs", "hold off on ordering an MRI", "sulfa should not
# be added". It is not an instruction, so nothing is cut or reported; the negation rule keeps it in the value.
NEGATED_INSTRUCTION = re.compile(
    r"\b(?:not|don't|dont|never|won't|no need(?: to| for)?|hold off(?: on)?|no)\s+(?:(?:be|to|on|going|any|further|yet)\s+)"
    r"{0,3}$",
    re.IGNORECASE,
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


def _without_instructions(value: str) -> tuple[str, int]:
    """The value with each instruction cut out: a sentence is cut where its instruction starts ("Start metformin, and
    order an A1c." keeps "Start metformin."), and dropped if the instruction is all it holds. Also how many were cut."""
    kept: list[str] = []
    cut = 0
    for sentence in SENTENCE.split(value):
        found = directives(sentence)
        if not found:
            kept.append(sentence)
            continue
        cut += len(found)
        head = TRAILING_CONNECTOR.sub("", sentence[: found[0][0]])
        if any(token.text not in LEAD_IN and token.text not in STOPWORDS for token in tokens(head)):
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


def _in_order(wanted: list[str], source: list[str]) -> bool:
    """Whether `wanted` can be found in order in `source`, with at most MAX_GAP non-skippable words between two of them,
    and only articles and units before a number. Each (word, position) pair is tried once, so repeated words can't make
    the search blow up."""
    tried: set[tuple[int, int]] = set()

    def onward(k: int, position: int) -> bool:
        if k == len(wanted):
            return True
        if (k, position) in tried:
            return False
        tried.add((k, position))
        skipped = 0
        number = wanted[k][:1].isdigit()
        for j in range(position + 1, len(source)):
            if source[j] == wanted[k] and onward(k + 1, j):
                return True
            if number and source[j] not in FREE_WORDS and source[j] not in DOSE_WORDS:
                return False
            if not _skippable(source[j]):
                skipped += 1
                if skipped > MAX_GAP:
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
HISTORY = re.compile(r"(?<!family )\b(?:history of|hx of|h/o|status post|s/p)\b", re.IGNORECASE)
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
        cue_words |= {i for i, token in enumerate(words) if cue.start() <= token.start < cue.end()}
        edge = cue.end()
        for k, i in enumerate([i for i, token in enumerate(words) if token.start >= cue.end()][:NEGATION_SCOPE]):
            if stops(text[edge : words[i].start], words[i].text) or (k and words[i].text in NEXT_CLAUSE and words[i - 1].text != "to"):
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
