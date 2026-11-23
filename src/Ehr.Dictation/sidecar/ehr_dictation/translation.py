"""Checks every English value K2 Horizon proposes from a transcript dictated in Spanish (the expansion plan's step 17,
proposed FR-61) before a clinician can see it, as validation.py does for English dictation (FR-12). A value that fails
any check is never shown and its field is left empty; only the reason codes go out. A value that passes is pending
until a clinician approves it (FR-13), with each English statement's place in the value and the passages of the
Spanish transcript it was translated from (FR-08).

English words can't be found in a Spanish transcript, so grounding works statement by statement. The model answers
with one list of statements, each the Spanish passages it rests on, copied word for word, the field it belongs in, and
their English (note-fields-es.schema.json), which llama.parse_statements() sorts into each field's statements. For each
statement:

1. The English is cleaned as validation.py cleans a value, and bad_format applies the same way: a character the
   transcript never uses fails, so a word of Italian or Chinese written into the English ("attività", "咳嗽") does.
   Here, too, a word holding a letter English isn't written in (ENGLISH_LETTERS) must be a word of the transcript as
   said (an eponym, "Lasègue"), so such a word fails even where the transcript uses its letters elsewhere. An
   instruction in it to order something or change the allergy list is cut out by validation.py's own rule (the English
   backstop); a statement that was only an instruction is dropped.
2. Each Spanish passage must be in the transcript, word for word, ignoring case, punctuation and accents
   (source_not_in_transcript), not in a passage marked unclear, and two words or more unless it holds a number. Every
   check below reads the transcript's own words where the passage was found, never the model's copy. No word may be
   cited by two statements of the answer: one citing a word an earlier statement (in the answer's order) cites outside
   an instruction fails (source_repeated), so no passage is shown twice, in one field or two. What a statement cites
   is at most MAX_SPANISH characters of the transcript (too_long), the most the schema's two quotes of 200 allow and
   the most the on-prem app takes for a sentence (TrustedResult).
3. Only the words outside a Spanish instruction to order something or change the allergy list count (spanish.py,
   FR-14). A statement whose passages are all inside one is that instruction translated: it is dropped, and the field
   is empty (order_or_allergy_change) when nothing is left of it. The Spanish is read as corrected: without a
   correction cue and what it took back.
4. The English is compared with that Spanish, class by class. "No more" means the English may leave something out, as
   an English value may, but may not add or change it; "the same" means both ways.
   - numbers, as digits (Spanish decimal commas, number words, both readings of "1.000", and 1 for "un", "una" or
     "uno" even as an article): no more (number_not_in_sources), and none the clinician took back (source_superseded);
   - a dose unit with its number ("50 mcg" is not "50 mg"): no more (unit_not_in_sources). A unit of measurement
     (mmHg, beats a minute, degrees, percent) may be added to a number said without one, never changed;
   - how often (times a day, a week, as needed, at bedtime): no more (frequency_not_in_sources);
   - dates ("hace una semana" is a week ago, or the past week): no more (date_not_in_sources);
   - drug names, failing closed both ways: every English word a list knows as a drug (a USAN stem, drugnames.py's
     RxNorm ingredients, the words of their names and brand names) or that comes just before a dose ("zorbitex 20
     mg", whether a list knows it or not) must be a word of the Spanish or an English name one gives (spanish.py's
     endings and exceptions) (drug_not_in_sources); and every drug the Spanish names must be named in the English
     (drug_dropped), so a drug no list knows can't be swapped in for one the clinician said;
   - sides, routes, severity and abbreviations: no more (side_, route_, severity_ and abbreviation_not_in_sources);
   - medication changes (start, stop, hold, continue, increase, decrease, switch), each said of a drug, a dose or a
     number: the same (action_changed);
   - a negation, uncertainty, the past, or someone other than the patient having it, read in the transcript as
     validation.py reads them in English, with an English word that negates itself ("uncontrolled") a negation and a
     relative told something ("the mother is explained") not someone having it: the same (negation_dropped or
     negation_added, and so on), and the relatives named the same (experiencer_changed). A doubt is the clinician's
     whether it comes before what it doubts ("creo que", "quizá", "tal vez", "a lo mejor", "puede que", "podría
     tener") or closes its clause (", creo", ", probablemente"), as validation.py hears one in English ("I think she
     has", "maybe", "she might have", "probably", a closing ", I think"); and "no creo que tenga" is a negation, as
     validation.py reads "I don't think she has";
   - the English no more than EXTRA_WORDS content words longer than the Spanish (translation_adds_words): a clause the
     model added ("continue metformin and check kidney function" from "continuar metformina").
5. If any statement fails, the whole field is discarded, as an English value is.

What these don't check: the meaning of every other word. "dolor abdominal leve" translated "mild chest pain" passes
(the severity and the length agree), and so does a finding moved inside a statement; nor a drug name no list knows,
written with no dose just after it, for one no list knows either ("continuar plixor" written "continue vortanex"); nor
a word left in the English that isn't English, if the transcript says it or it is written in English's letters: a
Spanish word left untranslated ("pain in the rodilla", "pain in the región lumbar"), or a word of no language. A quote is matched whole word by whole word, so one the schema's length cut
short inside a word ("niega dis") is never taken for the word it begins: it isn't found. The
review shows each English statement beside its Spanish for the clinician to compare, and says so.

Warnings, each a code and a place in the transcript, never text, as validation.py's: possible_correction (a later
number, date or drug of the same kind just after one the value uses, with a Spanish correction-like word between),
near_unclear_passage, one_field_holds_dictation, and two of their own: ambiguous_number, on a value using a number
written with a separator that reads two ways ("1.000"), pointing at it; and ambiguous_frequency, on a value from
"cada tercer día" (every other day in Mexico, every third day literally), pointing at it.
"""

from __future__ import annotations

import re
import string
from dataclasses import dataclass, field

from . import drugnames, prompt, spanish, validation
from .prompt import FIELDS
from .textnorm import STOPWORDS, numbers, tokens
from .transcript import Transcript, utf16_offset

