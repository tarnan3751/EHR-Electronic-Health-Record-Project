"""Checks every value K2 Horizon proposes before a clinician can see it (FR-12). A value that fails any check is never
shown and its field is left empty; only the reason codes go out, never the text. A value that passes is held as pending
until a clinician approves it (FR-13), with the positions in the transcript of the passages it came from (FR-08).

The checks, each with the reason code recorded when it fails:

- unknown_field: a key outside history, assessment and plan (the schema allows none; this is the backstop).
- not_dictated: an empty value. Not a failure: the transcript said nothing for that field.
- too_long: over the field's length limit, over 12 sources, or a source over 600 characters.
- bad_format: control characters, markup, a link, code or JSON in the value.
- order_or_allergy_change (FR-14): the value tells someone to place an order, send or refill a prescription, or change
  the allergy list; or one of its sources quotes such an instruction in the transcript, so the instruction can't reach
  the note through another field either. Documentation passes: "start lisinopril 10 mg", "allergic to penicillin",
  "her cardiologist ordered an echo".
- no_sources, source_too_short (under two words, without a number), source_not_in_transcript (compared word by word,
  ignoring case and punctuation), source_in_unclear_passage, source_superseded (the clinician corrected it: "sorry",
  "I mean", "scratch that" within three words after it in the same sentence, or inside it before the corrected words).
- number_not_in_sources, date_not_in_sources, drug_not_in_sources: every number, date word and drug-like name in the
  value must be in the value's own sources.
- negation_dropped: a finding the sources negate ("denies chest pain") appears in the value without a negation.
- unsupported_content: more than a quarter of the value's content words aren't in its sources.

The word lists and thresholds are deliberately simple; the eval (eval/) measures what they let through and what they
throw away. VERSION goes up whenever a rule changes, so results from older rules are told apart.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field

from .prompt import FIELDS
from .textnorm import STOPWORDS, Token, numbers, stem, tokens
from .transcript import Transcript, utf16_offset

VERSION = 1

LIMITS = {"history": 2000, "assessment": 1200, "plan": 1200}
MAX_SOURCES = 12
MAX_QUOTE = 600
UNSUPPORTED_LIMIT = 0.25
CORRECTION_WINDOW = 3  # words after a quoted passage, within its sentence
NEGATION_SCOPE = 6  # words after a negation cue
DIRECTIVE_REACH = 10  # words after an instruction that still belong to it

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
    "unsupported_content",
)


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


def check(fields: dict[str, dict], unknown: list[str], transcript: Transcript) -> Checks:
    words = tokens(transcript.text)
    unclear = transcript.unclear_ranges()
    directives = directive_spans(transcript.text)
    checked = {
        name: _check_field(name, fields[name]["value"], fields[name]["sources"], transcript.text, words, unclear, directives)
        for name in FIELDS
    }
    return Checks(checked, list(unknown))


def _check_field(
    name: str,
    value: str,
    quotes: list[str],
    text: str,
    words: list[Token],
    unclear: list[tuple[int, int]],
    directives: list[tuple[int, int]],
) -> Checked:
    value = value.strip()
    quotes = [quote.strip() for quote in quotes if quote.strip()]
    if not value:
        return Checked(name, value, quotes, ["not_dictated"])

    reasons: set[str] = set()
    if len(value) > LIMITS[name] or len(quotes) > MAX_SOURCES or any(len(quote) > MAX_QUOTE for quote in quotes):
        reasons.add("too_long")
    if BAD_FORMAT.search(value):
        reasons.add("bad_format")
    if directive_spans(value):
        reasons.add("order_or_allergy_change")
    if not quotes:
        reasons.add("no_sources")

    spans: list[tuple[int, int]] = []
    for quote in quotes:
        quote_words = tokens(quote)
        if UNCLEAR_QUOTE.search(quote):
            reasons.add("source_in_unclear_passage")
            continue
        if len(quote_words) < 2 and not any(word.text[0].isdigit() for word in quote_words):
            reasons.add("source_too_short")
            continue
        found = _locate(quote_words, words, unclear)
        if isinstance(found, str):
            reasons.add(found)
            continue
        spans.append(found)
        if any(_overlaps(found, directive) for directive in directives):
            reasons.add("order_or_allergy_change")
        if _followed_by_correction(text, words, found[1]):
            reasons.add("source_superseded")

    sources = " ".join(quotes)
    if _superseded_terms(quotes) & (set(numbers(value)) | _drug_terms(value) | _date_terms(value)):
        reasons.add("source_superseded")
    if not set(numbers(value)) <= set(numbers(sources)):
        reasons.add("number_not_in_sources")
    if not _date_terms(value) <= _date_terms(sources):
        reasons.add("date_not_in_sources")
    if not _drug_terms(value) <= {token.text for token in tokens(sources)}:
        reasons.add("drug_not_in_sources")
    if _negation_dropped(value, quotes):
        reasons.add("negation_dropped")
    if _unsupported_share(value, sources) > UNSUPPORTED_LIMIT:
        reasons.add("unsupported_content")

    ordered = [reason for reason in REASONS if reason in reasons]
    return Checked(name, value, quotes, ordered, spans if not ordered else [])


# ---- Quotes --------------------------------------------------------------------------------------------------------

UNCLEAR_QUOTE = re.compile(r"\[\s*unclear\s*\]", re.IGNORECASE)


def _locate(quote: list[Token], words: list[Token], unclear: list[tuple[int, int]]) -> tuple[int, int] | str:
    """The quote's place in the transcript, word for word: the first one outside an unclear passage, or why not."""
    size = len(quote)
    wanted = [token.text for token in quote]
    found = [
        (words[i].start, words[i + size - 1].end)
        for i in range(len(words) - size + 1)
        if [token.text for token in words[i : i + size]] == wanted
    ]
    if not found:
        return "source_not_in_transcript"
    clear = [span for span in found if not any(_overlaps(span, passage) for passage in unclear)]
    return clear[0] if clear else "source_in_unclear_passage"


