"""Checks every value K2 Horizon proposes before a clinician can see it (FR-12). A value that fails any check is never
shown and its field is left empty; only the reason codes go out, never the text. A value that passes is held as pending
until a clinician approves it (FR-13), with the positions in the transcript of the passages it came from (FR-08).

Grounding is strict: every word of a value must be in that value's own sources, compared after the shared reading
(case, punctuation, numbers as digits whether written or spoken, a few unit spellings, and simple plural and tense
endings), except for a short list of connecting words (REFORMULATION). Nothing else may be added: not "twice", not
"right" for "left", not another drug, not a heading. Nor may words be recombined: the value is cut into segments at
punctuation (not the point in "12.5") and at "and" and "or", and each segment's words must be found in order in one of
its sources, with at most MAX_GAP of that source's own words between any two of them (connecting words and doses don't
count). So "right knee pain" can't come from "left knee pain, right knee normal". A source with a correction in it is
read as corrected.

An instruction to order something or to change the allergy list is never carried into the note (FR-14), but the rest of
a field is: each sentence of a value holding one is cut where the instruction starts, and sources quoting one are set
aside. Separately, not_carried_over() lists every such instruction in the transcript, with its kind and where the
clinician said it, so the review screen can say what still has to be placed in Orders or the allergy list.

Every value and source is first NFKC-normalized with Unicode format characters (such as a zero-width space) removed,
and every check reads the cleaned text. The checks, each with the reason code recorded when it fails:

- unknown_field: a key outside history, assessment and plan (the schema allows none; this is the backstop).
- not_dictated: an empty value. Not a failure: the transcript said nothing for that field.
- too_long: over the field's length limit, over 12 sources, or a source over 600 characters.
- bad_format: control or format characters, characters NFKC would change, a non-ASCII letter the transcript never uses
  (a look-alike from another alphabet), markup, a link, code or JSON.
- order_or_allergy_change (FR-14): nothing was left of the value once its instructions were cut, or nothing was left of
  its sources once those quoting an instruction were set aside. An instruction says to place an order, or that one is
  being placed now ("I'm ordering"), to send, call in, refill or renew a prescription, or to change the allergy list;
  hyphens are ignored ("re-fill"). A plan stays a plan: "start", "continue", "increase" or "stop" a drug, and orders in
  the past or by someone else ("her cardiologist ordered an echo last year"), pass.
- no_sources, source_too_short (under two words, without a number), source_not_in_transcript (compared word by word,
  ignoring case and punctuation), source_in_unclear_passage.
- source_superseded: what the clinician corrected ("sorry", "I mean", "actually", "no,", "make it", "rather",
  "scratch that"). A source followed by a correction within three words in its sentence is superseded whole. Inside a
  source, a number or dose, date or drug name said only before the correction is superseded when the correction gives
  one of the same kind: "10 mg, sorry, 20 mg" supersedes 10 mg, and "lisinopril 10 mg, sorry, 20 mg" keeps lisinopril.
- number_not_in_sources, date_not_in_sources, drug_not_in_sources: the specific cases of a word not in the sources,
  kept for what they say: a number, a date word ("may" counts as one after a preposition or before a day), a drug-like
  name.
- negation_dropped: a negation one of its sources applies to words the value uses isn't kept ("no chest pain" as
  "chest pain").
- words_out_of_order: the value's words are all in its sources, but not in order within one of them.
- unsupported_content: any other word of the value isn't in its sources.

The eval (eval/) measures what these let through and what they throw away; its gate ties results to a hash of this
package's code, so a change to any rule asks for a new run.
"""

from __future__ import annotations

import re
import string
import unicodedata
from dataclasses import dataclass, field

from .prompt import FIELDS
from .textnorm import ORDINAL_TENS, ORDINAL_UNITS, TENS, UNITS, Token, numbers, stem, tokens
from .transcript import Transcript, utf16_offset

LIMITS = {"history": 2000, "assessment": 1200, "plan": 1200}
MAX_SOURCES = 12
MAX_QUOTE = 600
CORRECTION_WINDOW = 3  # words after a quoted passage, within its sentence
NEGATION_SCOPE = 6  # words after a negation cue
POST_SCOPE = 4  # words before a cue that negates what precedes it
DIRECTIVE_REACH = 10  # words after an instruction that still belong to it
LEAD_IN_REACH = 5  # words before an instruction that can be its lead-in ("go ahead and")
MAX_GAP = 1  # a source's own words allowed between two of a value's words (connecting words and doses are free)

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
    "words_out_of_order",
    "unsupported_content",
)