LIMITS = validation.LIMITS
MAX_STATEMENTS = 30
# What the Spanish note schema lets one statement hold (note-fields-es.schema.json), read from it so the two can't
# differ: two quotes of 200 characters, and 240 characters of English.
_SCHEMA = prompt.load(language="es").schema["properties"]["statements"]["items"]["properties"]
MAX_QUOTES = _SCHEMA["spanish"]["maxItems"]
MAX_QUOTE = _SCHEMA["spanish"]["items"]["maxLength"]
MAX_ENGLISH = _SCHEMA["english"]["maxLength"]
MAX_SPANISH = 400  # UTF-16 code units of the transcript a statement's sources cover, as the on-prem app counts them
EXTRA_WORDS = 2  # content words the English of a statement may have beyond its Spanish
CORRECTION_WINDOW = 3  # words after a correction cue within which the corrected value starts
WARNING_REACH = validation.WARNING_REACH
NEAR_UNCLEAR = validation.NEAR_UNCLEAR

AMBIGUOUS_NUMBER = "ambiguous_number"
AMBIGUOUS_FREQUENCY = "ambiguous_frequency"
WARNINGS = (validation.POSSIBLE_CORRECTION, validation.NEAR_UNCLEAR_PASSAGE, validation.ONE_FIELD_HOLDS_DICTATION,
            AMBIGUOUS_NUMBER, AMBIGUOUS_FREQUENCY)

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
    "source_repeated",
    "source_superseded",
    "number_not_in_sources",
    "unit_not_in_sources",
    "frequency_not_in_sources",
    "date_not_in_sources",
    "drug_not_in_sources",
    "drug_dropped",
    "side_not_in_sources",
    "route_not_in_sources",
    "severity_not_in_sources",
    "abbreviation_not_in_sources",
    "action_changed",
    "negation_dropped",
    "negation_added",
    "certainty_dropped",
    "certainty_added",
    "history_dropped",
    "history_added",
    "experiencer_dropped",
    "experiencer_added",
    "experiencer_changed",
    "translation_adds_words",
)
LOCATING = frozenset({"source_too_short", "source_not_in_transcript", "source_in_unclear_passage"})
# The letters English is written in, and the one other the English checks read (the μ of "μg"). A word of a
# statement's English holding any other must be a word of the transcript (step 1 of the header).
ENGLISH_LETTERS = frozenset(string.ascii_lowercase + "μ")


# ---- What goes out ---------------------------------------------------------------------------------------------------


@dataclass
class Translated:
    name: str
    value: str
    reasons: list[str]
    spans: list[tuple[int, int]] = field(default_factory=list)  # code points in the transcript, every statement's
    # Each statement's place in the value (code points, end exclusive) and its spans in the transcript.
    statements: list[tuple[int, int, list[tuple[int, int]]]] = field(default_factory=list)
    warnings: list[tuple[str, int, int]] = field(default_factory=list)

    @property
    def pending(self) -> bool:
        return not self.reasons

    def to_json(self, transcript: Transcript) -> dict:
        if not self.pending:
            return {"status": "empty", "reasons": self.reasons}

        def spans(found: list[tuple[int, int]]) -> list[dict]:
            return [{"start": utf16_offset(transcript.text, s), "end": utf16_offset(transcript.text, e)} for s, e in found]

        return {
            "status": "pending",
            "value": self.value,
            "sources": spans(self.spans),
            "statements": [
                {"start": utf16_offset(self.value, start), "end": utf16_offset(self.value, end), "sources": spans(found)}
                for start, end, found in self.statements
            ],
            "warnings": [{"code": code, "start": utf16_offset(transcript.text, s), "end": utf16_offset(transcript.text, e)}
                         for code, s, e in self.warnings],
        }


@dataclass
class Translations:
    fields: dict[str, Translated]
    unknown: list[str]

    def to_json(self, transcript: Transcript) -> dict:
        return {name: checked.to_json(transcript) for name, checked in self.fields.items()}


def not_carried_over(transcript: Transcript) -> list[dict]:
    """Each instruction in the Spanish transcript to order something or change the allergy list, which the note never
    carries: its kind and where the clinician said it, as UTF-16 offsets, the shape validation.not_carried_over() gives."""
    return [
        {"reason": kind, "start": utf16_offset(transcript.text, start), "end": utf16_offset(transcript.text, end)}
        for start, end, kind in spanish.directives(transcript.text)
    ]


# ---- The transcript, read once ------------------------------------------------------------------------------------


@dataclass(frozen=True)
class _Term:
    """A number, drug, side or date in the transcript: what it can be read as, and its words (inclusive)."""

    kind: str
    values: frozenset[str]
    first: int
    last: int
    ambiguous: bool = False


@dataclass
class _Context:
    text: str
    toks: list[spanish.Token]
    unclear: list[tuple[int, int]]
    directives: list[tuple[int, int]]
    letters: frozenset[str]
    words: frozenset[str]  # every word of the transcript as said, lower case, accents kept
    terms: list[_Term]
    measured: list[tuple[spanish.Number, str]]
    cues: set[int]
    taken_back: set[int]
    qualified: dict[str, set[int]]


GLUE = frozenset({"sobre", "de", "el", "la", "los", "las", "un", "una"}) | spanish.UNIT_WORDS


def _terms(toks: list[spanish.Token]) -> list[_Term]:
    found = [_Term("number", n.readings, n.first, n.last, n.ambiguous) for n in spanish.numbers(toks)]
    for i, token in enumerate(toks):
        if (token.key.isalpha() and len(token.key) >= 4 and token.key not in spanish.NOT_DRUGS
                and any(is_drug(name) for name in spanish.drug_candidates(token.key))):
            found.append(_Term("drug", frozenset(spanish.drug_candidates(token.key)), i, i))
        elif token.key in spanish.SIDES:
            found.append(_Term("side", frozenset({spanish.SIDES[token.key]}), i, i))
        elif token.key in spanish.MONTHS or token.key in spanish.WEEKDAYS:
            found.append(_Term("date", frozenset({spanish.MONTHS.get(token.key) or spanish.WEEKDAYS[token.key]}), i, i))
    return sorted(found, key=lambda term: (term.first, term.kind))


def _sentences(text: str, toks: list[spanish.Token]) -> list[int]:
    marks = [m.end() for m in spanish.SENTENCE_MARK.finditer(text)]
    return [sum(1 for mark in marks if mark <= token.start) for token in toks]