def _overlaps(a: tuple[int, int], b: tuple[int, int]) -> bool:
    return a[0] < b[1] and b[0] < a[1]


CORRECTION = re.compile(
    r"\b(?:no wait|wait no|sorry|i mean|i meant|correction|scratch that|strike that|make that|i misspoke)\b",
    re.IGNORECASE,
)


def _followed_by_correction(text: str, words: list[Token], end: int) -> bool:
    following = [token for token in words if token.start >= end][:CORRECTION_WINDOW]
    window = text[end : following[-1].end] if following else ""
    sentence_end = min((i for i in (window.find(mark) for mark in ".?!\n") if i != -1), default=len(window))
    return bool(CORRECTION.search(window[:sentence_end]))


def _superseded_terms(quotes: list[str]) -> set[str]:
    """Numbers, drugs and dates a quote says before a correction inside it, and nowhere after or elsewhere."""
    superseded: set[str] = set()
    kept: set[str] = set()
    for quote in quotes:
        cues = list(CORRECTION.finditer(quote))
        before, after = (quote[: cues[-1].start()], quote[cues[-1].end() :]) if cues else ("", quote)
        superseded |= set(numbers(before)) | _drug_terms(before) | _date_terms(before)
        kept |= set(numbers(after)) | _drug_terms(after) | _date_terms(after)
    return superseded - kept


# ---- Format --------------------------------------------------------------------------------------------------------

BAD_FORMAT = re.compile(r"[\x00-\x08\x0b-\x1f\x7f]|<[^>]*>|https?://|www\.|```|[\[\]{}]")


# ---- Orders and allergy-list changes (FR-14) ----------------------------------------------------------------------

_DET = r"(?:an?|the|her|his|their|this|that|my|our|its)"
_SAME_SENTENCE = r"(?:(?![.\n]).)"
_ORDER_VERB = re.compile(r"\b(?:order|e-?prescribe|prescribe|refill)\b", re.IGNORECASE)
# Words that make the verb a noun ("the order", "a refill"), or "in order to".
_NOUN_BEFORE = frozenset(
    "a an the this that her his their its my our your no any standing verbal written phone telephone in of lab".split()
)
_DIRECTIVES = [
    re.compile(
        rf"\b(?:place|put in|enter|send in|send over|send|call in|fax)\s+(?:{_DET}\s+)?(?:\w+\s+)?"
        r"(?:order|prescription|script|rx|refill)s?\b",
        re.IGNORECASE,
    ),
    re.compile(rf"\badd\b{_SAME_SENTENCE}{{0,40}}?\b(?:to|as|on)\s+(?:{_DET}\s+)?(?:drug\s+|medication\s+)?allerg", re.I),
    re.compile(rf"\badd\s+(?:{_DET}\s+)?(?:new\s+)?(?:\w+\s+)?allerg", re.IGNORECASE),
    re.compile(rf"\b(?:remove|delete|take)\b{_SAME_SENTENCE}{{0,40}}?\b(?:from|off)\s+(?:{_DET}\s+)?(?:drug\s+)?allerg", re.I),
    re.compile(rf"\b(?:remove|delete)\s+(?:{_DET}\s+)?(?:\w+\s+)?allerg", re.IGNORECASE),
    re.compile(rf"\b(?:update|change|edit|correct|amend)\s+(?:{_DET}\s+)?(?:drug\s+|medication\s+)?allerg", re.IGNORECASE),
    re.compile(rf"\b(?:mark|flag)\b{_SAME_SENTENCE}{{0,40}}?\ballerg", re.IGNORECASE),
    re.compile(rf"\b(?:enter|document)\s+(?:{_DET}\s+)?(?:new\s+)?(?:\w+\s+)?allerg", re.IGNORECASE),
    re.compile(rf"\blist\b{_SAME_SENTENCE}{{0,40}}?\bas\s+(?:an?\s+)?allerg", re.IGNORECASE),
    re.compile(rf"\bput\b{_SAME_SENTENCE}{{0,40}}?\b(?:on|in|into)\s+(?:{_DET}\s+)?allerg", re.IGNORECASE),
    re.compile(rf"\ballerg\w*\s+list\b{_SAME_SENTENCE}{{0,30}}?\b(?:add|remove|update|change|delete)\b", re.IGNORECASE),
]