# The only words a value may add to its sources: articles, copulas, conjunctions, prepositions and a few framing words.
# No negation, side, direction, quantity or time word is here, so none can be slipped in.
REFORMULATION = frozenset(
    """a an the is are was were be been being am has have had and or but also as of in on at to for with by from per
    about over after before into than then this that these those which who patient patients pt reports reported
    reporting report states stated says said presents presenting presented complains complaining notes noted describes
    described""".split()
)
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
class _Transcript:
    text: str
    words: list[Token]
    unclear: list[tuple[int, int]]
    directives: list[tuple[int, int]]
    letters: frozenset[str]


def not_carried_over(transcript: Transcript) -> list[dict]:
    """Each instruction in the transcript to order something or change the allergy list, which the note never carries:
    its kind (order or allergy_change) and where the clinician said it, as UTF-16 offsets into the transcript."""
    return [
        {"reason": kind, "start": utf16_offset(transcript.text, start), "end": utf16_offset(transcript.text, end)}
        for start, end, kind in directives(transcript.text)
    ]


def check(fields: dict[str, dict], unknown: list[str], transcript: Transcript) -> Checks:
    context = _Transcript(
        text=transcript.text,
        words=tokens(transcript.text),
        unclear=transcript.unclear_ranges(),
        directives=directive_spans(transcript.text),
        letters=frozenset(clean(transcript.text).casefold()),
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
        # A source quoting an instruction is set aside: whatever the value took from it is then ungrounded.
        if any(_overlaps(found, directive) for directive in transcript.directives):
            continue
        spans.append(found)
        kept.append(quote)
        if _followed_by_correction(transcript.text, transcript.words, found[1]):
            reasons.add("source_superseded")
    if quotes and not kept and not reasons & {"source_too_short", "source_not_in_transcript", "source_in_unclear_passage"}:
        reasons.add("order_or_allergy_change")
    quotes = kept

    sources = " ".join(quotes)
    value_numbers = set(numbers(value))
    if _superseded_terms(quotes) & (value_numbers | _date_terms(value) | _drug_terms(value)):
        reasons.add("source_superseded")
    if not value_numbers <= set(numbers(sources)):
        reasons.add("number_not_in_sources")
    if not _date_terms(value) <= _date_terms(sources):
        reasons.add("date_not_in_sources")
    if not _drug_terms(value) <= {token.text for token in tokens(sources)}:
        reasons.add("drug_not_in_sources")
    if _negation_dropped(value, quotes):
        reasons.add("negation_dropped")
    if _ungrounded(value, sources):
        reasons.add("unsupported_content")
    elif quotes and _recombined(value, quotes):
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


def _overlaps(a: tuple[int, int], b: tuple[int, int]) -> bool:
    return a[0] < b[1] and b[0] < a[1]


CORRECTION = re.compile(
    r"\b(?:no wait|wait no|sorry|i mean|i meant|correction|scratch that|strike that|make that|make it|i misspoke|"
    r"actually|rather)\b|\bno\s*,",
    re.IGNORECASE,
)


def _followed_by_correction(text: str, words: list[Token], end: int) -> bool:
    following = [token for token in words if token.start >= end][:CORRECTION_WINDOW]
    window = text[end : following[-1].end] if following else ""
    return bool(CORRECTION.search(window[: _sentence_end(window, 0)]))


def _superseded_terms(quotes: list[str]) -> set[str]:
    """Numbers and doses, dates and drug names a quote says only before a correction inside it, when the correction
    gives one of the same kind, and that no other quote says. "10 mg, sorry, 20 mg" supersedes 10; "lisinopril 10 mg,
    sorry, 20 mg" doesn't supersede lisinopril, since no drug replaces it."""
    superseded: set[str] = set()
    kept: set[str] = set()
    for quote in quotes:
        cues = list(CORRECTION.finditer(quote))
        before, after = (quote[: cues[-1].start()], quote[cues[-1].end() :]) if cues else ("", quote)
        for kind in (lambda text: set(numbers(text)), _date_terms, _drug_terms):
            said_before, said_after = kind(before), kind(after)
            if said_after:
                superseded |= said_before - said_after
            else:
                kept |= said_before
            kept |= said_after
    return superseded - kept


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
    r"\b(?:order|ordering|e-?prescribe|prescribe|prescribing|refill|refilling|renew|renewing)\b", re.IGNORECASE
)
# Words that make the verb a noun or an adjective ("the order", "a refill", "the ordering physician"), or "in order to".
_NOUN_BEFORE = frozenset(
    "a an the this that her his their its my our your no any standing verbal written phone telephone in of lab".split()
)
_ORDERS = [
    re.compile(
        rf"\b(?:place|put in|enter|send in|send over|send|call in|phone in|fax)\s+(?:{_DET}\s+)?(?:\w+\s+)?"
        r"(?:order|prescription|script|rx|refill)s?\b",
        re.IGNORECASE,
    ),
    re.compile(rf"\bput\s+(?:{_DET}\s+)?(?:\w+\s+)?orders?\s+in\b", re.IGNORECASE),
    re.compile(r"\b(?:call|calling|called|phone|phoning)\s+in\b", re.IGNORECASE),
]
_ALLERGY_CHANGES = [
    re.compile(rf"\badd\b{_SAME_SENTENCE}{{0,40}}?\b(?:to|as|on)\s+(?:{_DET}\s+)?(?:drug\s+|medication\s+)?allerg", re.I),
    re.compile(rf"\badd\s+(?:{_DET}\s+)?(?:new\s+)?(?:\w+\s+)?allerg", re.IGNORECASE),
    re.compile(rf"\b(?:remove|delete|take)\b{_SAME_SENTENCE}{{0,40}}?\b(?:from|off)\s+(?:{_DET}\s+)?(?:drug\s+)?allerg", re.I),
    re.compile(rf"\b(?:remove|delete)\s+(?:{_DET}\s+)?(?:\w+\s+)?allerg", re.IGNORECASE),
    re.compile(
        rf"\b(?:added|removed|deleted|listed|taken|put)\s+(?:to|from|on|as|off|in|into)\s+(?:{_DET}\s+)?"
        r"(?:drug\s+|medication\s+)?allerg",
        re.IGNORECASE,
    ),
    re.compile(rf"\b(?:update|change|edit|correct|amend)\s+(?:{_DET}\s+)?(?:drug\s+|medication\s+)?allerg", re.IGNORECASE),
    re.compile(rf"\b(?:mark|flag)\b{_SAME_SENTENCE}{{0,40}}?\ballerg", re.IGNORECASE),
    re.compile(rf"\b(?:enter|document)\s+(?:{_DET}\s+)?(?:new\s+)?(?:\w+\s+)?allerg", re.IGNORECASE),
    re.compile(rf"\blist\b{_SAME_SENTENCE}{{0,40}}?\bas\s+(?:an?\s+)?allerg", re.IGNORECASE),
    re.compile(rf"\bput\b{_SAME_SENTENCE}{{0,40}}?\b(?:on|in|into)\s+(?:{_DET}\s+)?allerg", re.IGNORECASE),
    re.compile(
        rf"\b(?:make sure|ensure|confirm|check that)\b{_SAME_SENTENCE}{{0,40}}?\bon\s+(?:{_DET}\s+)?(?:drug\s+)?allerg\w*\s+list",
        re.IGNORECASE,
    ),
    re.compile(rf"\ballerg\w*\s+list\b{_SAME_SENTENCE}{{0,30}}?\b(?:add|remove|update|change|delete)\b", re.IGNORECASE),
]
# What can lead up to an instruction ("go ahead and", "please", "I'll"); anything else ends its span, so the plan said
# just before it ("start lisinopril 10 mg daily and order...") stays out of it.
LEAD_IN = frozenset(
    "go ahead and please let's let us i'll we'll i'm we're i we am are will can could would you also just now then".split()
)