def _corrections(text: str, toks: list[spanish.Token], terms: list[_Term]) -> tuple[set[int], set[int]]:
    """What the clinician's corrections change, as validation.py reads them in English: the words of each cue that
    corrected something, and the words each took back. A correction gives a number, drug, side or date within
    CORRECTION_WINDOW words of its cue, and takes back the last of its kind said before the cue in its sentence (or,
    for a cue that starts a sentence, the three before), with the numbers joined to it ("142 sobre 88"), unless it says
    all of it again."""
    sentence = _sentences(text, toks)
    cues: set[int] = set()
    taken_back: set[int] = set()
    for first, last in spanish.correction_cues(text, toks):
        before = {i for i in range(first) if sentence[i] == sentence[first]}
        if not before and first:
            before = {i for i in range(first) if sentence[first - 1] - 3 < sentence[i] <= sentence[first - 1]}
        for kind in ("number", "drug", "side", "date"):
            following = [t for t in terms if t.kind == kind and last < t.first <= last + CORRECTION_WINDOW]
            preceding = [t for t in terms if t.kind == kind and t.last < first and t.first in before]
            if not following or not preceding:
                continue
            replaced = [preceding[-1]]
            for term in reversed(preceding[:-1]):
                if all(toks[i].key in GLUE for i in range(term.last + 1, replaced[0].first)):
                    replaced.insert(0, term)
                else:
                    break
            again = {value for term in following for value in term.values}
            if all(term.values & again for term in replaced):
                continue
            end = replaced[-1].last + 1
            taken_back.update(range(replaced[0].first, end + 1 if first - end == 1 else end))
            cues.update(range(first, last + 1))
    return cues, taken_back


def _context(transcript: Transcript) -> _Context:
    text = transcript.text
    toks = spanish.tokens(text)
    terms = _terms(toks)
    cues, taken_back = _corrections(text, toks, terms)
    return _Context(
        text=text,
        toks=toks,
        unclear=transcript.unclear_ranges(),
        directives=[(start, end) for start, end, _ in spanish.directives(text)],
        letters=frozenset(validation.clean(text).casefold()),
        words=frozenset(t.text for t in spanish.tokens(validation.clean(text))),
        terms=terms,
        measured=spanish.measured(text, toks),
        cues=cues,
        taken_back=taken_back,
        qualified=spanish.qualified(text, toks),
    )


# ---- The English side ------------------------------------------------------------------------------------------------

EXCEPTION_NAMES = frozenset(name for names in spanish.DRUG_EXCEPTIONS.values() for name in names)


def is_drug(word: str) -> bool:
    """An English drug name: a USAN stem (validation.py), an RxNorm ingredient, a word of one or a brand name
    (drugnames.py), or an exception's."""
    return validation._is_drug(word) or word in drugnames.NAMES or word in EXCEPTION_NAMES


ALPHANUMERIC = re.compile(r"\b(?=\w*[A-Za-z])(?=\w*\d)\w+\b")


def english_numbers(text: str) -> list[str]:
    """The numbers in English text, as digits, a comma before three digits read as a thousands separator. The digit in
    an abbreviation ("A1c", "SpO2", "T2DM") is part of its name, which the abbreviation check compares."""
    return numbers(ALPHANUMERIC.sub(" ", re.sub(r"(?<=\d),(?=\d{3}\b)", "", text)))


DOSE_UNITS = frozenset({"mg", "mcg", "g", "kg", "ml", "l", "unit", "mg/dl"})  # never added to a number said without
ENGLISH_UNITS = re.compile(
    r"(\d+(?:\.\d+)?)\s*(mg/dl|micrograms?|mcg|μg|µg|milligrams?|mg|grams?|g|kilograms?|kilos?|kg|milliliters?|"
    r"millilitres?|ml|cc|liters?|litres?|l|units?|iu|percent|%|degrees?|°|mmhg|bpm|beats (?:a|per) minute|"
    r"breaths (?:a|per) minute)(?![a-z])",
    re.IGNORECASE,
)
UNIT_NAMES = {
    "microgram": "mcg", "mcg": "mcg", "μg": "mcg", "µg": "mcg", "milligram": "mg", "mg": "mg", "gram": "g", "g": "g",
    "kilogram": "kg", "kilo": "kg", "kg": "kg", "milliliter": "ml", "millilitre": "ml", "ml": "ml", "cc": "ml",
    "liter": "l", "litre": "l", "l": "l", "unit": "unit", "iu": "unit", "percent": "percent", "%": "percent",
    "degree": "degree", "°": "degree", "mmhg": "mmhg", "bpm": "bpm", "mg/dl": "mg/dl",
}


def english_units(text: str) -> set[tuple[str, str]]:
    found = set()
    for match in ENGLISH_UNITS.finditer(re.sub(r"(?<=\d),(?=\d{3}\b)", "", text)):
        unit = match.group(2).lower()
        name = "bpm" if unit.startswith("beats") else "rpm" if unit.startswith("breaths") else UNIT_NAMES.get(unit.rstrip("s"), UNIT_NAMES.get(unit))
        if name:
            found.add((numbers(match.group(1))[0], name))
    return found


_EN_NUMBER = r"(\d+|one|two|three|four|five|six|eight|twelve|twenty-four|twenty four)"
_EN_COUNTS = {"one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "eight": 8, "twelve": 12,
              "twenty-four": 24, "twenty four": 24}


def _en(word: str) -> int:
    return int(word) if word.isdigit() else _EN_COUNTS[word]


