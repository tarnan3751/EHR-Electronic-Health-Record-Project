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
   backstop), and the Spanish the statement cites is listed as not carried over (not_carried_over()), so the clinician
   sees what was cut; a statement that was only an instruction is dropped.
2. Each Spanish passage must be in the transcript, word for word, ignoring case, punctuation and accents
   (source_not_in_transcript), not in a passage marked unclear, and two words or more unless it holds a number. Every
   check below reads the transcript's own words where the passage was found, never the model's copy. No word may be
   cited by two statements of the answer: one citing a word an earlier statement (in the answer's order) cites outside
   an instruction fails (source_repeated), so no passage is shown twice, in one field or two. What a statement cites
   is at most MAX_SPANISH characters of the transcript (too_long), the most the schema's two quotes of 200 allow and
   the most the on-prem app takes for a sentence (TrustedResult).
3. Only the words outside an instruction to order something or change the allergy list count: one spanish.py finds
   (FR-14), or one said in English that validation.py finds ("order a CBC"). A statement whose passages are all inside
   one is that instruction translated: it is dropped, and the field is empty (order_or_allergy_change) when nothing is
   left of it. The Spanish is read as corrected: without a correction cue and what it took back.
4. The English is compared with that Spanish, class by class. "No more" means the English may leave something out, as
   an English value may, but may not add or change it; "the same" means both ways.
   - numbers, as digits (Spanish decimal commas, number words, 1 for "un", "una" or "uno" even as an article, and
     halves and quarters however said: "un gramo y medio" 1.5, "un cuarto de tableta" 0.25, "1/2 tableta" 0.5, "half
     a tablet" 0.5): no more (number_not_in_sources), and none the clinician took back (source_superseded); a fraction
     with no exact decimal ("un tercio", "a third") can't be compared (number_unreadable); and a number written with a
     point or a comma before exactly three digits ("5.000", "2,500") is refused whatever the English writes
     (ambiguous_number), since Spain writes 2.500 for two thousand five hundred and Mexico for two and a half, and the
     checks can't know which was meant;
   - a dose unit with its number ("50 mcg" is not "50 mg"): no more (unit_not_in_sources). A unit of measurement
     (mmHg, beats a minute, degrees, percent) may be added to a number said without one, never changed; a dose's, a
     result's (mg/dL, mmol/L, mEq) or one a Spanish dictation doesn't use (lb, oz, °F, inches) must be said
     (SAID_UNITS);
   - how long, a number with its hours, days, weeks, months or years ("por cinco días" is not "for 5 months", "hace
     una semana" is "a week ago"): no more (period_not_in_sources);
   - how often (times a day, a week, as needed, at bedtime): no more (frequency_not_in_sources), and English that
     looks like how often but isn't read ("twice", "every 8 minutes", "after breakfast": LOOKS_LIKE_FREQUENCY) is
     refused, since it can't be compared;
   - dates ("hace una semana" is a week ago, or the past week): no more (date_not_in_sources);
   - drug names, failing closed both ways: every English word a list knows as a drug (a USAN stem, drugnames.py's
     RxNorm ingredients, the words of their names and brand names) or that comes just before a dose ("zorbitex 20
     mg", whether a list knows it or not) must be a word of the Spanish or an English name one gives (spanish.py's
     endings and exceptions) (drug_not_in_sources); and every drug the Spanish names must be named in the English
     (drug_dropped), so a drug no list knows can't be swapped in for one the clinician said;
   - sides (a capital R or L and "rt", "lt" too), routes (intrathecal, epidural, "IV push" and the others too),
     severity (every degree spanish.SEVERITY allows, from "slight" and "scant" to "excruciating" and "worst", is read in
     the English: one the Spanish's severity words don't allow is refused, so "Dolor leve" is not "Excruciating pain"),
     dose forms (tablets, syrup, a patch: spanish.DOSE_FORMS) and abbreviations, however they are written
     ("hiv", "nsaids": CLINICAL_ABBREVIATIONS): no more (side_, route_, severity_, dose_form_ and
     abbreviation_not_in_sources);
   - what can be ordered (an X-ray, labs, a culture, a referral, a prescription, a refill: ORDERABLES): no more
     (orderable_not_in_sources), so an instruction cut from the note can't come back in a neighbouring statement
     ("Continuar losartán 50 mg al día" as "Continue losartan 50 mg daily; chest X-ray.");
   - what is done with each drug, drug class or therapy (DRUG_CLASSES: "antihipertensivos", "anticoagulación"), dose,
     dose's number and habit (smoking, alcohol): the verb nearest it within ACTION_REACH words either side in its
     clause, a change (start, stop, hold, continue, increase, decrease, switch; "doblar" and "double" an increase, "a la
     mitad" and "halve" a decrease, "evitar", "avoid" and "prohibido" a stop, "espaciar" and "less often" fewer doses)
     or a taking ("toma", "takes", "fuma"), said before it or after it ("Metformina: suspender", "Metformina se
     suspende", "Metformin was stopped"), with its tense: a Spanish preterite, imperfect or participle is past
     ("suspendió", "tomaba", "suspendido"), an English one past after "was", "had", "previously", "used to" or with
     "ago" after it, and "either" otherwise ("stopped"). Each must be the same on both sides (action_changed): a change
     the same change, in a tense that may be the same; a taking a taking or nothing, but one in the past ("tomaba",
     "exfumador") a taking in the past. So "Dejó de fumar hace 10 años" is not "Smoking for 10 years", "Toma
     metformina" not "Metformin was stopped", and "Suspendió la metformina hace un mes" not "Stop metformin". A class
     the Spanish doesn't name is refused as a drug is (drug_not_in_sources). And where what a change is said of can't
     be read ("suspender la sonda"), the English may say no change of anything that goes the other way from one the
     Spanish says (OPPOSITES: "Keep the catheter"), failing closed; any other change it may, since everyday verbs say
     changes too ("subir las escaleras", "stop to rest"). The verbs
     are generated from their infinitives (spanish.conjugate()), with the pronouns a command takes ("suspéndala");
     "baja" and "continua", unaccented, are adjectives ("dosis baja");
   - a negation, uncertainty, the past, or someone other than the patient having it, read in the transcript as
     validation.py reads them in English, with a word that negates itself a negation in either language ("afebril",
     "indoloro", "inconsciente": spanish.SELF_NEGATING; "afebrile", "uncontrolled", "absent": NEGATED_WORDS) and a
     relative told something ("the mother is explained") not someone having it, a relative clause read from its verb
     on ("acude con su madre, que tiene diabetes" is the mother's diabetes) and a relative named with whose relative
     they are read past that ("la madre del niño refiere que tose" is the child's cough): the same (negation_dropped or
     negation_added, and so on), and the relatives named the same (experiencer_changed). A qualifier carries over a
     list past its commas, in both languages, as validation.py carries an English negation (spanish.carry_over_list()):
     up to the word that ends it (a contrast, a word starting a clause, a step of the plan, the end of its sentence),
     not into a clause of its own (a new subject, a verb of having or telling: "sin fiebre, la tos está mejor"), and
     not past a new paragraph, which ends every cue's reach: "Niega fiebre, tos, disnea y síncope" negates the four,
     "Niega fiebre, escalofríos o sudoración nocturna pero refiere pérdida de peso" the first three. And it is read in
     the whole transcript, so a passage quoting the tail of a negated list ("tos, disnea y síncope") takes the negation
     with it. A doubt is the clinician's whether it comes before what it doubts ("creo que", "quizá", "tal vez", "a lo
     mejor", "puede que", "podría tener") or closes its clause, after a comma or not, reaching back over its sentence
     ("creo", "probablemente"), as validation.py hears one in English ("I think she has", "maybe", "she might have",
     "probably", a closing "I think"); a question is doubt throughout; and a negation or a doubt passes over "creo",
     "que", a subject and its verb ("no creo que la paciente tenga"), as validation.py's TO_THE_FINDING does. "No creo
     que tenga", "dudo que", "no estoy seguro", "no se puede descartar" and "no se descarta" are a negation and a
     doubt, as validation.py reads "I don't think she has", "I doubt", "not sure" and "cannot rule out"; "le preocupa",
     "me pregunto si", "posibilidad de", "presuntamente", "compatible con" and "sugestivo de" are doubt, as "worried",
     "wonder", "a chance", "presumably", "consistent with" and "suggestive of" are; "parece", "aparentemente",
     "orientado como", "se plantea", "diagnóstico de presunción" (past its colon), "pudiera tratarse de" and "a
     valorar" are doubt, as "seems", "appears to be", "apparently", "looks like", "working diagnosis", "presumptive",
     "being considered" and "to be evaluated" are; "nada de" is a negation; "se descartó" and "descartado" are
     a negation, as "ruled out" is; "hace años", "el año pasado", "en el pasado", "previamente", "previo" and "antiguo"
     are the past, as "years ago", "last year", "in the past", "previously", "prior", "previous", "old" and "remote"
     are (MORE_ENGLISH, read as validation.py's own cues are); and "en la familia" is someone else, as "in the family"
     is. Having had something ("tuvo", "tenía", "padeció") written as having it now ("is having", "currently", "now",
     "active") is history_dropped, unless the Spanish says now too ("actualmente");
   - in order, and paired: numbers, how long, drugs, sides, routes, severity, how often, relatives and medication
     changes each come in the English in the Spanish's order, some left out but none moved (order_changed): "TA 142
     sobre 88" is not "BP 88/142", nor "Suspender metformina e iniciar insulina" "Start metformin and stop insulin"; a
     number measures what the Spanish says it measures, the vital sign, result (LABELS) or drug named before it in its
     clause (label_changed): "Glucosa de 140" is not "Blood pressure 140", nor "Reading of 140"; and a qualifier stays
     on the clause it qualifies in the Spanish, with the findings both languages spell alike ("neumonía" and
     "pneumonia", _skeleton()) (order_changed): "Posible neumonía, asma conocida" is not "Pneumonia, possible asthma",
     nor "Madre con diabetes y padre con hipertensión" "Mother with hypertension and father with diabetes". English
     that says the Spanish in another order is refused as well ("Amlodipino y losartán" as "Losartan and amlodipine"):
     the checks can't tell a reordering from a swap. A frequency read two ways may be written both ways in its place
     ("cada tercer día" as "every other day or every third day");
   - opposites (POLARITY: high, low or normal; better or worse; controlled or not; allergic or tolerating; positive or
     negative; hyper- or hypo-, tachy- or brady-): no more (polarity_changed), so "hipotensión" is not "hypertension",
     nor "mal controlada" "controlled"; and pregnancy, suicide, cancer and intubation (FINDINGS): no more
     (finding_not_in_sources), so the words the next check allows can't add "suicidal ideation" or "cancer
     diagnosed";
   - the English no more than EXTRA_WORDS content words longer than the Spanish (translation_adds_words): a clause the
     model added ("continue metformin and check kidney function" from "continuar metformina"). Any other content word
     is unchecked: what it means is the clinician's to compare.
5. If any statement fails, the whole field is discarded, as an English value is.

What these don't check: the meaning of every other word. "dolor abdominal leve" translated "mild chest pain" passes
(the severity and the length agree), and so does a finding moved inside a statement, from one severity, side or
qualifier to another, when the two languages spell it differently ("Dolor leve en la rodilla e intenso en la cadera"
as "Mild hip pain and severe knee pain", "Posible gripe, tos conocida" as "Flu, possible cough"); nor a drug name no
list knows,
written with no dose just after it, for one no list knows either ("continuar plixor" written "continue vortanex"); nor
a word left in the English that isn't English, if the transcript says it or it is written in English's letters: a
Spanish word left untranslated ("pain in the rodilla", "pain in the región lumbar"), or a word of no language. A quote is matched whole word by whole word, so one the schema's length cut
short inside a word ("niega dis") is never taken for the word it begins: it isn't found. The
review shows each English statement beside its Spanish for the clinician to compare, and says so.

Warnings, each a code and a place in the transcript, never text, as validation.py's: possible_correction (a later
number, date or drug of the same kind just after one the value uses, with a Spanish correction-like word between),
near_unclear_passage, one_field_holds_dictation, and one of their own: ambiguous_frequency, on a value from "cada
tercer día" (every other day in Mexico, every third day literally), pointing at it.
"""

from __future__ import annotations

import re
import string
from dataclasses import dataclass, field

from . import drugnames, prompt, spanish, validation
from .prompt import FIELDS
from . import textnorm
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
# Content words the English of a statement may have beyond its Spanish: the most the scripts' gold needs (the
# coordinator's decision). The hold-outs, spent, don't set it: h01's "Lasègue positivo en la pierna derecha a los 40
# grados" written "Positive Lasègue sign in the right leg at 40 degrees" is two more, and refused. What a word more may
# say that matters most is compared on its own: opposites (POLARITY), findings (FINDINGS), what can be ordered
# (ORDERABLES).
EXTRA_WORDS = 1
CORRECTION_WINDOW = 3  # words after a correction cue within which the corrected value starts
WARNING_REACH = validation.WARNING_REACH
NEAR_UNCLEAR = validation.NEAR_UNCLEAR

AMBIGUOUS_NUMBER = "ambiguous_number"  # a reason: a number that reads two ways is refused
AMBIGUOUS_FREQUENCY = "ambiguous_frequency"
WARNINGS = (validation.POSSIBLE_CORRECTION, validation.NEAR_UNCLEAR_PASSAGE, validation.ONE_FIELD_HOLDS_DICTATION,
            AMBIGUOUS_FREQUENCY)

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
    "number_unreadable",
    "ambiguous_number",
    "unit_not_in_sources",
    "period_not_in_sources",
    "frequency_not_in_sources",
    "date_not_in_sources",
    "drug_not_in_sources",
    "drug_dropped",
    "side_not_in_sources",
    "route_not_in_sources",
    "dose_form_not_in_sources",
    "orderable_not_in_sources",
    "severity_not_in_sources",
    "abbreviation_not_in_sources",
    "action_changed",
    "order_changed",
    "label_changed",
    "negation_dropped",
    "negation_added",
    "certainty_dropped",
    "certainty_added",
    "history_dropped",
    "history_added",
    "experiencer_dropped",
    "experiencer_added",
    "experiencer_changed",
    "polarity_changed",
    "finding_not_in_sources",
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
    # The Spanish each statement cites whose English was an instruction the English backstop cut (step 1), and its kind.
    listed: list[tuple[int, int, str]] = field(default_factory=list)

    def to_json(self, transcript: Transcript) -> dict:
        return {name: checked.to_json(transcript) for name, checked in self.fields.items()}


def not_carried_over(transcript: Transcript, checks: Translations | None = None) -> list[dict]:
    """Each instruction in the Spanish transcript to order something or change the allergy list, which the note never
    carries: its kind and where the clinician said it, as UTF-16 offsets, the shape validation.not_carried_over() gives.
    The instructions are those spanish.py finds, those validation.py finds said in English ("order a CBC" in a Spanish
    dictation), and, once the checks have run, the Spanish each statement cites whose English the backstop cut, so
    nothing cut from the note goes unlisted."""
    return [
        {"reason": kind, "start": utf16_offset(transcript.text, start), "end": utf16_offset(transcript.text, end)}
        for start, end, kind in _merged(directives(transcript.text) + (checks.listed if checks else []))
    ]


def directives(text: str) -> list[tuple[int, int, str]]:
    """The instructions in the transcript: spanish.directives() and validation.directives() merged."""
    return _merged(spanish.directives(text) + validation.directives(text))


def _merged(spans: list[tuple[int, int, str]]) -> list[tuple[int, int, str]]:
    """Overlapping spans as one, an allergy change winning, as spanish.directives() merges its own."""
    merged: list[tuple[int, int, str]] = []
    for start, end, kind in sorted(spans):
        if merged and start < merged[-1][1]:
            first, last, other = merged[-1]
            merged[-1] = (first, max(last, end), "allergy_change" if "allergy_change" in (kind, other) else kind)
        else:
            merged.append((start, end, kind))
    return merged


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
    qualifier_cues: dict[str, set[int]]


GLUE = frozenset({"sobre", "de", "el", "la", "los", "las", "un", "una"}) | spanish.UNIT_WORDS


def _terms(toks: list[spanish.Token], text: str | None = None) -> list[_Term]:
    found = [_Term("number", n.readings, n.first, n.last, n.ambiguous) for n in spanish.numbers(toks, text)]
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
    terms = _terms(toks, text)
    cues, taken_back = _corrections(text, toks, terms)
    return _Context(
        text=text,
        toks=toks,
        unclear=transcript.unclear_ranges(),
        directives=[(start, end) for start, end, _ in directives(text)],
        letters=frozenset(validation.clean(text).casefold()),
        words=frozenset(t.text for t in spanish.tokens(validation.clean(text))),
        terms=terms,
        measured=spanish.measured(text, toks),
        cues=cues,
        taken_back=taken_back,
        qualified=spanish.qualified(text, toks),
        qualifier_cues=spanish.qualifier_cue_kinds(text, toks),
    )


# ---- The English side ------------------------------------------------------------------------------------------------

EXCEPTION_NAMES = frozenset(name for names in spanish.DRUG_EXCEPTIONS.values() for name in names)


def is_drug(word: str) -> bool:
    """An English drug name: a USAN stem (validation.py), an RxNorm ingredient, a word of one or a brand name
    (drugnames.py), or an exception's."""
    return validation._is_drug(word) or word in drugnames.NAMES or word in EXCEPTION_NAMES


ALPHANUMERIC = re.compile(r"\b(?=\w*[A-Za-z])(?=\w*\d)\w+\b")


def english_numbers(text: str) -> list[str]:
    """The numbers in English text, as digits, a comma before three digits read as a thousands separator, and a half or
    a quarter as its decimal (english_fractions()). The digit in an abbreviation ("A1c", "SpO2", "T2DM") is part of its
    name, which the abbreviation check compares."""
    return numbers(_as_numbers(text)[0])


NUMBER_WORD = re.compile(r"[A-Za-z]+|\d+(?:\.\d+)?(?:st|nd|rd|th)?")  # textnorm.numbers()'s words


def english_number_items(text: str) -> list[tuple[str, int]]:
    """Each number of english_numbers(), in order, with where in the text it starts."""
    rewritten, origin = _as_numbers(text)
    starts: list[int] = []
    for word in NUMBER_WORD.finditer(rewritten):
        count = len(numbers(rewritten[: word.end()]))
        starts += [origin[word.start()]] * (count - len(starts))
    return list(zip(numbers(rewritten), starts))


def _as_numbers(text: str) -> tuple[str, list[int]]:
    """The text as english_numbers() reads it, and for each of its characters where in the text it came from."""
    origin = list(range(len(text)))
    text, origin = _rewrite(re.compile(r"(?<=\d),(?=\d{3}\b)"), lambda m: "", text, origin)
    for pattern, decimal in _EN_FRACTIONS:
        text, origin = _rewrite(pattern, lambda m, d=decimal: f" {normalize_decimal(d(m))} ", text, origin)
    return _rewrite(ALPHANUMERIC, lambda m: " " * len(m.group()), text, origin)


def _rewrite(pattern: re.Pattern, replace, text: str, origin: list[int]) -> tuple[str, list[int]]:
    out: list[str] = []
    where: list[int] = []
    last = 0
    for match in pattern.finditer(text):
        new = replace(match)
        out += [text[last : match.start()], new]
        source = origin[match.start()] if match.start() < len(origin) else len(text)
        where += origin[last : match.start()] + [source] * len(new)
        last = match.end()
    out.append(text[last:])
    where += origin[last:]
    return "".join(out), where


_EN_WHOLE = r"(\d+|one|two|three|four|five|six|seven|eight|nine|ten)"
_EN_WHOLES = {"one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7, "eight": 8, "nine": 9, "ten": 10}
# Each fraction English writes, and its decimal: "one and a half" is 1.5, "1 1/2" 1.5, "half a tablet" 0.5, "a
# quarter" 0.25, "three quarters" 0.75, "1/2 tablet" 0.5. A third has no exact decimal: UNREADABLE stands in for it.
UNREADABLE = "\u2047"
_EN_FRACTIONS = [
    (re.compile(rf"\b{_EN_WHOLE}\s+and\s+a\s+half\b", re.I), lambda m: str(_whole(m.group(1)) + 0.5)),
    (re.compile(r"\b(\d+)\s+([13])/4\b"), lambda m: str(int(m.group(1)) + int(m.group(2)) / 4)),
    (re.compile(r"\b(\d+)\s+1/2\b"), lambda m: str(int(m.group(1)) + 0.5)),
    (re.compile(r"\bthree[\s-]+quarters?\b|\b3/4\b", re.I), lambda m: "0.75"),
    (re.compile(r"\b(?:an?|one)[\s-]+(?:quarter|fourth)\b|\bquarter\s+of\b|\b1/4\b", re.I), lambda m: "0.25"),
    (re.compile(r"\b(?:an?|one)[\s-]+half\b|\bhalf\s+(?:an?|of|the)\b|\b1/2\b", re.I), lambda m: "0.5"),
    (re.compile(r"\b(?:an?|one|two)[\s-]+thirds?\b|\b[12]/3\b", re.I), lambda m: UNREADABLE),
]


def _whole(word: str) -> int:
    return int(word) if word.isdigit() else _EN_WHOLES[word.lower()]


def english_fractions(text: str) -> str:
    for pattern, decimal in _EN_FRACTIONS:
        text = pattern.sub(lambda m, d=decimal: f" {normalize_decimal(d(m))} ", text)
    return text


def normalize_decimal(value: str) -> str:
    return value if value == UNREADABLE else textnorm.normalize_number(value)


DOSE_UNITS = frozenset({"mg", "mcg", "g", "kg", "ml", "l", "unit", "mg/dl"})  # never added to a number said without
# Units a Spanish dictation doesn't use, or that are a dose or a result's: an English one must be said, never added to a
# number said without it ("70 kilos" is not "70 lb", "250 mg/dl" not "250 mmol/L", "38 grados" not "38 °F").
SAID_UNITS = DOSE_UNITS | frozenset({"lb", "oz", "fahrenheit", "celsius", "mmol/l", "meq", "inch", "ft", "cm", "mm"})
ENGLISH_UNITS = re.compile(
    r"(\d+(?:\.\d+)?)\s*(mg/dl|mmol/l|mmol|meq/l|meq|micrograms?|mcg|μg|µg|milligrams?|mg|grams?|g|kilograms?|kilos?|"
    r"kg|milliliters?|millilitres?|ml|cc|liters?|litres?|l|units?|iu|percent|%|°\s*f|°\s*c|degrees? fahrenheit|"
    r"degrees? celsius|fahrenheit|celsius|degrees?|°|mmhg|bpm|beats (?:a|per) minute|breaths (?:a|per) minute|"
    r"pounds?|lbs?|ounces?|oz|inch(?:es)?|feet|foot|ft|centimeters?|centimetres?|cm|millimeters?|millimetres?|mm)"
    r"(?![a-z])",
    re.IGNORECASE,
)
UNIT_NAMES = {
    "microgram": "mcg", "mcg": "mcg", "μg": "mcg", "µg": "mcg", "milligram": "mg", "mg": "mg", "gram": "g", "g": "g",
    "kilogram": "kg", "kilo": "kg", "kg": "kg", "milliliter": "ml", "millilitre": "ml", "ml": "ml", "cc": "ml",
    "liter": "l", "litre": "l", "l": "l", "unit": "unit", "iu": "unit", "percent": "percent", "%": "percent",
    "degree": "degree", "°": "degree", "mmhg": "mmhg", "bpm": "bpm", "mg/dl": "mg/dl", "mmol/l": "mmol/l",
    "mmol": "mmol/l", "meq/l": "meq", "meq": "meq", "pound": "lb", "lb": "lb", "ounce": "oz", "oz": "oz",
    "°f": "fahrenheit", "degree fahrenheit": "fahrenheit", "degrees fahrenheit": "fahrenheit",
    "fahrenheit": "fahrenheit", "°c": "degree", "degree celsius": "degree", "degrees celsius": "degree",
    "celsius": "degree", "inche": "inch", "inch": "inch", "feet": "ft",
    "foot": "ft", "ft": "ft", "centimeter": "cm", "centimetre": "cm", "cm": "cm", "millimeter": "mm",
    "millimetre": "mm", "mm": "mm",
}


def english_units(text: str) -> set[tuple[str, str]]:
    found = set()
    for match in ENGLISH_UNITS.finditer(re.sub(r"(?<=\d),(?=\d{3}\b)", "", text)):
        unit = re.sub(r"\s+", " ", match.group(2).lower()).replace("° ", "°")
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


# Words that say how often, which a reading of ENGLISH_FREQUENCIES must take in: one it doesn't ("twice", "every 8
# minutes", "after breakfast", "hourly") can't be compared, and is refused.
LOOKS_LIKE_FREQUENCY = re.compile(
    r"\b(?:twice|thrice|once|hourly|every|daily|weekly|monthly|nightly|q\d+\s*h|q\d+\s*hrs?|qd|bid|tid|qid|qhs|qod|qam|"
    r"qpm|prn|b\.i\.d\.|t\.i\.d\.)\b|\b(?:before|after|with)\s+(?:breakfast|lunch|dinner|supper|meals?|food|eating)\b"
    r"|\b(?:\d+|one|two|three|four|five|six)\s+times\b",
    re.IGNORECASE,
)


def unread_frequencies(text: str) -> bool:
    """Whether the English says how often in a way ENGLISH_FREQUENCIES doesn't read (LOOKS_LIKE_FREQUENCY)."""
    lowered = text.lower()
    read = [match.span() for pattern, _ in ENGLISH_FREQUENCIES for match in pattern.finditer(lowered)]
    return any(not any(start <= m.start() and m.end() <= end for start, end in read)
               for m in LOOKS_LIKE_FREQUENCY.finditer(lowered))


def english_frequencies(text: str) -> set[str]:
    return {value for _, read in english_frequency_items(text) for value in read}


def english_frequency_items(text: str) -> list[tuple[int, frozenset[str]]]:
    lowered = text.lower()
    taken: list[tuple[int, int]] = []
    found: list[tuple[int, frozenset[str]]] = []
    for pattern, read in ENGLISH_FREQUENCIES:
        for match in pattern.finditer(lowered):
            if any(match.start() < end and start < match.end() for start, end in taken):
                continue
            taken.append(match.span())
            found.append((match.start(), frozenset(read(match))))
    return sorted(found, key=lambda item: item[0])


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
    ("intrathecal", re.compile(r"\bintrathecal(?:ly)?\b")),
    ("epidural", re.compile(r"\bepidural(?:ly)?\b")),
    ("intradermal", re.compile(r"\bintradermal(?:ly)?\b")),
    ("intraosseous", re.compile(r"\b(?:intraosseous(?:ly)?|io)\b")),
    ("intra-articular", re.compile(r"\bintra-?articular(?:ly)?\b")),
    ("transdermal", re.compile(r"\b(?:transdermal(?:ly)?|patch(?:es)?)\b")),
    ("buccal", re.compile(r"\bbuccal(?:ly)?\b")),
    ("enteral", re.compile(r"\b(?:enteral(?:ly)?|nasogastric|ng tube|peg tube|per tube|via tube|g-tube)\b")),
    ("iv", re.compile(r"\b(?:iv push|iv piggyback|ivpb|infusion)\b")),
]


def english_routes(text: str) -> set[str]:
    return {name for _, name in english_route_items(text)}


def english_route_items(text: str) -> list[tuple[int, str]]:
    lowered = text.lower()
    return sorted((match.start(), name) for name, pattern in ENGLISH_ROUTES for match in pattern.finditer(lowered))


ENGLISH_SIDES = {"left": "left", "right": "right", "bilateral": "bilateral", "bilaterally": "bilateral",
                 "both": "bilateral", "rt": "right", "lt": "left", "bilat": "bilateral"}
# A side written as its capital letter: "R knee pain", "L ankle" (not a number's liters: "2 L").
SIDE_LETTER = re.compile(r"(?<![\d.]\s)(?<![\d./])\b([RL])\b(?!\s*/)")


def english_sides(text: str) -> list[str]:
    """Each side the English names, in order: the words, and a capital R or L."""
    found = [(w.start, ENGLISH_SIDES[w.text]) for w in tokens(text) if w.text in ENGLISH_SIDES]
    found += [(m.start(), "right" if m.group(1) == "R" else "left") for m in SIDE_LETTER.finditer(text)]
    return [side for _, side in sorted(found)]
# Every English degree a Spanish severity word allows (spanish.SEVERITY): one the Spanish doesn't say is refused.
ENGLISH_SEVERITY = frozenset(word for words in spanish.SEVERITY.values() for word in words) | {"excruciating", "worst"}
# Dose forms, by spanish.DOSE_FORMS' names: an English one must be one the Spanish says ("inhalado" is not "tablets").
ENGLISH_DOSE_FORMS = {
    "tablet": "tablet", "tablets": "tablet", "tab": "tablet", "tabs": "tablet", "pill": "tablet", "pills": "tablet",
    "capsule": "capsule", "capsules": "capsule", "cap": "capsule", "caps": "capsule", "syrup": "syrup",
    "patch": "patch", "patches": "patch", "suppository": "suppository", "suppositories": "suppository",
    "drops": "drops", "drop": "drops", "spray": "spray", "sprays": "spray", "injection": "injection",
    "injections": "injection", "injectable": "injection", "cream": "cream", "ointment": "ointment", "gel": "gel",
    "lozenge": "lozenge", "lozenges": "lozenge", "inhaler": "inhaler", "inhalers": "inhaler", "puff": "inhaler",
    "puffs": "inhaler", "solution": "solution", "suspension": "suspension", "powder": "powder", "sachet": "powder",
    "sachets": "powder",
}


def english_dose_forms(text: str) -> set[str]:
    return {ENGLISH_DOSE_FORMS[w.text] for w in tokens(text) if w.text in ENGLISH_DOSE_FORMS}
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


# Clinical abbreviations, read however they are written ("hiv positive", "avoid nsaids"): every one the Spanish lists
# give (spanish.ABBREVIATIONS), and these. Not "hr" in lower case, which is an hour as often.
CLINICAL_ABBREVIATIONS = frozenset(
    a for names in spanish.ABBREVIATIONS.values() for a in names
) | frozenset(
    """HIV NSAID NSAIDS COPD CHF CKD HTN AMI DVT UTI CBC MRI ECG EKG SPO2 BMI LDL HDL TSH INR AFIB GERD OSA BPH CAD PCI
    CABG TIA CVA ARDS AKI CRP ESR PSA A1C HBA1C GFR EGFR LVEF HFREF HFPEF T2DM DM2 IBS IBD PTSD ADHD URI ICU BP DM MI PE
    AF EF HF RR CT""".split()
)


def english_abbreviations(text: str) -> set[str]:
    found = set()
    for match in ABBREVIATION.finditer(text):
        word = match.group()
        capitals = sum(1 for c in word if c.isupper())
        written = capitals >= 2 or (capitals and any(c.isdigit() for c in word))
        if (written or word.upper() in CLINICAL_ABBREVIATIONS) and word.upper() not in NOT_ABBREVIATIONS:
            found.add(word.upper())
    return found


# Medication changes in English, each form with its kind (start, stop, hold, continue, increase, decrease, switch).
ACTIONS = {
    "start": "start", "starts": "start", "started": "start", "starting": "start", "begin": "start",
    "begins": "start", "began": "start", "initiate": "start", "initiated": "start", "add": "start", "added": "start",
    "adding": "start", "restart": "start", "restarted": "start", "resume": "start", "resumed": "start",
    "stop": "stop", "stops": "stop", "stopped": "stop", "stopping": "stop", "discontinue": "stop",
    "discontinued": "stop", "withdraw": "stop", "withdrawn": "stop", "withdrew": "stop", "quit": "stop",
    "quits": "stop", "quitting": "stop", "cease": "stop", "ceased": "stop", "hold": "hold", "held": "hold",
    "pause": "hold", "paused": "hold", "omit": "hold", "continue": "continue", "continues": "continue",
    "continued": "continue", "keep": "continue", "kept": "continue", "maintain": "continue", "maintained": "continue",
    "increase": "increase", "increased": "increase", "increasing": "increase", "raise": "increase",
    "raised": "increase", "uptitrate": "increase", "decrease": "decrease", "decreased": "decrease",
    "decreasing": "decrease", "reduce": "decrease", "reduced": "decrease", "lower": "decrease", "lowered": "decrease",
    "taper": "decrease", "tapered": "decrease", "switch": "switch", "switched": "switch", "switching": "switch",
    # "Suspender" said, "suspend" written: a stop, as the prompt asks it written.
    "suspend": "stop", "suspended": "stop", "suspending": "stop",
    # A dose doubled or halved; a drug or a habit to avoid, forbidden or allowed.
    "double": "increase", "doubled": "increase", "doubling": "increase", "triple": "increase", "tripled": "increase",
    "halve": "decrease", "halved": "decrease", "halving": "decrease", "avoid": "stop", "avoided": "stop",
    "avoiding": "stop", "forbid": "stop", "forbidden": "stop", "prohibit": "stop", "prohibited": "stop",
    "allow": "continue", "allowed": "continue", "permit": "continue", "permitted": "continue",
}
# How often, made more or less often: a change of its own ("use albuterol less often", "space out the albuterol").
ACTION_PHRASES = {("more", "often"): "increase", ("more", "frequently"): "increase", ("less", "often"): "decrease",
                  ("less", "frequently"): "decrease", ("space", "out"): "decrease", ("spread", "out"): "decrease",
                  ("in", "half"): "decrease"}
# Taking a drug, or a habit, without changing it: "takes", "toma", "smokes", "fuma".
ENGLISH_TAKES = {"take": "present", "takes": "present", "taking": "present", "took": "past", "taken": "either",
                 "use": "present", "uses": "present", "using": "present", "smoke": "present", "smokes": "present",
                 "smoking": "present", "smoked": "either", "drink": "present", "drinks": "present",
                 "drinking": "present", "drank": "past"}
# English that puts a change or a habit in the past, said before it ("was stopped", "had started", "former smoker",
# "used to smoke"; "has been" only with a change: "has been stopped", not "has been taking"), or "ago" after it.
ENGLISH_PAST = frozenset("was were had previously already formerly former ex".split())
ENGLISH_HABITS = {"smoke": "smoking", "smokes": "smoking", "smoking": "smoking", "smoked": "smoking",
                  "smoker": "smoking", "tobacco": "smoking", "cigarettes": "smoking", "cigarette": "smoking",
                  "alcohol": "alcohol", "drink": "alcohol", "drinks": "alcohol", "drinking": "alcohol",
                  "drank": "alcohol", "drinker": "alcohol"}
ENGLISH_OBJECTS = {"dose": "dose", "doses": "dose", "dosage": "dose", "medication": "medication",
                   "medications": "medication", "medicine": "medication", "medicines": "medication",
                   "treatment": "treatment", "therapy": "treatment"}
# Drug classes and therapies, named as a drug is: what is done with one is compared as with a drug ("Suspender los
# antihipertensivos" is not "Continue antihypertensives"), and an English class the Spanish doesn't name is refused as
# a drug would be (drug_not_in_sources). Each: its English words, as words of the English; its Spanish keys.
DRUG_CLASSES = {
    "antihypertensive": ("antihypertensive antihypertensives",
                         "antihipertensivo antihipertensivos antihipertensiva antihipertensivas"),
    "antibiotic": ("antibiotic antibiotics", "antibiotico antibioticos antibioticoterapia"),
    "anticoagulant": ("anticoagulant anticoagulants anticoagulation thinner thinners",
                      "anticoagulante anticoagulantes anticoagulacion"),
    "antiplatelet": ("antiplatelet antiplatelets", "antiagregante antiagregantes antiagregacion"),
    "diuretic": ("diuretic diuretics", "diuretico diureticos"),
    "nsaid": ("nsaid nsaids antiinflammatory antiinflammatories", "aine aines antiinflamatorio antiinflamatorios"),
    "analgesic": ("analgesic analgesics painkiller painkillers", "analgesico analgesicos analgesia"),
    "steroid": ("steroid steroids corticosteroid corticosteroids",
                "corticoide corticoides esteroide esteroides corticosteroide corticosteroides"),
    "statin": ("statin statins", "estatina estatinas"),
    "antidepressant": ("antidepressant antidepressants", "antidepresivo antidepresivos"),
    "beta blocker": ("betablocker betablockers blocker blockers",
                     "betabloqueante betabloqueantes betabloqueador betabloqueadores"),
    "chemotherapy": ("chemotherapy chemo", "quimioterapia quimio"),
}
ENGLISH_CLASSES = {word: name for name, (words, _) in DRUG_CLASSES.items() for word in words.split()}
ANTI = {"inflammatory": "nsaid", "inflammatories": "nsaid"}  # after "anti-", as English splits it; not "inflammatory flare"
SPANISH_CLASSES = {key: name for name, (_, keys) in DRUG_CLASSES.items() for key in keys.split()}

# The Spanish, from each verb's forms (spanish.conjugate()): by kind, the infinitives; and the verbs said with a word
# after them ("dejar de", "cambiar a", "suspender temporalmente").
SPANISH_CHANGES = {
    "start": "iniciar empezar comenzar agregar anadir pautar reiniciar",
    "stop": "suspender retirar descontinuar quitar interrumpir evitar prohibir",
    "continue": "continuar seguir mantener permitir",
    "increase": "aumentar subir incrementar doblar duplicar triplicar",
    "decrease": "disminuir bajar reducir espaciar",
    "hold": "pausar omitir",
}
# Participles that say a state, not a past change: "prohibido el alcohol" forbids it now.
STATES = frozenset("prohibido prohibida prohibidos prohibidas permitido permitida permitidos permitidas".split())
# A change said by how much, whichever verb goes with it ("bajar a la mitad", "reducir a la mitad", "pasar al doble"),
# read where its last word is.
SPANISH_ACTION_PHRASES = {("a", "la", "mitad"): "decrease", ("al", "doble"): "increase"}
# Forms that are adjectives as often: "dosis baja", "tos continua". Accented, they are the verb ("bajó", "continúa").
NOT_CHANGES = frozenset("baja bajo bajas bajos continua continuo continuas continuos".split())
SPANISH_CHANGE_PHRASES = [("dejar", "de", "stop"), ("cambiar", "a", "switch"), ("cambiar", "por", "switch"),
                          ("suspender", "temporalmente", "hold")]
SPANISH_TAKES = "tomar usar utilizar consumir fumar beber"
SPANISH_HABITS = {"fumar": "smoking", "beber": "alcohol"}
SPANISH_HABIT_WORDS = {"fumador": "smoking", "fumadora": "smoking", "exfumador": "smoking", "exfumadora": "smoking",
                       "tabaco": "smoking", "tabaquismo": "smoking", "cigarrillos": "smoking", "cigarrillo": "smoking",
                       "cigarros": "smoking", "alcohol": "alcohol", "alcoholicas": "alcohol", "bebedor": "alcohol"}
SPANISH_OBJECTS = {"dosis": "dose", "medicamento": "medication", "medicamentos": "medication",
                   "medicacion": "medication", "farmaco": "medication", "farmacos": "medication",
                   "tratamiento": "treatment", "terapia": "treatment"}


def _verb_table() -> tuple[dict[str, tuple[str, frozenset[str]]], dict[str, tuple[str, frozenset[str]]]]:
    changes: dict[str, tuple[str, frozenset[str]]] = {}
    for kind, infinitives in SPANISH_CHANGES.items():
        for infinitive in infinitives.split():
            present, past = spanish.conjugate(infinitive)
            changes.update({form: (kind, past) for form in present | past})
    takes: dict[str, tuple[str, frozenset[str]]] = {}
    for infinitive in SPANISH_TAKES.split():
        present, past = spanish.conjugate(infinitive)
        takes.update({form: (SPANISH_HABITS.get(infinitive, ""), past) for form in present | past})
    return changes, takes


SPANISH_CHANGE, SPANISH_TAKE = _verb_table()
PHRASE_FORMS = {verb: spanish.conjugate(verb) for verb, _, _ in SPANISH_CHANGE_PHRASES}
ACTION_REACH = 4  # words either side of a drug, a habit, a dose or its number within which its change is said


@dataclass(frozen=True)
class _Mention:
    """A drug, a habit, a dose or a dose's number, and what is done with it: a change and its tense (present, past, or
    either, for an English "-ed" with nothing else to say which), "take", or nothing."""

    what: frozenset[str]  # a drug's names, ("smoking",), ("dose",), or a number's readings
    action: str | None
    tense: str


def _mentions(words: list[str], gaps: list[str], what: list[frozenset[str]], action_at, joins: frozenset[str],
              habit_at) -> list[_Mention]:
    """Each mention (what[i] not empty) with the verb said nearest it, a change or a taking, within ACTION_REACH words
    either side in its clause (a colon doesn't end one: "Metformina: suspender"), the one before it first; with none,
    the habit's own verb ("fuma"); and, with none either, the change of the mention it is listed with ("suspender
    metformina y glibenclamida")."""
    found: list[tuple[int, str | None, str]] = []
    for i in (i for i, names in enumerate(what) if names):
        action, tense = None, "present"
        for step in [d for k in range(1, ACTION_REACH + 1) for d in (-k, k)]:
            j = i + step
            between = range(min(i, j) + 1, max(i, j) + 1)
            if not 0 <= j < len(words) or any(re.search(r"[.,;!?\n]", gaps[k]) or words[k] in joins for k in between):
                continue
            action, tense = action_at(j)
            if action is not None:
                break
        if action is None:
            action, tense = habit_at(i) or (None, "present")
        found.append((i, action, tense))
    mentions = [_Mention(what[i], action, tense) for i, action, tense in found]
    for k in range(1, len(found)):
        listed = found[k][0] - found[k - 1][0] <= 3 and all(
            words[j] in joins or words[j] in spanish.STOPWORDS or words[j] in STOPWORDS or not words[j].isalpha()
            for j in range(found[k - 1][0] + 1, found[k][0]))
        if listed and mentions[k].action is None and mentions[k - 1].action not in (None, "take"):
            mentions[k] = _Mention(mentions[k].what, mentions[k - 1].action, mentions[k - 1].tense)
        elif listed and mentions[k - 1].action is None and mentions[k].action not in (None, "take"):
            mentions[k - 1] = _Mention(mentions[k - 1].what, mentions[k].action, mentions[k].tense)
    return mentions


def english_mentions(text: str) -> list[_Mention]:
    words = tokens(text)
    keys = [w.text for w in words]
    gaps = [text[words[i - 1].end : words[i].start] if i else "" for i in range(len(words))]
    doses = {start for _, start in english_number_items(text)
             if (unit := ENGLISH_UNITS.match(text, start)) and UNIT_NAMES.get(unit.group(2).lower().rstrip("s"),
                                                                              UNIT_NAMES.get(unit.group(2).lower()))
             in DOSE_UNITS}
    what = [frozenset({w}) if w.isalpha() and is_drug(w) else frozenset({ENGLISH_HABITS[w]}) if w in ENGLISH_HABITS
            else frozenset({ENGLISH_OBJECTS[w]}) if w in ENGLISH_OBJECTS
            else frozenset({ENGLISH_CLASSES[w]}) if w in ENGLISH_CLASSES
            else frozenset({ANTI[w]}) if w in ANTI and i and keys[i - 1] == "anti"
            else frozenset({w}) if words[i].start in doses else frozenset() for i, w in enumerate(keys)]
    phrase_at = _phrase_actions(keys, gaps)

    def past(j: int, change: bool) -> bool:
        before = keys[max(0, j - 2) : j]
        return (any(word in ENGLISH_PAST for word in before) or before == ["used", "to"] or "ago" in keys[j : j + 8]
                or (change and "been" in before))

    def action_at(j: int) -> tuple[str | None, str]:
        word = keys[j]
        if j in phrase_at:
            return phrase_at[j], "present"
        if word in ACTIONS:
            either = word.endswith("ed") or word in ("began", "held", "withdrawn", "withdrew", "kept", "quit", "forbidden")
            return ACTIONS[word], "past" if past(j, True) else "either" if either else "present"
        if word in ENGLISH_TAKES:
            if j and keys[j - 1] in ACTIONS:  # "stopped taking", "quit smoking": the change
                return action_at(j - 1)
            # "Use albuterol less often": how often it is taken, changed.
            later = next((phrase_at[k] for k in range(j + 1, min(len(keys), j + 4))
                          if k in phrase_at and not any(re.search(r"[.,;!?\n]", gaps[m]) for m in range(j + 1, k + 1))),
                         None)
            if later:
                return later, "present"
            return "take", "past" if past(j, False) else ENGLISH_TAKES[word]
        return None, "present"

    def habit_at(i: int) -> tuple[str, str] | None:
        word = keys[i]
        if word in ENGLISH_TAKES:
            return "take", "past" if past(i, False) else ENGLISH_TAKES[word]
        if word in ("smoker", "drinker") and past(i, False):
            return "take", "past"
        return None

    return _mentions(keys, gaps, what, action_at, ENGLISH_PARTS | {"or"}, habit_at)


def _phrase_actions(keys: list[str], gaps: list[str]) -> dict[int, str]:
    """Where an English phrase of ACTION_PHRASES starts, with its change."""
    return {j: ACTION_PHRASES[(keys[j], keys[j + 1])] for j in range(len(keys) - 1)
            if (keys[j], keys[j + 1]) in ACTION_PHRASES and not gaps[j + 1].strip()}


def english_change_kinds(text: str) -> set[str]:
    """Every change an English verb or phrase says, whatever it is said of."""
    words = tokens(text)
    keys = [w.text for w in words]
    gaps = [text[words[i - 1].end : words[i].start] if i else "" for i in range(len(words))]
    return {ACTIONS[k] for k in keys if k in ACTIONS} | set(_phrase_actions(keys, gaps).values())


def spanish_change_kinds(said: list[spanish.Token]) -> set[str]:
    """Every change a Spanish verb says, whatever it is said of."""
    keys = [t.key for t in said]
    found = {SPANISH_CHANGE[t.key][0] for t in said if t.key in SPANISH_CHANGE and t.text not in NOT_CHANGES}
    for verb, word, kind in SPANISH_CHANGE_PHRASES:
        present, past = PHRASE_FORMS[verb]
        found |= {kind for i in range(len(keys) - 1) if keys[i] in present | past and keys[i + 1] == word}
    return found | {kind for phrase, kind in SPANISH_ACTION_PHRASES.items()
                    if any(tuple(keys[i : i + len(phrase)]) == phrase for i in range(len(keys)))}


def english_classes(text: str) -> set[str]:
    words = [t.text for t in tokens(text)]
    return {ENGLISH_CLASSES[w] for w in words if w in ENGLISH_CLASSES} | {
        ANTI[w] for i, w in enumerate(words) if w in ANTI and i and words[i - 1] == "anti"}


# A change and those that go the other way: what the English may not say where the Spanish says the other.
OPPOSITES = {"stop": {"start", "continue"}, "hold": {"start", "continue"}, "start": {"stop", "hold"},
             "continue": {"stop", "hold"}, "increase": {"decrease"}, "decrease": {"increase"}, "switch": set()}


def spanish_mentions(said: list[spanish.Token], gaps: list[str] | None = None,
                     doses: dict[int, frozenset[str]] | None = None) -> list[_Mention]:
    keys = [t.key for t in said]
    gaps = gaps or [" "] * len(said)
    doses = doses or {}

    def names(i: int) -> frozenset[str]:
        key = keys[i]
        if key in SPANISH_HABIT_WORDS:
            return frozenset({SPANISH_HABIT_WORDS[key]})
        if key in SPANISH_TAKE and SPANISH_TAKE[key][0]:
            return frozenset({SPANISH_TAKE[key][0]})
        if key in SPANISH_OBJECTS:
            return frozenset({SPANISH_OBJECTS[key]})
        if key in SPANISH_CLASSES:
            return frozenset({SPANISH_CLASSES[key]})
        if i in doses:
            return doses[i]
        return _spanish_drug(key)

    def action_at(j: int) -> tuple[str | None, str]:
        key = keys[j]
        after = keys[j + 1] if j + 1 < len(keys) else ""
        before = keys[j - 1] if j else ""
        for verb, word, kind in SPANISH_CHANGE_PHRASES:
            present, past = PHRASE_FORMS[verb]
            if key in present | past and after == word:
                return kind, spanish.tense(said[j], past)
            if key == word and before in present | past:
                return kind, spanish.tense(said[j - 1], past)
        for phrase, kind in SPANISH_ACTION_PHRASES.items():
            if tuple(keys[j - len(phrase) + 1 : j + 1]) == phrase:
                return kind, "present"
        if key in SPANISH_CHANGE and said[j].text not in NOT_CHANGES:
            kind, past = SPANISH_CHANGE[key]
            return kind, "either" if key in STATES else spanish.tense(said[j], past)
        if key in SPANISH_TAKE:
            if j >= 2 and keys[j - 1] in ("de", "a") and action_at(j - 2)[0] not in (None, "take"):
                return action_at(j - 2)  # "dejó de tomar", "empezó a fumar": the change
            if j and action_at(j - 1)[0] not in (None, "take"):
                return action_at(j - 1)
            if not SPANISH_TAKE[key][0]:
                return "take", spanish.tense(said[j], SPANISH_TAKE[key][1])
        return None, "present"

    def habit_at(i: int) -> tuple[str, str] | None:
        key = keys[i]
        if key in SPANISH_TAKE and SPANISH_TAKE[key][0]:
            return "take", spanish.tense(said[i], SPANISH_TAKE[key][1])
        if key.startswith("ex") and key in SPANISH_HABIT_WORDS:
            return "take", "past"
        return None

    return _mentions(keys, gaps, [names(i) for i in range(len(said))], action_at, SPANISH_PARTS | {"o"}, habit_at)


def english_actions(text: str) -> set[str]:
    return set(english_action_items(text))


def english_action_items(text: str) -> list[str]:
    """The changes the English makes to a drug, a dose or a habit, in order."""
    return [m.action for m in english_mentions(text) if m.action not in (None, "take")]


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


def spanish_actions(said: list[spanish.Token]) -> set[str]:
    return set(spanish_action_items(said))


def spanish_action_items(said: list[spanish.Token]) -> list[str]:
    """The changes the Spanish makes to a drug, a dose or a habit, in order."""
    return [m.action for m in spanish_mentions(said) if m.action not in (None, "take")]


def _same_action(english: _Mention, said: _Mention) -> bool:
    """A change is the same change in a tense that may be the same ("stopped" either way); taking is taking, as now or
    in the past, or nothing said, unless the Spanish took it in the past ("tomaba")."""
    if english.action in (None, "take") and said.action in (None, "take"):
        if said.action == "take" and said.tense == "past":
            return english.action == "take" and english.tense in ("past", "either")
        return not (english.action == "take" and said.action == "take" and "either" not in (english.tense, said.tense)
                    and english.tense != said.tense)
    return english.action == said.action and "either" in (english.tense, said.tense) or (
        english.action == said.action and english.tense == said.tense)


def _actions_changed(english: list[_Mention], said: list[_Mention]) -> bool:
    """Whether a drug, a dose or a habit is changed differently, or now or then, in the English: each English mention
    matched to the first Spanish one of the same thing after the last matched (or else before it: "la dosis de
    losartán" is "the losartan dose"; the order is compared in _in_order()), and every change on either side matched;
    but a drug
    or a number one side says and the other doesn't is the drug or number checks' (drug_not_in_sources,
    drug_dropped, number_not_in_sources)."""
    j = 0
    matched: set[int] = set()
    said_names = {name for m in said for name in m.what}
    english_names = {name for m in english for name in m.what}
    changes = {m.action for m in english} - {None, "take"}, {m.action for m in said} - {None, "take"}

    def unmatched(mention: _Mention, other_names: set[str], other_changes: set) -> bool:
        if mention.action in (None, "take"):
            return False
        # A drug or a number the other side doesn't say is the drug or number checks' to refuse: its change need only
        # be made there too.
        if not mention.what & other_names:
            return mention.action not in other_changes
        return True

    for mention in english:
        k = next((k for k in [*range(j, len(said)), *range(j)] if k not in matched and said[k].what & mention.what),
                 None)
        if k is None:
            if unmatched(mention, said_names, changes[1]):
                return True
            continue
        if not _same_action(mention, said[k]):
            return True
        matched.add(k)
        j = k + 1
    return any(unmatched(m, english_names, changes[0]) for k, m in enumerate(said) if k not in matched)



def english_content(text: str) -> int:
    return sum(1 for t in tokens(text) if t.text.isalpha() and len(t.text) >= 2 and t.text not in STOPWORDS
               and t.text not in validation.FREE_WORDS)


def spanish_content(said: list[spanish.Token]) -> int:
    return sum(1 for t in said if spanish._is_content(t))


# English for what spanish.py's lists say that validation.py's English cues don't, read as validation.py reads its own
# (Qualifier rows): "infarto previo" is a prior, old or remote MI, "infarto hace años" an MI years ago or in the past,
# "el año pasado" last year; "neumonía en estudio" pneumonia under investigation, "no se puede descartar" can't be
# ruled out, "compatible con neumonía" consistent with pneumonia; "diabetes en la familia" diabetes in the family. Not
# "old" in "68-year-old", "in the past" in "in the past 3 days", nor "years ago" after a number ("10 years ago" is a
# date, as "hace 10 años" is). A cue counts only where it reaches a word ("previously 18" doesn't).
_NEVER = re.compile(r"(?!)")
_PERIOD = r"(?:days?|weeks?|months?|years?|hours?)"
MORE_ENGLISH = (
    validation.Qualifier(
        "history_dropped", re.compile(r"\b(?:prior|previous|previously|remote)\b|(?<![-\w])old\b(?!-)", re.I),
        re.compile(rf"\bin the past\b(?!\s+(?:\d|\w+\s+{_PERIOD}\b|{_PERIOD}\b|few|several|couple))|\blast year\b"
                   r"|\bpreviously\b|(?<!\d )\byears ago\b", re.I),
        None, False, validation.TERMINATORS),
    validation.Qualifier(
        "certainty_dropped", re.compile(
            r"\b(?:doubtful|presumed|presumptive|consistent with|compatible with|suggestive of|suggesting|"
            r"in keeping with|seems?(?: to(?: be| have)?)?|appears? to(?: be| have)?|apparently|looks? like|"
            r"working diagnosis)\b", re.I),
        re.compile(r"\b(?:under (?:investigation|study)|cannot (?:be )?ruled? out|can't (?:be )?ruled? out|"
                   r"not ruled out|doubtful|presumed|being considered|to be (?:evaluated|assessed|ruled out|excluded))\b",
                   re.I),
        None, False, validation.TERMINATORS),
    validation.Qualifier("experiencer_dropped", _NEVER,
                         re.compile(r"\b(?:in (?:the|her|his|their) family|runs in the family)\b", re.I),
                         None, False, validation.TERMINATORS | validation.REPORTING),
)
_NAMES = {"negation_dropped": "negation", "experiencer_dropped": "experiencer", "certainty_dropped": "certainty",
          "history_dropped": "history"}


# A doubt given as a heading, before a colon: "Presumptive diagnosis: pneumonia" reaches past it, as the Spanish does.
DOUBT_HEADING = re.compile(r"\b(?:presumptive|working) diagnosis\s*:", re.IGNORECASE)


def _more_english(text: str) -> list[tuple[str, set[int], set[int]]]:
    """For each MORE_ENGLISH row, its kind, the content words its cues reach in the text, and the cues' words."""
    text = DOUBT_HEADING.sub(lambda m: m.group()[:-1] + " ", text)  # the same length: the words' places hold
    words = tokens(text)
    found = []
    for qualifier in MORE_ENGLISH:
        reached, cues = validation._reach(text, words, qualifier)
        content = {i for i in reached if words[i].text.isalpha() and len(words[i].text) >= 2
                   and words[i].text not in STOPWORDS}
        found.append((_NAMES[qualifier.reason], content, set(cues) if content else set()))
    return found


# English words that are a negation of their own, as Spanish says "no" and the word: "no controlada" is
# "uncontrolled", "tos no productiva" a "nonproductive cough"; or as a Spanish word negates itself
# (spanish.SELF_NEGATING): "afebril" is "afebrile", "indoloro" "painless" or "nontender", "inconsciente"
# "unconscious"; and "absent" before what it negates ("absent distal pulses"), which validation.py reads only after it.
# Read so here only; validation.py reads English dictation as it did.
NEGATED_WORDS = frozenset(
    """uncontrolled uncomplicated untreated unvaccinated unimmunized nonproductive nontender nonradiating nonpitting
    nonpalpable nondistended nonreactive nonsmoker afebrile apyrexial apyretic asymptomatic eupneic eupnoeic anicteric
    acyanotic painless unconscious absent""".split()
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
    names = _NAMES
    found = {}
    for qualifier in validation.QUALIFIERS:
        read = _without_told_relatives(text) if qualifier.reason == "experiencer_dropped" else text
        found[names[qualifier.reason]] = bool(validation._reach(read, tokens(read), qualifier)[0])
    for kind, reached, _ in _more_english(text):
        found[kind] = found[kind] or bool(reached)
    found["negation"] = found["negation"] or any(t.text in NEGATED_WORDS for t in tokens(text))
    return found


# How long, as a number and its period: "5 days", "dos semanas", "for a week" (1 week).
SPANISH_PERIODS = {"hora": "hour", "horas": "hour", "minuto": "minute", "minutos": "minute", "dia": "day",
                   "dias": "day", "semana": "week", "semanas": "week", "mes": "month", "meses": "month", "ano": "year",
                   "anos": "year"}
ENGLISH_PERIODS = {"hour": "hour", "hours": "hour", "minute": "minute", "minutes": "minute", "day": "day",
                   "days": "day", "week": "week", "weeks": "week", "month": "month", "months": "month", "year": "year",
                   "years": "year"}
# "A week" is one week after a word of how long or when ("for a week", "a month ago"), not in "twice a day".
ONE_PERIOD = re.compile(
    r"\b(?:for|in|over|during|within|after|past|about|nearly|almost)\s+(?:a|an)\s+(hour|minute|day|week|month|year)\b"
    r"|\b(?:a|an)\s+(hour|minute|day|week|month|year)\s+(?:ago|later|earlier)\b",
    re.IGNORECASE,
)


def english_periods(text: str) -> list[tuple[str, str]]:
    """Each length of time the English gives, in order, as (number, period)."""
    words = spanish.tokens(text)
    found = []
    for value, start in english_number_items(text):
        at = next((k for k, w in enumerate(words) if w.start >= start), len(words))
        after = next((w.key for w in words[at + 1 :]
                      if not w.key[:1].isdigit() and w.key not in validation.NUMBER_WORDS), "")
        if after in ENGLISH_PERIODS:
            found.append((start, (value, ENGLISH_PERIODS[after])))
    for match in ONE_PERIOD.finditer(text):
        found.append((match.start(), ("1", (match.group(1) or match.group(2)).lower())))
    return [period for _, period in sorted(found)]


def spanish_periods(said: list[spanish.Token], terms: list[_Term],
                    position: dict[int, int]) -> list[set[tuple[str, str]]]:
    """Each length of time the Spanish says, in order, as the (number, period) it may be read as."""
    found = []
    for term in terms:
        after = position.get(term.last, -1) + 1
        if term.kind == "number" and 0 < after < len(said) and said[after].key in SPANISH_PERIODS:
            found.append({(value, SPANISH_PERIODS[said[after].key]) for value in term.values})
    return found


# ---- In order, and paired ------------------------------------------------------------------------------------------

# What a number measures, named before it: the vital signs and common results, each by an id of its own, as Spanish
# (keys) and English (lower case) name it. A number's label is the first of these or a drug before it in its clause,
# past function words, units, dose forms, the words in LABEL_GLUE and up to LABEL_REACH other words ("la A1c de la
# última vez fue 6,1"); else the first other word, if any; a number joined to the one before it ("142 sobre 88", "10 to
# 20 mg") has that one's label; with none before it, a drug after it in its clause ("50 mg de losartán"); and a number
# of days, weeks, times or tablets (COUNTS) has none.
LABELS = {
    "bp": ("ta; tension arterial; presion arterial; tension; presion; pa", "bp; blood pressure; pressure"),
    "hr": ("fc; frecuencia cardiaca; pulso; frecuencia del pulso", "hr; heart rate; pulse"),
    "rr": ("fr; frecuencia respiratoria", "rr; respiratory rate; respirations"),
    "temp": ("temperatura; temp; fiebre; febricula", "temperature; temp; fever"),
    "spo2": ("sato2; sat o2; sat; saturacion; saturando; spo2; oximetria",
             "spo2; sat; sats; saturation; saturating; o2 sat; oxygen saturation; pulse ox; oximetry"),
    "glucose": ("glucosa; glucemia; glicemia; azucar; glucometria", "glucose; sugar; sugars; glycemia"),
    "a1c": ("hba1c; a1c; hemoglobina glucosilada; hemoglobina glicosilada; hemoglobina glicada",
            "a1c; hba1c; hemoglobin a1c; glycated hemoglobin"),
    "hb": ("hemoglobina; hb; hgb", "hemoglobin; hb; hgb"),
    "k": ("potasio; k", "potassium; k"),
    "na": ("sodio; na", "sodium; na"),
    "cr": ("creatinina; cr", "creatinine; cr"),
    "weight": ("peso; pesa", "weight; weighs; weighed"),
    "height": ("talla; estatura; mide", "height"),
    "bmi": ("imc", "bmi"),
    "cholesterol": ("colesterol", "cholesterol"),
    "ldl": ("ldl", "ldl"),
    "hdl": ("hdl", "hdl"),
    "tg": ("trigliceridos", "triglycerides"),
    "tsh": ("tsh", "tsh"),
    "inr": ("inr", "inr"),
    "platelets": ("plaquetas", "platelets; platelet; plt"),
    "wbc": ("leucocitos; leucos; globulos blancos", "wbc; leukocytes; white count; white cells; white blood cells"),
    "gfr": ("tfg; filtrado glomerular; filtrado", "gfr; egfr"),
    "ef": ("fevi; fraccion de eyeccion", "ef; lvef; ejection fraction"),
}
SPANISH_LABELS = sorted(((tuple(name.split()), label) for label, (names, _) in LABELS.items()
                         for name in names.split("; ")), key=lambda item: -len(item[0]))
ENGLISH_LABELS = sorted(((tuple(name.split()), label) for label, (_, names) in LABELS.items()
                         for name in names.split("; ")), key=lambda item: -len(item[0]))
LABEL_GLUE = frozenset(
    """sobre aproximadamente alrededor cerca actualmente hoy sistolica diastolica media basal capilar ayunas aire
    ambiente mmhg lpm rpm over approximately around nearly roughly currently today systolic diastolic mean baseline
    capillary fasting random room air measured tablet tablets capsule capsules puff puffs drop drops""".split()
) | spanish.UNIT_WORDS | spanish.COUNTED | frozenset(UNIT_NAMES)
# "sobre" counts sachets, but joins the two numbers of a blood pressure first ("142 sobre 88").
COUNTS = spanish.COUNTED - {"sobre"} | frozenset(
    """time times dose doses hour hours day days week weeks month months year years minute minutes tablet tablets
    capsule capsules pill pills drop drops puff puffs patch patches""".split()
)
LABEL_REACH = 3  # words before a number, other than function words and LABEL_GLUE, its label may be past
SPANISH_JOINS = frozenset("sobre a y".split())  # between two numbers of one reading or range: "142 sobre 88", "10 a 20"
ENGLISH_JOINS = frozenset("over to and".split())
CLAUSE_END = re.compile(r"[.,;!?\n]")


def _labels(words: list[str], gaps: list[str], numbered: list[int], names: list, stop: frozenset[str],
            drug, joins: frozenset[str]) -> list[tuple[str, object] | None]:
    """The label of each number (numbered: the index of its first word), as ("id", label), ("drug", names it may be),
    ("word", word), or None: see LABELS. gaps[i] is the text between words i - 1 and i."""
    first = set(numbered)
    is_number = [word[:1].isdigit() or word in validation.NUMBER_WORDS or word in spanish.NUMBER_WORDS
                 for word in words]
    found: list[tuple[str, object] | None] = []

    def named(i: int):
        for phrase, label in names:
            if tuple(words[i - len(phrase) + 1 : i + 1]) == phrase:
                return ("id", label)
        names_of = drug(words[i])
        return ("drug", names_of) if names_of else None

    for k, at in enumerate(numbered):
        label = None
        # A number of days, weeks, times or tablets counts them: it measures nothing.
        if at + 1 < len(words) and words[at + 1] in COUNTS:
            found.append(None)
            continue
        i, passed = at - 1, 0
        while i >= 0 and not CLAUSE_END.search(gaps[i + 1]) and passed < LABEL_REACH:
            if is_number[i]:
                joined = all(words[j] in joins or words[j] in stop or words[j] in LABEL_GLUE
                             for j in range(i + 1, at) if not is_number[j])
                previous = [n for n, start in enumerate(numbered[:k]) if start <= i]
                label = (found[previous[-1]] if joined and previous else None) or label
                break
            if words[i] not in stop and words[i] not in LABEL_GLUE:
                if named(i):
                    label = named(i)
                    break
                label = label or ("word", words[i])
                passed += 1
            i -= 1
        if label is None or label[0] == "word":
            j = at + 1
            while j < len(words) and j - at <= 5 and not CLAUSE_END.search(gaps[j]):
                if j not in first and not is_number[j] and words[j] not in stop and words[j] not in LABEL_GLUE:
                    after = named(j)
                    if after and after[0] == "drug":
                        label = after
                    break
                j += 1
        found.append(label)
    return found


def _paired(english, spanish_label) -> bool:
    """Whether a number's English label may stand for its Spanish one: the same vital sign or result; a drug the
    Spanish one may be; and no other word, nor none, for a vital sign or result the Spanish names."""
    kind = english[0] if english else None
    said = spanish_label[0] if spanish_label else None
    if kind == "id":
        return said == "id" and english[1] == spanish_label[1]
    if kind == "drug":
        return said != "id" and not (said == "drug" and not english[1] & spanish_label[1])
    if kind == "word":
        return said != "id"
    return True


def _follows(english: list, said: list, same, shared: bool = False) -> bool:
    """Whether the English items are the Spanish ones in order, some left out: each matched to the first Spanish item
    after the last one matched (or from it, if items may share one) that it may be, which finds a match whenever there
    is one."""
    j = 0
    for item in english:
        while j < len(said) and not same(item, said[j]):
            j += 1
        if j == len(said):
            return False
        j += 0 if shared else 1
    return True


# Where a statement's clauses part: punctuation, and "and" or "but" ("y", "e", "pero").
SPANISH_PARTS = frozenset("y e pero".split())
ENGLISH_PARTS = frozenset("and but".split())
KINDS = ("negation", "certainty", "history", "experiencer")


def _skeleton(word: str) -> str | None:
    """The first five letters of a word as both languages may spell it, so "hipertensión" and "hypertension", and
    "neumonía" and "pneumonia", meet: None for a shorter word."""
    word = spanish.fold(word)
    for spelled, said in (("ph", "f"), ("th", "t"), ("y", "i"), ("k", "c"), ("qu", "c"), ("ch", "c"), ("z", "s")):
        word = word.replace(spelled, said)
    word = re.sub(r"(.)\1", r"\1", word[1:] if word.startswith(("pn", "ps")) else word)
    return word[:5] if len(word) >= 5 and word.isalpha() else None


@dataclass(frozen=True)
class _Part:
    """A clause of a statement: the kinds of qualifier said in it or reaching a finding of it, the relatives it names,
    and the skeletons of its findings (_skeleton())."""

    kinds: frozenset[str]
    relatives: frozenset[str]
    skeletons: frozenset[str]


def _parts(words: list[str], gaps: list[str], content: list[bool], flagged: dict[str, set[int]],
           cues: dict[str, set[int]], relatives: list[frozenset[str]], joins: frozenset[str]) -> list[_Part]:
    """The statement's clauses, in order (see _Part). content: the words that are findings (not function words, nor a
    qualifier's cue); flagged and cues: for each kind, the words its cues reach and the cues' own words."""
    parts: list[list[int]] = [[]]
    for i, word in enumerate(words):
        if CLAUSE_END.search(gaps[i]) or word in joins:
            parts.append([])
        if word not in joins:
            parts[-1].append(i)
    found = []
    for part in parts:
        findings = [i for i in part if content[i]]
        if not findings:
            continue
        kinds = {kind for kind in KINDS
                 if any(i in flagged[kind] for i in findings) or any(i in cues[kind] for i in part)}
        found.append(_Part(frozenset(kinds), frozenset().union(*(relatives[i] for i in part)),
                           frozenset(s for i in findings if (s := _skeleton(words[i])))))
    return found


# What can be ordered, in English, by what in Spanish says it (spanish.ORDERABLE_WORDS, the list the instruction
# detector reads too): an English one the Spanish a statement cites doesn't say is refused, so an instruction cut from the note can't come back in a neighbouring statement ("Continue losartan 50 mg
# daily; chest X-ray."). Abbreviations ("CBC", "CT", "MRI", "ECG") are the abbreviation check's.
ORDERABLES = {name: (english, spanish.ORDERABLE_WORDS[name]) for name, english in {
    "xray": r"x-?rays?|radiographs?|radiography|films?",
    "ct": r"ct scans?|cat scans?|computed tomography|tomography",
    "mri": r"magnetic resonance|resonance",
    "ultrasound": r"ultrasounds?|sonograms?|sonography|ultrasonography",
    "echo": r"echocardiograms?|echocardiography|echo",
    "ecg": r"electrocardiograms?|electrocardiography",
    "labs": r"labs?|laboratory|blood (?:work|tests?)|bloodwork|lab tests?|tests",
    "blood count": r"blood counts?|complete blood count|hemogram",
    "panel": r"panels?|profiles?",
    "culture": r"cultures?",
    "biopsy": r"biops(?:y|ies)",
    "referral": r"referrals?|consults?",
    "prescription": r"prescriptions?|scripts?|rx",
    "refill": r"refills?|re-?fill",
    "mammogram": r"mammograms?|mammography",
    "spirometry": r"spirometry",
    "colonoscopy": r"colonoscopy|colonoscopies",
    "endoscopy": r"endoscopy|endoscopies",
    "densitometry": r"densitometry|bone density|dexa",
}.items()}
ENGLISH_ORDERABLES = [(name, re.compile(rf"\b(?:{pattern})\b", re.IGNORECASE))
                      for name, (pattern, _) in ORDERABLES.items()]
SPANISH_ORDERABLES = {word: {name for name, (_, words) in ORDERABLES.items() if word in words.split()}
                      for _, words in ORDERABLES.values() for word in words.split()}


def english_orderables(text: str) -> set[str]:
    """What can be ordered that the English names, by ORDERABLES' name ("X-ray", "labs", "referral")."""
    return {name for name, pattern in ENGLISH_ORDERABLES if pattern.search(text)}


# Opposites, each class with its English and its Spanish words (keys): an English one the Spanish doesn't say is
# refused (polarity_changed): "hipotensión" is not "hypertension", nor "peor" "better", "mal controlada" "controlled",
# "negativo" "positive". Matched longest first, so "poorly controlled" is not "controlled".
POLARITY = {
    "level": {"high": (r"high|elevated|raised", "alta alto altas altos elevada elevado elevadas elevados aumentada "
                       "aumentado aumentadas aumentados aumento elevacion"),
              "low": (r"low(?! back)|lowered|decreased|reduced|diminished",
                      "baja bajo bajas bajos disminuida disminuido disminuidas disminuidos reducida reducido descendida "
                      "descendido disminucion descenso reduccion"),
              "normal": (r"normal(?! saline)|unremarkable|within normal limits", "normal normales")},
    "course": {"better": (r"better|improved|improving|improvement|improves", "mejor mejora mejoria mejorado mejorada "
                          "mejorando mejoro mejoran"),
               "worse": (r"worse|worsened|worsening|deteriorated|deteriorating|worsens", "peor empeora empeoramiento "
                         "empeorado empeorada empeorando empeoro empeoran")},
    "control": {
        "uncontrolled": (r"poorly controlled|not controlled|uncontrolled|badly controlled|out of control",
                         "mal controlada|mal controlado|no controlada|no controlado|descontrolada|descontrolado"),
        "controlled": (r"(?:well[- ])?controlled|under control",
                       "controlada|controlado|controladas|controlados|bajo control"),
    },
    "tolerance": {"allergic": (r"allergic|allerg(?:y|ies)", "alergico alergica alergicos alergicas alergia alergias"),
                  "tolerates": (r"tolerates|tolerated|tolerating|tolerance|tolerating", "tolera toleran tolero "
                                "tolerado tolerada tolerancia")},
    "result": {"positive": (r"positive", "positivo positiva positivos positivas"),
               "negative": (r"negative", "negativo negativa negativos negativas")},
    "prefix": {"hyper": (r"hyper\w+", "hiper"), "hypo": (r"hypo\w+", "hipo"), "tachy": (r"tachy\w+", "taqui"),
               "brady": (r"brady\w+", "bradi")},
}
# Findings an English statement may never add: what the Spanish it cites doesn't say (finding_not_in_sources).
FINDINGS = {
    "pregnancy": (r"pregnan\w*|gestation\w*|gravid", "embarazada embarazadas embarazo embarazos gestacion gestante "
                  "gestantes encinta"),
    "suicide": (r"suicid\w*", "suicida suicidas suicidio suicidios autolitica autolitico"),
    "cancer": (r"cancers?|malignan\w*|carcinomas?|neoplasm\w*|tumou?rs?|metasta\w*|oncolog\w*",
               "cancer canceres maligno maligna malignidad carcinoma carcinomas neoplasia neoplasias tumor tumores "
               "tumoracion metastasis oncologico oncologica"),
    "intubation": (r"intubat\w*", "intubar intubacion intubado intubada intubele intubarlo intubarla"),
}


def _polarities(english: str, said: list[spanish.Token]) -> tuple[set[str], set[str]]:
    """The opposites (POLARITY) the English says, and those the Spanish does, as "class:value"."""
    lowered = english.lower()
    keys = [t.key for t in said]
    written, spoken = set(), set()
    for name, values in POLARITY.items():
        taken: list[tuple[int, int]] = []
        for value, (english_words, _) in sorted(values.items(), key=lambda v: -len(v[1][0])):
            for match in re.finditer(rf"\b(?:{english_words})\b", lowered):
                if not any(start <= match.start() < end for start, end in taken):
                    taken.append(match.span())
                    written.add(f"{name}:{value}")
        if name == "prefix":
            spoken |= {f"{name}:{value}" for value, (_, prefix) in values.items()
                       if any(key.startswith(prefix) and len(key) >= len(prefix) + 4 for key in keys)}
            continue
        phrases = [(tuple(phrase.split()), value) for value, (_, words) in values.items()
                   for phrase in words.split("|" if "|" in words else " ")]
        for first, last in _phrase_spans(keys, [phrase for phrase, _ in phrases]):
            spoken |= {f"{name}:{value}" for phrase, value in phrases if tuple(keys[first : last + 1]) == phrase}
    return written, spoken


def _phrase_spans(keys: list[str], phrases: list[tuple[str, ...]]) -> list[tuple[int, int]]:
    """Where the phrases are said, longest first, none overlapping ("mal controlada" before "controlada")."""
    return spanish._matches(keys, sorted(set(phrases), key=len, reverse=True))


def _findings(english: str, said: list[spanish.Token]) -> bool:
    """Whether the English names a finding of FINDINGS the Spanish doesn't."""
    keys = {t.key for t in said}
    return any(re.search(rf"\b(?:{english_words})\b", english, re.IGNORECASE) and not keys & set(spanish_words.split())
               for english_words, spanish_words in FINDINGS.values())


HAVING_NOW = re.compile(r"\b(?:is|are|am) having\b|\bcurrent(?:ly)?\b|\bnow\b|\bongoing\b|\bactive\b", re.IGNORECASE)
# What ends a qualifier's carrying over an English list, as spanish.LIST_STOPS does the Spanish: "No fever, cough or
# dyspnea" carries, "No fever, has a cough" doesn't.
ENGLISH_LIST_STOPS = validation.REPORTING | frozenset(
    "has have had is are was were with shows showed presents presented endorses endorsed complains persists".split()
)
ENGLISH_LIST_JOINS = frozenset("and or nor".split())
ENGLISH_CONTRASTS = frozenset("but however although though except apart aside yet whereas".split())


def _english_flagged(text: str, words: list[textnorm.Token]) -> tuple[dict[str, set[int]], dict[str, set[int]]]:
    """For each kind, the English words a qualifier reaches (as _english_qualified() reads them), and its cues' words,
    MORE_ENGLISH's read the same way, with NEGATED_WORDS among the cues. Cues of a kind that reach nothing in the
    statement ("either" closing it) say nothing of their clause, and aren't counted."""
    names = _NAMES
    flagged: dict[str, set[int]] = {}
    cues: dict[str, set[int]] = {}
    for qualifier in validation.QUALIFIERS:
        read = _without_told_relatives(text) if qualifier.reason == "experiencer_dropped" else text
        reached, cue_words = validation._reach(read, tokens(read), qualifier)
        reached = spanish.carry_over_list(
            text, words, [w.text for w in words], reached - cue_words,
            ENGLISH_CONTRASTS | validation.NEXT_CLAUSE | validation.CLAUSE_STARTS,
            (qualifier.ends - ENGLISH_CONTRASTS) | validation.LIST_ENDS | ENGLISH_LIST_STOPS, ENGLISH_LIST_JOINS)
        flagged[names[qualifier.reason]] = set(reached)
        cues[names[qualifier.reason]] = set(cue_words) if reached else set()
    for kind, reached, cue_words in _more_english(text):
        flagged[kind] |= reached
        cues[kind] |= cue_words
    cues["negation"] |= {i for i, w in enumerate(words) if w.text in NEGATED_WORDS}
    return flagged, cues


# For each class compared in order, the reasons that already refuse a change in it: what fails those isn't compared
# again.
ALREADY = {
    "numbers": {"number_not_in_sources", "number_unreadable", "ambiguous_number", "source_superseded"},
    "periods": {"number_not_in_sources", "period_not_in_sources", "source_superseded"},
    "labels": {"drug_not_in_sources", "drug_dropped", "abbreviation_not_in_sources"},
    "drugs": {"drug_not_in_sources", "drug_dropped", "source_superseded"},
    "sides": {"side_not_in_sources"},
    "routes": {"route_not_in_sources"},
    "severity": {"severity_not_in_sources"},
    "frequencies": {"frequency_not_in_sources"},
    "relatives": {"experiencer_changed"},
    "actions": {"action_changed"},
    "clauses": {f"{kind}_{change}" for kind in ("negation", "certainty", "history", "experiencer")
                for change in ("dropped", "added", "changed")},
}


def _in_order(english: str, said_index: list[int], terms: list[_Term], ctx: _Context, refused: set[str]) -> set[str]:
    """Each class the checks read, in the English as in the Spanish, some left out but none moved (order_changed);
    each number measuring what the Spanish says it measures (label_changed); and each qualifier on the clause, and the
    findings, it qualifies in the Spanish (order_changed). A class already refused (refused, ALREADY) isn't compared
    again."""
    found: set[str] = set()
    said = [ctx.toks[i] for i in said_index]
    said_text = " ".join(t.text for t in said)
    position = {index: k for k, index in enumerate(said_index)}
    gaps = _said_gaps(said_index, ctx)

    numbered = [t for t in terms if t.kind == "number"]
    spoken = {value for t in numbered for value in t.values}
    english_items = [(value, start) for value, start in english_number_items(english) if value in spoken]
    spanish_labels = _labels([t.key for t in said], gaps, [position[t.first] for t in numbered],
                             SPANISH_LABELS, spanish.STOPWORDS, _spanish_drug, SPANISH_JOINS)
    labelled = spanish.tokens(english)
    label_gaps = [english[labelled[i - 1].end : labelled[i].start] if i else "" for i in range(len(labelled))]
    at = [next((k for k, w in enumerate(labelled) if w.start >= start), len(labelled) - 1)
          for _, start in english_items]
    english_labels = _labels([w.key for w in labelled], label_gaps, at, ENGLISH_LABELS, STOPWORDS, _english_drug,
                             ENGLISH_JOINS)
    numbers_said = list(zip(numbered, spanish_labels))
    numbers_written = list(zip(english_items, english_labels))
    if refused & ALREADY["numbers"]:
        pass
    elif not _follows(numbers_written, numbers_said, lambda e, s: e[0][0] in s[0].values):
        found.add("order_changed")
    elif not refused & ALREADY["labels"] and not _follows(
            numbers_written, numbers_said, lambda e, s: e[0][0] in s[0].values and _paired(e[1], s[1])):
        found.add("label_changed")

    words = tokens(english)
    english_words = [w.text for w in words]
    drugs = [t for t in terms if t.kind == "drug"]
    named = {name for t in drugs for name in t.values}
    sides = [t for t in terms if t.kind == "side"]
    severity = [spanish.SEVERITY[t.key] for t in said if t.key in spanish.SEVERITY]
    classes = [
        ("drugs", [w for w in english_words if w in named], drugs, lambda e, s: e in s.values),
        ("sides", english_sides(english), sides, lambda e, s: e in s.values),
        ("routes", [name for _, name in english_route_items(english)],
         [name for _, name in spanish.route_items(said_text)], lambda e, s: e == s),
        ("severity", [w for w in english_words if w in ENGLISH_SEVERITY], severity, lambda e, s: e in s),
        ("frequencies", [read for _, read in english_frequency_items(english)],
         [read for _, read in spanish.frequency_items(said_text)], lambda e, s: e <= s),
        ("relatives", [ENGLISH_RELATIVES[w] for w in english_words if w in ENGLISH_RELATIVES], spanish.relatives(said),
         lambda e, s: e in s),
        ("actions", english_action_items(english),
         [m.action for m in _spanish_mentions(said_index, ctx) if m.action not in (None, "take")], lambda e, s: e == s),
        ("periods", english_periods(english), spanish_periods(said, terms, position), lambda e, s: e in s),
    ]
    # A frequency read two ways ("cada tercer día") may be written both ways, each reading in its place.
    if not all(_follows(english_items, said_items, same, shared=name == "frequencies")
               for name, english_items, said_items, same in classes if not refused & ALREADY[name]):
        found.add("order_changed")

    if refused & ALREADY["clauses"]:
        return found
    said_flagged = {kind: {position[i] for i in ctx.qualified[kind] if i in position} for kind in KINDS}
    english_flagged, english_cues = _english_flagged(english, words)
    if not any(said_flagged.values()) and not any(english_flagged.values()) and not any(english_cues.values()):
        return found
    said_cues = ctx.qualifier_cues
    findings = [spanish._is_content(t) and not any(said_index[k] in c for c in said_cues.values())
                for k, t in enumerate(said)]
    said_parts = _parts([t.key for t in said], gaps, findings,
                        said_flagged, {kind: {position[i] for i in said_cues[kind] if i in position} for kind in KINDS},
                        [frozenset(spanish.RELATIVES.get(t.text, ())) for t in said], SPANISH_PARTS)
    english_gaps = [english[words[i - 1].end : words[i].start] if i else "" for i in range(len(words))]
    every_cue = set().union(*english_cues.values())
    english_parts = _parts(english_words, english_gaps,
                           [w.isalpha() and len(w) >= 2 and w not in STOPWORDS and i not in every_cue
                            for i, w in enumerate(english_words)],
                           english_flagged, english_cues,
                           [frozenset({ENGLISH_RELATIVES[w]}) if w in ENGLISH_RELATIVES else frozenset()
                            for w in english_words], ENGLISH_PARTS)
    # The Spanish's findings, not its cues: "palpan" in "no se palpan masas" is no finding for "palpable" to match.
    skeletons = {s for k, t in enumerate(said) if findings[k] if (s := _skeleton(t.key))}
    reached = {s for k, t in enumerate(said) if any(k in f for f in said_flagged.values()) if (s := _skeleton(t.key))}

    # An English clause sits in a run of Spanish ones ("No edema or cyanosis" in "Sin edema, cianosis") that each say
    # the same qualifiers, with the findings both name; or, saying none, in any, unless it names a finding a Spanish
    # qualifier reaches. Clauses may share a Spanish one, and keep their order.
    def same(e: _Part, run: list[_Part]) -> bool:
        shared = e.skeletons & skeletons
        if not (e.relatives <= frozenset().union(*(s.relatives for s in run))
                and shared <= frozenset().union(*(s.skeletons for s in run))):
            return False
        return all(e.kinds == s.kinds for s in run) or (not e.kinds and not shared & reached)

    start = 0
    for part in english_parts:
        run = next(((a, b) for a in range(start, len(said_parts)) for b in range(a, len(said_parts))
                    if same(part, said_parts[a : b + 1])), None)
        if run is None:
            found.add("order_changed")
            break
        start = run[1]
    return found


def _spanish_mentions(said_index: list[int], ctx: _Context) -> list[_Mention]:
    position = {index: k for k, index in enumerate(said_index)}
    doses = {position[number.first]: number.readings for number, unit in ctx.measured
             if number.first in position and unit in DOSE_UNITS}
    return spanish_mentions([ctx.toks[i] for i in said_index], _said_gaps(said_index, ctx), doses)


def _said_gaps(said_index: list[int], ctx: _Context) -> list[str]:
    """Between each word of a statement's Spanish and the one before it, what the transcript has, unless only a
    correction and what it took back lie between them; two passages, or the two sides of a cut instruction, are two
    clauses."""
    gaps = []
    for k, index in enumerate(said_index):
        between = range(said_index[k - 1] + 1, index) if k else range(0)
        if k and all(i in ctx.cues or i in ctx.taken_back for i in between):
            gaps.append(" " if between else ctx.text[ctx.toks[said_index[k - 1]].end : ctx.toks[index].start])
        else:
            gaps.append(". " if k else "")
    return gaps


def _spanish_drug(key: str) -> frozenset[str]:
    if not key.isalpha() or len(key) < 4 or key in spanish.NOT_DRUGS:
        return frozenset()
    names = spanish.drug_candidates(key)
    return frozenset(names) if any(is_drug(name) for name in names) else frozenset()


def _english_drug(word: str) -> frozenset[str]:
    return frozenset({word}) if word.isalpha() and is_drug(word) else frozenset()


# ---- One statement --------------------------------------------------------------------------------------------------


@dataclass
class _Statement:
    english: str
    spans: list[tuple[int, int]] = field(default_factory=list)  # the kept pieces, outside instructions
    reasons: set[str] = field(default_factory=set)
    dropped: bool = False  # only an instruction, in English or in the Spanish it cites
    warnings: list[tuple[str, int, int]] = field(default_factory=list)
    listed: list[tuple[int, int, str]] = field(default_factory=list)  # its Spanish, if the backstop cut its English


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
    cut = validation.directives(english)
    # No English transcript reads the English in context, so nothing it finds is kept as said outside an instruction:
    # every instruction found is cut, and what it cites listed.
    english, _ = validation._without_instructions(english, "", [])
    result.english = english
    if cut:
        # The English was, or held, an instruction: what it cites is listed, whatever else the statement says.
        kind = "allergy_change" if any(kind == "allergy_change" for _, _, kind in cut) else "order"
        for quote in proposed.get("spanish", []):
            found = _locate(spanish.tokens(validation.clean(str(quote))), ctx, earlier, cited)
            if not isinstance(found, str):
                result.listed.append((found[0], found[1], kind))
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
    # A fraction with no exact decimal ("un tercio", "a third") can't be compared: refused, for the clinician to write.
    if any(term.kind == "number" and not term.values for term in terms) or UNREADABLE in english_fractions(english):
        reasons.add("number_unreadable")
    said_nums = said_numbers(said, terms)
    # "Una" read as part of a number ("una tableta y media", 1.5) is no 1 of its own.
    if not any(ctx.toks[i].key in spanish.ARTICLE_NUMBERS
               and not any(t.kind == "number" and t.first <= i <= t.last for t in terms) for i in said_index):
        said_nums = {value for term in terms if term.kind == "number" for value in term.values}
    missing = english_nums - said_nums
    if missing & values("number", back):
        reasons.add("source_superseded")
    elif missing:
        reasons.add("number_not_in_sources")
    if any(term.kind == "number" and term.ambiguous for term in terms):
        reasons.add(AMBIGUOUS_NUMBER)

    spanish_units = {(reading, unit) for number, unit in ctx.measured if number.first in said_index
                     for reading in number.readings}
    with_a_unit = {reading for reading, _ in spanish_units}
    for reading, unit in english_units(english):
        if (reading, unit) in spanish_units or reading in missing:  # a number not said is the number check's
            continue
        if unit in SAID_UNITS or reading in with_a_unit:
            reasons.add("unit_not_in_sources")

    position = {index: k for k, index in enumerate(said_index)}
    spoken_periods = spanish_periods(said, terms, position)
    if any(number in said_nums and not any((number, period) in read for read in spoken_periods)
           for number, period in english_periods(english)):
        reasons.add("period_not_in_sources")

    if not english_frequencies(english) <= spanish.frequencies(said_text) or unread_frequencies(english):
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

    if not set(english_sides(english)) <= values("side", terms):
        reasons.add("side_not_in_sources")
    said_forms = {name for t in said for name in spanish.DOSE_FORM_WORDS.get(t.key, ())}
    if not english_dose_forms(english) <= said_forms:
        reasons.add("dose_form_not_in_sources")
    if not english_orderables(english) <= {name for t in said for name in SPANISH_ORDERABLES.get(t.key, ())}:
        reasons.add("orderable_not_in_sources")
    if not english_routes(english) <= spanish.routes(said_text):
        reasons.add("route_not_in_sources")
    allowed = {word for t in said for word in spanish.SEVERITY.get(t.key, ())}
    if not {w for w in english_words if w in ENGLISH_SEVERITY} <= allowed:
        reasons.add("severity_not_in_sources")
    if not english_abbreviations(english) <= spanish.abbreviations(said_text):
        reasons.add("abbreviation_not_in_sources")
    if not english_classes(english) <= {SPANISH_CLASSES[t.key] for t in said if t.key in SPANISH_CLASSES}:
        reasons.add("drug_not_in_sources")
    if _actions_changed(english_mentions(english), _spanish_mentions(said_index, ctx)):
        reasons.add("action_changed")
    # Failing closed where what a change is said of can't be read: a change the English says, of anything, that goes
    # the other way from one the Spanish says ("Suspender la sonda" is not "Keep the catheter"). Not any change the
    # Spanish doesn't say: everyday verbs say changes too ("subir las escaleras", "stop to rest").
    said_kinds = spanish_change_kinds(said)
    if any(OPPOSITES[kind] & said_kinds for kind in english_change_kinds(english) - said_kinds):
        reasons.add("action_changed")

    in_english = _english_qualified(english)
    for kind in ("negation", "certainty", "history", "experiencer"):
        in_spanish = bool(ctx.qualified[kind] & set(inside))
        if in_spanish and not in_english[kind]:
            reasons.add(f"{kind}_dropped")
        elif in_english[kind] and not in_spanish:
            reasons.add(f"{kind}_added")
    # Had, written as having it now: "Tuvo un infarto" as "Is having an MI", "MI now".
    if (any(t.text in spanish.HAD for t in said) and not any(t.key in spanish.NOW for t in said)
            and HAVING_NOW.search(english)):
        reasons.add("history_dropped")
    english_relatives = {ENGLISH_RELATIVES[w] for w in english_words if w in ENGLISH_RELATIVES}
    spanish_relatives = spanish.relatives(said)
    if not english_relatives <= {who for whos in spanish_relatives for who in whos} or any(
        not whos & english_relatives for whos in spanish_relatives
    ):
        reasons.add("experiencer_changed")

    reasons |= _in_order(english, said_index, terms, ctx, set(reasons))

    written, spoken = _polarities(english, said)
    # "Uncontrolled", "negative": a negation, which the negation check compares.
    negated = {"control", "result"} if reasons & {"negation_added", "negation_dropped"} else set()
    if any(said_as.split(":")[0] not in negated for said_as in written - spoken):
        reasons.add("polarity_changed")
    if _findings(english, said):
        reasons.add("finding_not_in_sources")

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
    listed = [span for name in FIELDS for result in results[name].values() for span in result.listed]
    return Translations(checked, list(unknown), listed)