def directive_spans(text: str) -> list[tuple[int, int]]:
    return [(start, end) for start, end, _ in directives(text)]


def directives(text: str) -> list[tuple[int, int, str]]:
    """Each instruction to place an order ("order") or change the allergy list ("allergy_change"): from its lead-in to
    the end of its sentence, or DIRECTIVE_REACH words on when there's no punctuation. Overlapping ones are merged, an
    allergy change winning. Hyphens are ignored ("re-fill")."""
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
            matches += [(match.start(), match.end(), kind) for match in pattern.finditer(text)]

    spans = []
    for start, end, kind in sorted(matches):
        clause = max((mark.end() for mark in CLAUSE_MARK.finditer(text, 0, start)), default=0)
        span_start = start
        for token in reversed([token for token in words if token.end <= start][-LEAD_IN_REACH:]):
            if token.text not in LEAD_IN or token.start < clause:
                break
            span_start = token.start
        sentence = _sentence_end(text, end)
        reach = [token for token in words if token.start >= end][:DIRECTIVE_REACH]
        span_end = min(sentence, reach[-1].end if reach else end)
        spans.append((span_start, max(span_end, end), kind))
    return spans


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
        if any(token.text not in LEAD_IN and token.text not in REFORMULATION for token in tokens(head)):
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