ENGLISH_FREQUENCIES = [
    (re.compile(r"\b(?:twice|two times) (?:a|per|each) day\b|\btwice daily\b|\bbid\b|\bb\.i\.d\."), lambda m: {"2/d"}),
    (re.compile(r"\b(?:three times|thrice) (?:a|per|each) day\b|\bthree times daily\b|\btid\b"), lambda m: {"3/d"}),
    (re.compile(r"\bfour times (?:a|per|each) day\b|\bfour times daily\b|\bqid\b"), lambda m: {"4/d"}),
    (re.compile(r"\b(\d+) times (?:a|per|each) day\b"), lambda m: {f"{m.group(1)}/d"}),
    (re.compile(r"\b(?:twice|two times) (?:a|per|each) week\b"), lambda m: {"2/w"}),
    (re.compile(r"\b(\d+) times (?:a|per|each) week\b"), lambda m: {f"{m.group(1)}/w"}),
    (re.compile(rf"\bevery {_EN_NUMBER} hours?\b"), lambda m: {spanish._per_day(_en(m.group(1)))}),
    (re.compile(r"\bq(\d+)h\b"), lambda m: {spanish._per_day(int(m.group(1)))}),
    (re.compile(r"\bevery other day\b|\bqod\b|\balternate days\b|\bon alternate days\b"), lambda m: {"1/2d"}),
    (re.compile(r"\bevery third day\b"), lambda m: {"1/3d"}),
    (re.compile(rf"\bevery {_EN_NUMBER} days\b"), lambda m: {f"1/{_en(m.group(1))}d"}),
    (re.compile(rf"\bevery {_EN_NUMBER} weeks\b"), lambda m: {f"1/{_en(m.group(1))}w"}),
    (re.compile(rf"\bevery {_EN_NUMBER} months\b"), lambda m: {f"1/{_en(m.group(1))}m"}),
    (re.compile(r"\bonce (?:a|per) week\b|\bweekly\b|\bevery week\b|\bonce weekly\b"), lambda m: {"1/w"}),
    (re.compile(r"\bonce (?:a|per) month\b|\bmonthly\b|\bevery month\b"), lambda m: {"1/m"}),
    (re.compile(r"\bonce (?:a|per) day\b|\bonce daily\b|\bdaily\b|\bevery day\b|\bqd\b|\b(?:a|per|each) day\b"),
     lambda m: {"1/d"}),
    (re.compile(r"\bas needed\b|\bprn\b|\bif needed\b|\bwhen needed\b"), lambda m: {"prn"}),
    (re.compile(r"\bat bedtime\b|\bat night\b|\bnightly\b|\bevery night\b|\bqhs\b|\bbefore bed\b"), lambda m: {"hs"}),
    (re.compile(r"\bin the morning\b|\bevery morning\b|\bqam\b"), lambda m: {"am"}),
    (re.compile(r"\bbefore (?:meals|eating|each meal)\b"), lambda m: {"ac"}),
    (re.compile(r"\bafter (?:meals|eating|each meal)\b"), lambda m: {"pc"}),
    (re.compile(r"\bwith (?:meals|food)\b"), lambda m: {"with-meals"}),
    (re.compile(r"\bfasting\b|\bon an empty stomach\b"), lambda m: {"fasting"}),
]


def english_frequencies(text: str) -> set[str]:
    lowered = text.lower()
    taken: list[tuple[int, int]] = []
    found: set[str] = set()
    for pattern, read in ENGLISH_FREQUENCIES:
        for match in pattern.finditer(lowered):
            if any(match.start() < end and start < match.end() for start, end in taken):
                continue
            taken.append(match.span())
            found |= read(match)
    return found


ENGLISH_ROUTES = [
    ("oral", re.compile(r"\b(?:oral(?:ly)?|by mouth|po)\b")),
    ("iv", re.compile(r"\b(?:iv|intravenous(?:ly)?)\b")),
    ("im", re.compile(r"\b(?:im|intramuscular(?:ly)?)\b")),
    ("sc", re.compile(r"\b(?:sc|subq|sub-q|subcut|subcutaneous(?:ly)?)\b")),
    ("inhaled", re.compile(r"\b(?:inhaled|inhalations?|inhalers?|nebuli[sz]ed|nebuli[sz]ers?|nebuli[sz]ations?)\b")),
    ("topical", re.compile(r"\btopical(?:ly)?\b")),
    ("sublingual", re.compile(r"\bsublingual(?:ly)?\b")),
    ("rectal", re.compile(r"\brectal(?:ly)?\b")),
    ("vaginal", re.compile(r"\bvaginal(?:ly)?\b")),
    ("ophthalmic", re.compile(r"\b(?:ophthalmic|eye drops?)\b")),
    ("otic", re.compile(r"\b(?:otic|ear drops?)\b")),
    ("nasal", re.compile(r"\b(?:nasal(?:ly)?|intranasal(?:ly)?)\b")),
]


def english_routes(text: str) -> set[str]:
    lowered = text.lower()
    return {name for name, pattern in ENGLISH_ROUTES if pattern.search(lowered)}


ENGLISH_SIDES = {"left": "left", "right": "right", "bilateral": "bilateral", "bilaterally": "bilateral", "both": "bilateral"}
ENGLISH_SEVERITY = frozenset({"mild", "moderate", "severe", "intense", "critical"})
ENGLISH_RELATIVES = {
    "mother": "mother", "mom": "mother", "mum": "mother", "father": "father", "dad": "father", "parent": "parent",
    "parents": "parent", "brother": "brother", "brothers": "brother", "sister": "sister", "sisters": "sister",
    "sibling": "sibling", "siblings": "sibling", "son": "son", "sons": "son", "daughter": "daughter",
    "daughters": "daughter", "child": "child", "children": "child", "grandfather": "grandfather",
    "grandmother": "grandmother", "grandparent": "grandparent", "grandparents": "grandparent", "uncle": "uncle",
    "uncles": "uncle", "aunt": "aunt", "aunts": "aunt", "cousin": "cousin", "cousins": "cousin", "husband": "husband",
    "wife": "wife", "spouse": "spouse", "partner": "partner", "nephew": "nephew", "nephews": "nephew", "niece": "niece",
    "nieces": "niece", "grandson": "grandson", "granddaughter": "granddaughter", "grandchild": "grandchild",
    "grandchildren": "grandchild", "maternal": "maternal", "paternal": "paternal",
}
# Abbreviations whose meaning another check covers (routes, how often, units), or that aren't clinical.
NOT_ABBREVIATIONS = frozenset(
    "PO IV IM SC PRN BID TID QID QD QHS QOD QAM AM PM OK MG ML KG MCG IU II III".split()
)
ABBREVIATION = re.compile(r"\b[A-Za-z][A-Za-z0-9]*[A-Za-z0-9]\b")