def directive_spans(text: str) -> list[tuple[int, int]]:
    """Each instruction to place an order or change the allergy list: from a few words before it (its lead-in, such as
    "go ahead and") to the end of its sentence, or DIRECTIVE_REACH words on when the transcript has no punctuation."""
    words = tokens(text)
    matches: list[tuple[int, int]] = []
    for match in _ORDER_VERB.finditer(text):
        before = [token.text for token in words if token.end <= match.start()][-1:]
        after = text[match.end() : match.end() + 4].lower()
        if (before and before[0] in _NOUN_BEFORE) or after.startswith(" of"):
            continue
        matches.append((match.start(), match.end()))
    for pattern in _DIRECTIVES:
        matches += [(match.start(), match.end()) for match in pattern.finditer(text)]

    spans = []
    for start, end in sorted(matches):
        clause = max(text.rfind(mark, 0, start) for mark in ".!?;\n,") + 1
        lead = [token for token in words if token.end <= start][-4:]
        span_start = max(clause, lead[0].start if lead else 0)
        sentence = min((i for i in (text.find(mark, end) for mark in ".!?\n") if i != -1), default=len(text))
        reach = [token for token in words if token.start >= end][:DIRECTIVE_REACH]
        span_end = min(sentence, reach[-1].end if reach else end)
        spans.append((span_start, max(span_end, end)))
    return spans


# ---- Grounding -----------------------------------------------------------------------------------------------------

MONTHS = frozenset(
    "january february march april may june july august september october november december sept".split()
)
WEEKDAYS = frozenset("monday tuesday wednesday thursday friday saturday sunday".split())
RELATIVE = frozenset("today yesterday tomorrow tonight ago".split())
PERIOD_STARTS = frozenset("last next this past".split())
PERIODS = frozenset("week weeks month months year years weekend night morning evening".split()) | WEEKDAYS


def _date_terms(text: str) -> set[str]:
    found: set[str] = set()
    previous = ""
    for word in (token.text for token in tokens(text)):
        if word in MONTHS - {"may"} or word in WEEKDAYS or word in RELATIVE:
            found.add(word)
        if previous in PERIOD_STARTS and word in PERIODS:
            found.add(f"{previous} {word}")
        previous = word
    return found


# USAN stems: most drug names end in one, including new ones no list has heard of yet.
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


NEGATION = re.compile(
    r"\b(?:no|not|denies|denied|deny|without|never|none|negative for|absence of|free of|ruled out|rules out|"
    r"doesn't|does not|didn't|did not|isn't|is not|wasn't|was not|hasn't|has not|haven't|have not|non)\b",
    re.IGNORECASE,
)
# Cues that negate what comes before them: "chest pain denied", "fracture ruled out".
POST_NEGATION = re.compile(r"\b(?:denied|ruled out|negative|absent|not present|not seen)\b", re.IGNORECASE)
POST_SCOPE = 4
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
    negated: set[str] = set()
    affirmed: set[str] = set()
    for quote in quotes:
        quote_negated, quote_affirmed = _negated(quote)
        negated |= quote_negated
        affirmed |= quote_affirmed
    value_negated, value_affirmed = _negated(value)
    return bool((negated - affirmed) & (value_affirmed - value_negated))


FRAMING = frozenset(
    "patient pt reports reported report states stated says said presents presenting complains notes noted describes "
    "described".split()
)


def _is_content(word: str) -> bool:
    return word.isalpha() and len(word) >= 3 and word not in STOPWORDS and word not in FRAMING


def _unsupported_share(value: str, sources: str) -> float:
    content = [stem(token.text) for token in tokens(value) if _is_content(token.text)]
    if not content:
        return 0.0
    available = {stem(token.text) for token in tokens(sources)}
    return sum(1 for word in content if word not in available) / len(content)