def _date_terms(text: str) -> set[str]:
    words = [token.text for token in tokens(text)]
    found: set[str] = set()
    for i, word in enumerate(words):
        previous = words[i - 1] if i else ""
        following = words[i + 1] if i + 1 < len(words) else ""
        if word == "may":
            if previous in MAY_BEFORE or following[:1].isdigit() or following in NUMBER_WORDS:
                found.add(word)
        elif word in MONTHS or word in WEEKDAYS or word in RELATIVE:
            found.add(word)
        if previous in PERIOD_STARTS and word in PERIODS:
            found.add(f"{previous} {word}")
    return found


# USAN stems: most drug names end in one, including new ones no list has heard of yet. Any other drug name a value adds
# is caught as ungrounded content.
DRUG_STEMS = tuple(
    """mab nib tinib ciclib parib rafenib pril sartan olol alol dipine statin gliflozin gliptin glutide glinide tide
    parin xaban gatran prazole tidine azole conazole cillin cycline floxacin mycin micin vir navir previr asvir buvir
    tegravir semide thiazide lukast terol sonide olone betasol pam zolam oxetine triptan setron profen coxib caine
    dronate lutamide relin tropin kinra cept umab ximab zumab tecan platin rubicin mustine fungin bactam penem
    entan sentan gepant tirom vastatin dapsone zepine apine idone peridol pramine triptyline afil lol""".split()
)
# Ordinary words that happen to end in a stem.
NOT_DRUGS = frozenset("except accept concept intercept precept april cholesterol riptide".split())


def _drug_terms(text: str) -> set[str]:
    return {
        token.text
        for token in tokens(text)
        if token.text.isalpha() and len(token.text) >= 5 and token.text.endswith(DRUG_STEMS) and token.text not in NOT_DRUGS
    }


def _word_key(word: str) -> str:
    return stem(UNIT_SPELLINGS.get(word, word))


def _ungrounded(value: str, sources: str) -> bool:
    """Whether the value has a word its sources don't: numbers are left to the number check, and the reformulation
    words are allowed."""
    available = {_word_key(token.text) for token in tokens(sources)}
    for token in tokens(value):
        word = token.text
        if word[0].isdigit() or word in NUMBER_WORDS or word in REFORMULATION:
            continue
        if _word_key(word) not in available:
            return True
    return False


SEGMENT_BREAK = re.compile(r"(?:[;:!?()\n]|[.,](?!\d)|(?<!\d)[.,])+|\b(?:and|or)\b", re.IGNORECASE)
CONNECTING = frozenset(_word_key(word) for word in REFORMULATION)


def _sequence(text: str) -> list[str]:
    """The text's words as compared: number words read together as digits, units in one spelling, words stemmed."""
    out: list[str] = []
    run: list[str] = []

    def flush() -> None:
        if run:
            trailing = run[-1] == "and"
            out.extend(numbers(" ".join(run[:-1] if trailing else run)))
            if trailing:
                out.append("and")
            run.clear()

    for token in tokens(text):
        word = token.text
        if word in NUMBER_WORDS or (word == "and" and run and run[-1] in ("hundred", "thousand")):
            run.append(word)
            continue
        flush()
        out.extend(numbers(word) if word[0].isdigit() else [_word_key(word)])
    flush()
    return out