def english_abbreviations(text: str) -> set[str]:
    found = set()
    for match in ABBREVIATION.finditer(text):
        word = match.group()
        capitals = sum(1 for c in word if c.isupper())
        if (capitals >= 2 or (capitals and any(c.isdigit() for c in word))) and word.upper() not in NOT_ABBREVIATIONS:
            found.add(word.upper())
    return found


ACTIONS = {
    "start": "start", "starts": "start", "started": "start", "starting": "start", "begin": "start",
    "begins": "start", "began": "start", "initiate": "start", "initiated": "start", "add": "start", "added": "start",
    "adding": "start", "stop": "stop", "stops": "stop", "stopped": "stop", "stopping": "stop", "discontinue": "stop",
    "discontinued": "stop", "withdraw": "stop", "withdrawn": "stop", "hold": "hold", "held": "hold", "pause": "hold",
    "paused": "hold", "omit": "hold", "continue": "continue", "continues": "continue", "continued": "continue",
    "keep": "continue", "maintain": "continue", "maintained": "continue", "increase": "increase",
    "increased": "increase", "increasing": "increase", "raise": "increase", "raised": "increase", "uptitrate": "increase",
    "decrease": "decrease", "decreased": "decrease", "decreasing": "decrease", "reduce": "decrease",
    "reduced": "decrease", "lower": "decrease", "lowered": "decrease", "taper": "decrease", "tapered": "decrease",
    "switch": "switch", "switched": "switch", "switching": "switch",
    # "Suspender" said, "suspend" written: a stop, as the prompt asks it written.
    "suspend": "stop", "suspended": "stop", "suspending": "stop",
}
SPANISH_ACTIONS = {
    "start": "inicia iniciar inicio iniciamos inicie inicien iniciare iniciaremos iniciando empezar empieza empiece "
             "empiecen empezamos empezaremos empezando comenzar comienza comience comenzamos comenzaremos agregar agrega "
             "agrego agregue agregamos agregaremos anadir anade anado anada anadimos pautar pauto pauta paute pautamos",
    "stop": "suspender suspende suspendo suspenda suspendemos suspenderemos retirar retira retiro retire retiramos "
            "descontinuar descontinua descontinuo descontinue descontinuamos quitar quita quito quite quitamos parar "
            "paramos pare",
    "continue": "continuar continua continuo continue continuamos continuaremos seguir sigue siga seguimos seguiremos "
                "mantener mantiene mantengo mantenga mantenemos mantendremos",
    "increase": "aumentar aumenta aumento aumente aumentamos subir sube subo suba subimos incrementar incrementa "
                "incremento incremente incrementamos titular",
    "decrease": "disminuir disminuye disminuyo disminuya disminuimos bajar baja bajo baje bajamos reducir reduce reduzco "
                "reduzca reducimos",
}
SPANISH_ACTION = {form: kind for kind, forms in SPANISH_ACTIONS.items() for form in forms.split()}
# Several words: "dejar de" (stop), "suspender temporalmente" (hold), "cambiar a" (switch).
SPANISH_ACTION_PHRASES = [
    (("suspender", "temporalmente"), "hold"), (("suspende", "temporalmente"), "hold"),
    (("suspendo", "temporalmente"), "hold"), (("dejar", "de"), "stop"), (("deja", "de"), "stop"), (("dejo", "de"), "stop"),
    (("deje", "de"), "stop"), (("dejamos", "de"), "stop"), (("cambiar", "a"), "switch"), (("cambia", "a"), "switch"),
    (("cambio", "a"), "switch"), (("cambie", "a"), "switch"), (("cambiamos", "a"), "switch"), (("pausar",), "hold"),
    (("pausa",), "hold"), (("omitir",), "hold"), (("omita",), "hold"),
]
OBJECT_WORDS = frozenset("dose doses dosage medication medications treatment therapy dosis tratamiento medicamento "
                         "medicamentos medicacion farmaco farmacos".split())
OBJECT_REACH = 4  # words after an action within which its drug, dose or number must come


def english_actions(text: str) -> set[str]:
    words = [t.text for t in tokens(text)]
    found = set()
    for i, word in enumerate(words):
        if word in ACTIONS and any(w in OBJECT_WORDS or w[0].isdigit() or is_drug(w) for w in words[i + 1 : i + 1 + OBJECT_REACH]):
            found.add(ACTIONS[word])
    return found


# A word just before a dose is a drug's name whether a list knows it or not ("zorbitex 20 mg", "kraxin 2 puffs"):
# unless it is a function word, a number, a route, a dose form, a salt or release ("valproic acid 500 mg", "extended
# release 500 mg"), a measurement, a laboratory or diet word, or a word that says how much or how it is taken ("weight
# 11 kg" has no dose unit; "take 2 tablets", "sodium 2 g").
DOSE = re.compile(
    r"\b([a-z]{3,})\s+\d+(?:[.,]\d+)?\s*(?:mg|mcg|μg|µg|g|ml|units?|iu|micrograms?|milligrams?|grams?|milliliters?|"
    r"millilitres?|puffs?|tablets?|capsules?|drops?|sprays?|inhalations?|ampoules?|ampules?|vials?|patch(?:es)?|"
    r"sachets?|lozenges?|suppositor(?:y|ies)|pills?)(?![a-z/])"
)
NOT_DOSED = frozenset(
    """weight weighs weighed height temperature glucose sugar hemoglobin creatinine potassium sodium calcium magnesium
    albumin protein fiber salt fat carbohydrate carbohydrates cholesterol triglycerides bilirubin urine output intake
    fluid fluids loss drained drainage voided volume residual blood take takes taking took give gives given giving use
    uses using used dose doses dosage total extra another additional maximum max minimum daily nightly weekly twice
    once now plus except approximately about around nearly almost least single double half bolus oral orally inhaled
    nebulized topical topically sublingual intravenous intramuscular subcutaneous rectal vaginal nasal intranasal
    ophthalmic otic tablet tablets capsule capsules puff puffs drop drops spray sprays patch inhaler injection solution
    suspension syrup cream ointment gel units acid hydrochloride hcl sulfate phosphate acetate citrate succinate
    tartrate maleate mesylate besylate fumarate bromide chloride carbonate bicarbonate hyclate monohydrate extended
    delayed release""".split()
) | frozenset(ACTIONS) | validation.NUMBER_WORDS


def dosed(text: str) -> set[str]:
    """The words just before a dose that no rule says aren't a drug's name (DOSE)."""
    return {m.group(1) for m in DOSE.finditer(text.lower())
            if m.group(1) not in STOPWORDS and m.group(1) not in NOT_DOSED}


# A past tense ends in an accented vowel ("dejó de fumar", "aumentó", "suspendió"), and is what happened, not a change
# made now; without its accent it would read as a present ("dejo de", "aumento").
PAST = re.compile(r"[áéíóú]$")


def spanish_actions(said: list[spanish.Token]) -> set[str]:
    keys = [t.key for t in said]
    found = set()
    i = 0
    while i < len(keys):
        if PAST.search(said[i].text):
            i += 1
            continue
        phrase = next(((words, kind) for words, kind in SPANISH_ACTION_PHRASES if tuple(keys[i : i + len(words)]) == words), None)
        words, kind = phrase if phrase else ((keys[i],), SPANISH_ACTION.get(keys[i]))
        after = keys[i + len(words) : i + len(words) + OBJECT_REACH]
        if kind and any(w in OBJECT_WORDS or w[0].isdigit() or any(is_drug(c) for c in spanish.drug_candidates(w)) for w in after):
            found.add(kind)
        i += len(words)
    return found


def english_content(text: str) -> int:
    return sum(1 for t in tokens(text) if t.text.isalpha() and len(t.text) >= 2 and t.text not in STOPWORDS
               and t.text not in validation.FREE_WORDS)


def spanish_content(said: list[spanish.Token]) -> int:
    return sum(1 for t in said if spanish._is_content(t))


# English for what spanish.py's lists say that validation.py's English cues don't: "infarto previo" is a prior MI,
# "neumonía en estudio" pneumonia under investigation, "no se puede descartar" can't be ruled out.
MORE_ENGLISH = {
    "history": re.compile(r"\b(?:prior|previous)\b", re.IGNORECASE),
    "certainty": re.compile(
        r"\b(?:under (?:investigation|study)|cannot (?:be )?ruled? out|can't (?:be )?ruled? out|not ruled out|"
        r"doubtful|presumed|presumptive)\b", re.IGNORECASE),
}


# English words that are a negation of their own, as Spanish says "no" and the word: "no controlada" is
# "uncontrolled", "tos no productiva" a "nonproductive cough". Read so here only; validation.py reads English dictation
# as it did.
NEGATED_WORDS = frozenset(
    """uncontrolled uncomplicated untreated unvaccinated unimmunized nonproductive nontender nonradiating nonpitting
    nonpalpable nondistended nonreactive nonsmoker""".split()
)
# A relative told something isn't someone having it, as a relative reporting it isn't (validation.REPORTING): "Mother
# is explained about feeding", "the father was advised to", "explained to the mother". Spanish reads "se explica a la
# madre" so already (spanish.NOT_EXPERIENCER_AFTER).
TOLD = r"(?:told|tells?|explained|explains?|advised|advises?|counsell?ed|counsels?|instructed|instructs?|informed|informs?)"
TOLD_RELATIVES = (
    re.compile(rf"\b({validation.FAMILY})\s+(?:(?:is|was|were|are|has been|have been|had been)\s+)?{TOLD}\b",
               re.IGNORECASE),
    re.compile(rf"\b{TOLD}\s+(?:to\s+)?(?:(?:the|her|his|their)\s+)?({validation.FAMILY})\b", re.IGNORECASE),
)


def _without_told_relatives(text: str) -> str:
    for pattern in TOLD_RELATIVES:
        text = pattern.sub(lambda m: m.group()[: m.start(1) - m.start()] + " " * len(m.group(1))
                           + m.group()[m.end(1) - m.start():], text)
    return text


def _english_qualified(text: str) -> dict[str, bool]:
    names = {"negation_dropped": "negation", "experiencer_dropped": "experiencer", "certainty_dropped": "certainty",
             "history_dropped": "history"}
    found = {}
    for qualifier in validation.QUALIFIERS:
        read = _without_told_relatives(text) if qualifier.reason == "experiencer_dropped" else text
        found[names[qualifier.reason]] = bool(validation._reach(read, tokens(read), qualifier)[0])
    for kind, pattern in MORE_ENGLISH.items():
        found[kind] = found[kind] or bool(pattern.search(text))
    found["negation"] = found["negation"] or any(t.text in NEGATED_WORDS for t in tokens(text))
    return found


# ---- One statement --------------------------------------------------------------------------------------------------


@dataclass
class _Statement:
    english: str
    spans: list[tuple[int, int]] = field(default_factory=list)  # the kept pieces, outside instructions
    reasons: set[str] = field(default_factory=set)
    dropped: bool = False  # only an instruction, in English or in the Spanish it cites
    warnings: list[tuple[str, int, int]] = field(default_factory=list)


def said_numbers(said: list[spanish.Token], terms: list[_Term]) -> set[str]:
    """The numbers the Spanish says, as the number check reads them: every reading of each number, and 1 wherever it
    says "un", "una" or "uno", an article as often as a number ("un piso de escaleras", one flight of stairs)."""
    found = {value for term in terms if term.kind == "number" for value in term.values}
    return found | {"1"} if any(t.key in spanish.ARTICLE_NUMBERS for t in said) else found


def _locate(quote: list[spanish.Token], ctx: _Context, earlier: list[tuple[int, int]],
            cited: list[tuple[int, int]]) -> tuple[int, int] | str:
    """The quote's place in the transcript, word for word on keys, outside unclear passages, the first after the
    statement's previous source that no earlier source took (as validation._locate()), and one no earlier statement of
    the answer cites if there is one."""
    size, wanted = len(quote), [t.key for t in quote]
    keys = [t.key for t in ctx.toks]
    found = [(ctx.toks[i].start, ctx.toks[i + size - 1].end) for i in range(len(keys) - size + 1) if keys[i : i + size] == wanted]
    if not found:
        return "source_not_in_transcript"
    clear = [span for span in found if not any(validation._overlaps(span, passage) for passage in ctx.unclear)]
    if not clear:
        return "source_in_unclear_passage"
    unused = [span for span in clear if span not in earlier]
    unused = [span for span in unused if not any(validation._overlaps(span, other) for other in cited)] or unused
    after = earlier[-1][1] if earlier else 0
    return next((span for span in unused if span[0] >= after), unused[0] if unused else clear[0])