def _recombined(value: str, quotes: list[str]) -> bool:
    """Whether a segment of the value can't be found in order in any one source (see the module's header). A source
    with a correction in it is read as corrected: without the cue, and without what the correction replaced."""
    sources = [_corrected_sequence(quote) for quote in quotes]
    for segment in SEGMENT_BREAK.split(value):
        wanted = [word for word in _sequence(segment) if word not in CONNECTING]
        if wanted and not any(_in_order(wanted, source) for source in sources):
            return True
    return False


def _corrected_sequence(quote: str) -> list[str]:
    cues = list(CORRECTION.finditer(quote))
    if not cues:
        return _sequence(quote)
    before, after = quote[: cues[-1].start()], quote[cues[-1].end() :]
    replaced: set[str] = set()
    if numbers(after):
        replaced |= set(numbers(before))
    for kind in (_date_terms, _drug_terms):
        if kind(after):
            replaced |= {_word_key(term) for term in kind(before)}
    return [word for word in _sequence(before) if word not in replaced] + _sequence(after)


# Skipped freely between a value's words, like connecting words: a value may leave out a dose ("takes lisinopril
# daily" from "takes lisinopril 10 mg daily"), though never a word that changes what it says.
DOSE_WORDS = frozenset({"mg", "mcg", "ml", "kg", "g", "unit", "percent"})


def _skippable(word: str) -> bool:
    return word in CONNECTING or word in DOSE_WORDS or word[:1].isdigit()


def _in_order(wanted: list[str], source: list[str]) -> bool:
    """Whether `wanted` can be found in order in `source`, with at most MAX_GAP non-skippable words between two of them.
    Each (word, position) pair is tried once, so repeated words can't make the search blow up."""
    tried: set[tuple[int, int]] = set()

    def onward(k: int, position: int) -> bool:
        if k == len(wanted):
            return True
        if (k, position) in tried:
            return False
        tried.add((k, position))
        skipped = 0
        for j in range(position + 1, len(source)):
            if source[j] == wanted[k] and onward(k + 1, j):
                return True
            if not _skippable(source[j]):
                skipped += 1
                if skipped > MAX_GAP:
                    return False
        return False

    return any(onward(1, i) for i, word in enumerate(source) if word == wanted[0])


NEGATION = re.compile(
    r"\b(?:no|not|denies|denied|deny|without|never|none|negative for|absence of|free of|ruled out|rules out|"
    r"doesn't|does not|didn't|did not|isn't|is not|wasn't|was not|hasn't|has not|haven't|have not|non)\b",
    re.IGNORECASE,
)
# Cues that negate what comes before them: "chest pain denied", "fracture ruled out".
POST_NEGATION = re.compile(r"\b(?:denied|ruled out|negative|absent|not present|not seen)\b", re.IGNORECASE)
TERMINATORS = frozenset("but however although though except apart aside yet which who whereas".split())


def _negated(text: str) -> tuple[set[str], set[str]]:
    """Content-word stems the text negates, and those it mentions without a negation."""
    words = tokens(text)
    negated: set[int] = set()

    def stops(between: str, word: str) -> bool:
        return word in TERMINATORS or any(mark in between for mark in ".,;:?!\n")

    for cue in NEGATION.finditer(text):
        edge = cue.end()
        for i in [i for i, token in enumerate(words) if token.start >= cue.end()][:NEGATION_SCOPE]:
            if stops(text[edge : words[i].start], words[i].text):
                break
            negated.add(i)
            edge = words[i].end
    for cue in POST_NEGATION.finditer(text):
        edge = cue.start()
        for i in reversed([i for i, token in enumerate(words) if token.end <= cue.start()][-POST_SCOPE:]):
            if stops(text[words[i].end : edge], words[i].text):
                break
            negated.add(i)
            edge = words[i].start
    content = [(i, token) for i, token in enumerate(words) if _is_content(token.text)]
    return (
        {stem(token.text) for i, token in content if i in negated},
        {stem(token.text) for i, token in content if i not in negated},
    )


def _negation_dropped(value: str, quotes: list[str]) -> bool:
    """Whether the value affirms what any one of its sources negates, even if another source affirms it."""
    value_negated, value_affirmed = _negated(value)
    affirmed = value_affirmed - value_negated
    return any(_negated(quote)[0] & affirmed for quote in quotes)


def _is_content(word: str) -> bool:
    return word.isalpha() and len(word) >= 3 and word not in REFORMULATION