def _outside_instructions(span: tuple[int, int], ctx: _Context) -> list[tuple[int, int]]:
    pieces: list[tuple[int, int]] = []
    run: list[spanish.Token] = []
    for token in ctx.toks:
        if not (span[0] <= token.start and token.end <= span[1]):
            continue
        if any(validation._overlaps((token.start, token.end), directive) for directive in ctx.directives):
            if run:
                pieces.append((run[0].start, run[-1].end))
            run = []
        else:
            run.append(token)
    if run:
        pieces.append((run[0].start, run[-1].end))
    return pieces


def _statement(proposed: dict, ctx: _Context, earlier: list[tuple[int, int]], cited: list[tuple[int, int]]) -> _Statement:
    raw = str(proposed.get("english", "")).strip()
    english = validation.clean(raw)
    result = _Statement(english)
    if not english:
        result.dropped = True
        return result
    if (english != raw or validation.BAD_FORMAT.search(english)
            or any(c not in ctx.letters and c not in validation.NEUTRAL_CHARACTERS for c in english.casefold())
            or any(any(c.isalpha() and c not in ENGLISH_LETTERS for c in t.text) and t.text not in ctx.words
                   for t in spanish.tokens(english))):
        result.reasons.add("bad_format")
    # The grammar stops the English at MAX_ENGLISH: a statement that reaches it without a full stop was cut, and what
    # was cut may have changed what it says.
    if len(english) > MAX_ENGLISH or (len(english) == MAX_ENGLISH and english[-1] not in ".!?"):
        result.reasons.add("too_long")
    english, _ = validation._without_instructions(english)
    result.english = english
    if not english:
        result.dropped = True
        return result

    quotes = [validation.clean(q).strip() for q in proposed.get("spanish", []) if validation.clean(str(q)).strip()]
    if not quotes:
        result.reasons.add("no_sources")
    if len(quotes) > MAX_QUOTES or any(len(q) > MAX_QUOTE for q in quotes):
        result.reasons.add("too_long")
    located: list[tuple[int, int]] = []
    for quote in quotes:
        quote_words = spanish.tokens(quote)
        if validation.UNCLEAR_QUOTE.search(quote):
            result.reasons.add("source_in_unclear_passage")
            continue
        if len(quote_words) < 2 and not any(t.key[0].isdigit() for t in quote_words):
            result.reasons.add("source_too_short")
            continue
        found = _locate(quote_words, ctx, earlier + located, cited)
        if isinstance(found, str):
            result.reasons.add(found)
            continue
        located.append(found)
        result.spans += _outside_instructions(found, ctx)
    if located and not result.spans and not result.reasons & LOCATING:
        result.dropped = True
        return result
    if result.reasons & LOCATING or not result.spans:
        return result
    if any(validation._overlaps(span, other) for span in result.spans for other in cited):
        result.reasons.add("source_repeated")
    if sum(utf16_offset(ctx.text, end) - utf16_offset(ctx.text, start) for start, end in result.spans) > MAX_SPANISH:
        result.reasons.add("too_long")
    _compare(result, ctx)
    return result


def _compare(result: _Statement, ctx: _Context) -> None:
    """Step 4 of the module's header: the English against the Spanish words it rests on."""
    english, reasons = result.english, result.reasons
    inside = [i for i, t in enumerate(ctx.toks) if any(a <= t.start and t.end <= b for a, b in result.spans)]
    said_index = [i for i in inside if i not in ctx.cues and i not in ctx.taken_back]
    said = [ctx.toks[i] for i in said_index]
    said_text = " ".join(t.text for t in said)
    terms = [t for t in ctx.terms if t.first in said_index and t.last in said_index]
    back = [t for t in ctx.terms if t.first in inside and t.first in ctx.taken_back]

    def values(kind: str, group: list[_Term]) -> set[str]:
        return {value for term in group if term.kind == kind for value in term.values}

    english_nums = set(english_numbers(english))
    missing = english_nums - said_numbers(said, terms)
    if missing & values("number", back):
        reasons.add("source_superseded")
    elif missing:
        reasons.add("number_not_in_sources")
    for term in terms:
        if term.kind == "number" and term.ambiguous and term.values & english_nums:
            result.warnings.append((AMBIGUOUS_NUMBER, ctx.toks[term.first].start, ctx.toks[term.last].end))

    spanish_units = {(reading, unit) for number, unit in ctx.measured if number.first in said_index
                     for reading in number.readings}
    with_a_unit = {reading for reading, _ in spanish_units}
    for reading, unit in english_units(english):
        if (reading, unit) in spanish_units or reading in missing:  # a number not said is the number check's
            continue
        if unit in DOSE_UNITS or reading in with_a_unit:
            reasons.add("unit_not_in_sources")

    if not english_frequencies(english) <= spanish.frequencies(said_text):
        reasons.add("frequency_not_in_sources")
    if spanish.AMBIGUOUS_FREQUENCY.search(spanish.fold(said_text)):
        found = spanish.AMBIGUOUS_FREQUENCY.search(spanish.fold(ctx.text), min(s for s, _ in result.spans))
        if found:
            result.warnings.append((AMBIGUOUS_FREQUENCY, found.start(), found.end()))

    if not validation._date_terms(english) <= spanish.date_terms(said):
        reasons.add("date_not_in_sources")

    english_words = [t.text for t in tokens(english)]
    english_drugs = {w for w in english_words if w.isalpha() and is_drug(w)} | dosed(english)
    unbacked = english_drugs - {name for t in said if t.key.isalpha() for name in spanish.drug_candidates(t.key)}
    if unbacked & values("drug", back):
        reasons.add("source_superseded")
    elif unbacked:
        reasons.add("drug_not_in_sources")
    if any(term.kind == "drug" and not term.values & set(english_words) for term in terms):
        reasons.add("drug_dropped")

    if not {ENGLISH_SIDES[w] for w in english_words if w in ENGLISH_SIDES} <= values("side", terms):
        reasons.add("side_not_in_sources")
    if not english_routes(english) <= spanish.routes(said_text):
        reasons.add("route_not_in_sources")
    allowed = {word for t in said for word in spanish.SEVERITY.get(t.key, ())}
    if not {w for w in english_words if w in ENGLISH_SEVERITY} <= allowed:
        reasons.add("severity_not_in_sources")
    if not english_abbreviations(english) <= spanish.abbreviations(said_text):
        reasons.add("abbreviation_not_in_sources")
    if english_actions(english) != spanish_actions(said):
        reasons.add("action_changed")

    in_english = _english_qualified(english)
    for kind in ("negation", "certainty", "history", "experiencer"):
        in_spanish = bool(ctx.qualified[kind] & set(inside))
        if in_spanish and not in_english[kind]:
            reasons.add(f"{kind}_dropped")
        elif in_english[kind] and not in_spanish:
            reasons.add(f"{kind}_added")
    english_relatives = {ENGLISH_RELATIVES[w] for w in english_words if w in ENGLISH_RELATIVES}
    spanish_relatives = spanish.relatives(said)
    if not english_relatives <= {who for whos in spanish_relatives for who in whos} or any(
        not whos & english_relatives for whos in spanish_relatives
    ):
        reasons.add("experiencer_changed")

    if english_content(english) > spanish_content(said) + EXTRA_WORDS:
        reasons.add("translation_adds_words")
    if not reasons:
        result.warnings += _possible_corrections(english_nums, english_drugs, terms, ctx)


def _possible_corrections(english_nums: set[str], english_drugs: set[str], terms: list[_Term],
                          ctx: _Context) -> list[tuple[str, int, int]]:
    """Where a correction the checks didn't hear may be: for each number or drug the value uses from its sources, the
    first of its kind within WARNING_REACH words after it, read otherwise, with a correction-like word between them
    (spanish.GAP_WORDS) that isn't a cue the checks heard."""
    found = []
    for term in terms:
        used = term.values & (english_nums if term.kind == "number" else english_drugs if term.kind == "drug" else set())
        if not used:
            continue
        for later in ctx.terms:
            if later.kind != term.kind or not term.last < later.first <= term.last + WARNING_REACH or later.values & term.values:
                continue
            gap = [i for i in range(term.last + 1, later.first) if i not in ctx.cues]
            if all(ctx.toks[i].key in GLUE for i in range(term.last + 1, later.first)):
                continue
            if any(ctx.toks[i].key in spanish.GAP_WORDS for i in gap):
                found.append((validation.POSSIBLE_CORRECTION, ctx.toks[later.first].start, ctx.toks[later.last].end))
            break
    return found


def _near_unclear(spans: list[tuple[int, int]], ctx: _Context) -> list[tuple[str, int, int]]:
    found = []
    for start, end in ctx.unclear:
        unclear = [i for i, t in enumerate(ctx.toks) if start <= t.start < end]
        for span in spans:
            source = [i for i, t in enumerate(ctx.toks) if span[0] <= t.start and t.end <= span[1]]
            if not unclear or not source:
                continue
            between = unclear[0] - source[-1] - 1 if unclear[0] > source[-1] else source[0] - unclear[-1] - 1
            if between <= NEAR_UNCLEAR:
                found.append((validation.NEAR_UNCLEAR_PASSAGE, start, end))
                break
    return found


# ---- A field --------------------------------------------------------------------------------------------------------


def _field(name: str, proposed: list[dict], results: list[_Statement], ctx: _Context) -> Translated:
    if not proposed:
        return Translated(name, "", ["not_dictated"])
    kept = [r for r in results if not r.dropped]
    reasons = {reason for r in results for reason in r.reasons if not r.dropped or reason == "bad_format"}
    if len(proposed) > MAX_STATEMENTS:
        reasons.add("too_long")
    if not kept and not reasons:
        return Translated(name, "", ["order_or_allergy_change"])
    value, statements = "", []
    for result in kept:
        sentence = result.english if result.english[-1:] in ".!?" else result.english + "."
        start = len(value) + (1 if value else 0)
        value = f"{value} {sentence}" if value else sentence
        statements.append((start, len(value), result.spans))
    if len(value) > LIMITS[name]:
        reasons.add("too_long")
    if reasons:
        return Translated(name, value, [reason for reason in REASONS if reason in reasons])
    spans = [span for result in kept for span in result.spans]
    warnings = [w for result in kept for w in result.warnings] + _near_unclear(spans, ctx)
    return Translated(name, value, [], spans, statements, sorted(set(warnings), key=lambda w: (w[1], w[0])))


def check(fields: dict[str, list[dict]], unknown: list[str], transcript: Transcript) -> Translations:
    """Each field's statements checked against the Spanish transcript (see the module's header), in the answer's order:
    each statement's "order" (llama.parse_statements()), or else field by field."""
    ctx = _context(transcript)
    proposed = {name: list(fields.get(name, [])) for name in FIELDS}
    listed = [(name, k, statement) for name in FIELDS for k, statement in enumerate(proposed[name])]
    results: dict[str, dict[int, _Statement]] = {name: {} for name in FIELDS}
    earlier: dict[str, list[tuple[int, int]]] = {name: [] for name in FIELDS}
    cited: list[tuple[int, int]] = []  # what every statement before this one in the answer quotes, outside instructions
    for _, (name, k, statement) in sorted(enumerate(listed), key=lambda item: (item[1][2].get("order", item[0]), item[0])):
        result = _statement(statement, ctx, earlier[name], cited)
        earlier[name] += result.spans
        cited += result.spans
        results[name][k] = result
    checked = {name: _field(name, proposed[name], [results[name][k] for k in range(len(proposed[name]))], ctx)
               for name in FIELDS}
    offered = [f for f in checked.values() if f.pending]
    if len(offered) == 1 and ctx.toks:
        only = offered[0]
        covered = sum(any(s <= t.start and t.end <= e for s, e in only.spans) for t in ctx.toks)
        if covered >= validation.WHOLE_DICTATION * len(ctx.toks):
            only.warnings.append((validation.ONE_FIELD_HOLDS_DICTATION, min(s for s, _ in only.spans),
                                  max(e for _, e in only.spans)))
    return Translations(checked, list(unknown))
