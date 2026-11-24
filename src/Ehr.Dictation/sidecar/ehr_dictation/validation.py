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
a field is: each sentence of a value holding one is cut where the instruction starts, with all of the sentence after
it, and a source quoting one counts only for its words outside it, the instruction reaching to the end of its clause
however long it is. Separately, not_carried_over() lists every such instruction in the transcript, with its
kind and where the clinician said it, so the review screen can say what still has to be placed in Orders or the
allergy list.

Some of those the dictation proposes (the expansion plan's step 15; FR-14 as reworded): an instruction to place an
order, or to add an allergy, said in a closed sentence, which the clinician may open in the order form or the allergy
form, which fill in nothing from it. proposals() is the one place they are made; a session that must propose none
doesn't ask (sessions.py). An instruction is proposed only when every word of its sentence is accounted for (closed()):
each falls, in this order, into a lead-in (okay, ok, alright, so, and, also, then, now, next, finally, lastly, plan,
"plan:", please, "go ahead and", I'll, I will, I'm going to, I am going to, we'll, we will, we're going to, we are
going to, let's, let us, I'd like to, I want to, we need to, will; each maybe followed by a comma); the verb in one of
its plain forms (order, refill, prescribe, renew, e-prescribe, place an order for, put in an order for, write a
prescription or script for, call in or send ... to her pharmacy; ordering, refilling and the like only after I'm, I am,
we're or we are; for an allergy, add or put ... to or on her allergies or allergy list, add ... as an allergy, list
... as an allergy, add, record, note, document or enter an allergy to ..., or a ... allergy, or "... should be added to
her allergies"); determiners and possessives (a, an, the, her, his, their, some, another, "a new"), and before an
order's item fasting, stat, urgent, routine or repeat; an item, an orderable for an order (a medication for refill,
prescribe, renew, e-prescribe, a prescription or script, call in and send: "Refill a CBC" isn't plain) and an allergen
for an allergy to add, as the reference data names it, or a common name mapped to it there
(db/reference/dictation-names.toml: "penicillin", "latex", "CRP", "A1c"; a word written as letters and digits, "A1c" or
"B12", is one word here, ONE_WORD, and nowhere else) (is_item(): a run of one name of catalog.py that
begins with one of its standalone runs whole, a generic head, an abbreviation in parentheses, a brand in brackets, a
common catalog item's name or a common name, and never holds a negation or a number word, nor a number but between two of its words, so "lisinopril"
and "chest x-ray" are items and "no", "one", "morning", "chest" alone or "CBC 2019" aren't); the item's quantities
(numbers in doses, frequencies, durations, supply or refills, never a zero; forms, routes: QUANTITY); and trailers
(today, now, stat, please, too, as well, also), with "and" or a comma joining another item the verb takes. Any other word anywhere in the sentence, or any other mark (a semicolon, a dash,
a bracket, a quote), makes it not plain, and no list of what must not be said decides it. So an instruction naming
something the reference data doesn't know (a new drug, a misheard name, an unusual test, a test by a name the catalog
doesn't hold) is never proposed, only listed: the clinician still sees it, and the dictation proposes only what it has
read whole. The sentences next to it are read for what may take it back (see "The sentences next to a plain
instruction" below). Those after it are read past line breaks and heading lines, up to the heading of another of the
note's own sections than Plan ("Assessment:"), within TAKEN_BACK_REACH words, up to one holding an instruction of its own, and each of them
is held to the same rules, the first and every later one. One of BARE_SENTENCE words or fewer, or opening with a clause
that short before a comma, holds it back unless that is accounted for, as the closed sentence's words are: a step of
the plan said with its verb and what follows it ("Recheck in one week."), a heading with what follows it accounted for
too, lead-ins alone, or thanks. So a short word of retraction or doubt no list holds ("Nah.", "Oops.", "Kidding.",
"Questionable.", "Hang on, let me check her pressure first.") holds it back; a longer sentence whose words of refusal,
doubt or reassignment no list holds ("She turned down the blood test.") does not, which no list can close. A negation, a
word of error or a condition anywhere in one of them holds it back too, whatever it opens with ("She is not willing to
have blood drawn today.", "Recommend she not have any blood drawn today.", "Patient prefers to wait until after the
holidays."). The rest is read for cues, where a list still decides: a short sentence after it that doubts, retracts or
points back at it, or one that points back or names its item again (by another of its names too: "blood count" for a
CBC) with a negation, a doubt or a word of removing it, or, for an allergy, one that denies it; the next one when it
opens with "no", "not", "or", "instead", "actually", "hold" or "wait", points back in its first three words, or is only
a condition, a time for later or says it was done already, ordered or sent; and a later one that retracts it or opens
like a retraction ("Order a CBC. She is anxious about needles. Actually, let's not."). Before it: a bare negation or
doubt, a bare sentence naming someone else ("Mom insists."), for an allergy one about someone else's allergy, reaction
or its item ("Her sister reacted to penicillin."), a condition said as a sentence of its own, a heading line but Plan,
Orders or Labs, a report or list of what not to do (quoting, per, according to, says, a request, the following, avoid,
not to do), a retraction right before it ("Hold off on these for now."), and a word of refusing, removing or putting
off anywhere before it in its paragraph, within TAKEN_BACK_REACH words, unless said of what came before it ("Not to do
today. She is frail. Order a CBC.", "Things she refused.", "what we're not doing"; not "Skip that."). A proposal can
still be wrong, and is the clinician's to dismiss.
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
needed; changed my mind: from its start to the end of the next sentence, read past line breaks and heading lines,
within TAKEN_BACK_REACH words after it); in_the_past (a word of the past in its clause, as for the pharmacy: already, last week, ago); unclear (it
overlaps a passage marked unclear); question (its sentence ends in a question mark); quoted (the clause before it ends
in a word of telling: "she asked, can you order an MRI"); not_imperative (anything but an opener, from the start of
its clause or the last "and" or word starting a clause, up to its lead-in or subject: "capillary refill under 2
seconds"); names_no_item (no run of its words is an item the reference data names: "call that in", "send the
prescription", "send it", or "add shellfish", which neither the reference data nor a common name names); not_plain
(not said in the closed sentence, or a sentence next to it holds it back, as above); too_many (past
MAX_PROPOSALS). A proposal is its kind (order or allergy_addition), the place of its listed
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
  is being placed now ("I'm ordering"), to write, send, refill, renew or sort out a prescription, to have or get one
  refilled ("have her lisinopril refilled", not "it was refilled"), to call in anything ("call in something for her
  nausea", not "call in sick"), to send something to the pharmacy (anywhere on in the sentence: "send amoxicillin 500
  mg by mouth three times a day for 7 days to her pharmacy"), to make an orders section, or to change the allergy list
  (add, remove, record, clear, update; or note, record, document or flag that the patient is allergic or reacts to
  something: "note that she's allergic to penicillin", not "she is allergic to penicillin"), whoever's list it is
  called ("her", "his", "their", "my", "your": "I'll add penicillin to your allergies", as said to the patient);
  hyphens are ignored ("re-fill"). A word
  before the verb makes it a noun only right before it ("no order", "the lab order"): after a comma or a full stop the
  verb starts an instruction ("No, order a chest X-ray", "Check the lab. Order a CBC"). A plan stays a plan:
  "start", "continue", "increase" or "stop" a drug, orders in the past or by someone else ("her cardiologist ordered an
  echo last year"), and an instruction not to ("don't order an A1c", "no need to order labs"), pass. A piece of the
  sentence that says nothing once the instruction is cut is listed with it ("Do not start metformin until we order an
  A1c", "We discussed ordering a sleep study", "No, order a CBC"), and a condition before it is kept with what follows
  it ("If her ferritin is low, order an iron panel and continue omeprazole" keeps "If her ferritin is low, continue
  omeprazole."); what is cut from a value is only what the transcript lists, so a sentence the transcript reads in
  context as no instruction stays ("Any problems with refills? No. Refill history is regular.").
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
  Monday" is the patient's rash); no cue reaches past a line break, a spoken new paragraph, forwards or back. A
  negation of something named carries on through a list after a comma, up to LIST_SCOPE words, to the first word that
  ends it: a full stop, a semicolon, a word that stops a cue ("but"), a step of the plan, or a word saying something is
  there or someone told it after "and" or "or" ("denies fever, chills or night sweats but reports weight loss"); never
  into a clause of its own after the comma ("no fever, the cough is better"). A negation or a doubt passes over a subject and its verb instead of stopping there
  (TO_THE_FINDING), and over a "that", "if" or "whether", "think" or "believe", and a clause between commas: "I don't
  think, given her normal oxygen, that she has pneumonia", "no evidence she has a fracture", "I think her knee pain is
  from arthritis", "it doesn't look like she has cellulitis". The subject is a pronoun (you included), "this", "there",
  "the patient" or a possessive and up to three words; the verb a form of "have" or "be", negated or not, or "does",
  "did" or "do" with "have". "I don't think", "doubt" and "unlikely" are doubt as well as negation, so "She doesn't
  have asthma." from "I don't think she doesn't have asthma" is refused. Uncertainty is also: "maybe", "perhaps",
  "presumably", "think", "believe", "suspect", "wonder", "not sure", "not certain", "not convinced", "either", "a
  chance", "concern that", "worried", "can't exclude" or "rule out"; she, he, it, they, you, this or the patient with
  "might", "may" or "could" and "have" or "be" ("she may have ibuprofen as needed" is read as doubt too, a known false
  refusal); doubt said after the finding, closing its clause with a comma or without ("she has gout, I think",
  "pneumonia probably", "..., most likely", "..., I guess"), or an alternative offered ("she has pneumonia, or maybe
  bronchitis"), which reaches back over all of its sentence to a word that ends a reach; and a question, a sentence
  that ends with a question mark or opens with a verb and its subject ("does she have", "is this") or "any". A number a
  doubt reaches counts ("her temperature was maybe 38"). A source said both hedged and plainly is read where it was
  said plainly ("I think she has a viral infection. Assessment: viral infection.").
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
import functools
import re
import string
import unicodedata
from dataclasses import dataclass, field
from typing import NamedTuple

from .prompt import FIELDS
from .textnorm import ORDINAL_TENS, ORDINAL_UNITS, STOPWORDS, TENS, UNITS, WORD, Token, numbers, stem, tokens
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
LIST_SCOPE = 30  # words after a negation that a list it carries through may take, past its commas
POST_SCOPE = 4  # words before a cue that qualifies what precedes it ("chest pain denied")
UNDOES_REACH = 3  # words before an instruction not to within which another negation makes it an instruction again
DIRECTIVE_REACH = 14  # at most, words an instruction reaches past its verb when nothing ends it sooner...
MARKED_REACH = 30  # ...unless a full stop, question mark, semicolon or line break ends it within this many
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
    value, _ = _without_instructions(value, transcript.text, transcript.directives)
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
    where it was said (FR-08). Only a plain restatement under a heading is preferred to it: when that occurrence is
    reached by a qualifier and a later one is the whole of its clause after a heading and no qualifier reaches it ("I
    think she has a viral infection. Assessment: viral infection."). Never someone else's sentence that happens to say
    the same words ("I doubt she has shingles. The patient in the next bed has shingles.")."""
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
    ordered = [span for span in unused if span[0] >= after] + [span for span in unused if span[0] < after] or clear
    qualified = {i for reached in transcript.qualified.values() for i in reached}
    plain = lambda span: not any(span[0] <= words[i].start and words[i].end <= span[1] for i in qualified)  # noqa: E731
    if plain(ordered[0]):
        return ordered[0]
    return next((span for span in ordered[1:] if plain(span) and _under_a_heading(span, transcript.text)), ordered[0])


HEADING_BEFORE = re.compile(r"\s*[A-Za-z][A-Za-z /&-]{0,40}:\s*$")


def _under_a_heading(span: tuple[int, int], text: str) -> bool:
    """Whether a passage is the whole of its clause after a heading: "Assessment: viral infection."."""
    start = max((mark.end() for mark in SENTENCE_MARK.finditer(text, 0, span[0])), default=0)
    return bool(HEADING_BEFORE.fullmatch(text, start, span[0])) and not text[span[1] : _sentence_end(text, span[1])].strip(
        " ,;:"
    )


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
# Sent to the pharmacy, said however far on in its clause (not past what ends its sentence, as SENTENCE_MARK reads it:
# "2.5 mg" doesn't; nor past a comma or a new subject, "she went straight to the pharmacy"): "send amoxicillin 500 mg by
# mouth three times a day for 7 days to her pharmacy". Within a few words, the pharmacy is enough without "to".
_NO_NEW_SUBJECT = r"(?!\b(?:she|he|they|i|we|you)\b)"
_TO_THE_PHARMACY = (
    rf"(?:{_NO_NEW_SUBJECT}[^.,;!?\n]|(?<=\d)\.(?=\d))*?\b(?:to|into)\s+(?:{_DET}\s+)?(?:\w+\s+){{0,2}}pharmacy\b"
)
# Within a few words, as before, but not past a comma or a new subject either.
_NEAR_THE_PHARMACY = rf"(?:{_NO_NEW_SUBJECT}[^.,;!?\n]){{0,60}}?\bpharmacy\b"
# Sending the patient somewhere is not an order: "send her home", "send her to the pharmacy for a flu shot".
_NOT_THE_PATIENT = r"(?!\s+(?:her|him|them|me|us|the\s+patient|patient)\s+(?:home|to|back|down|over|away|out|off|up|in)\b)"
# Placing an order, or a prescription, now: the verb as an instruction ("order", "renew") or under way ("ordering").
_ORDER_VERB = re.compile(
    r"\b(?:order|ordering|e-?prescribe|prescribe|prescribing|refill|refilling|renew|renewing|e-?rx)\b", re.IGNORECASE
)
# Words that make the verb a noun or an adjective ("the order", "a refill", "the ordering physician", "capillary refill
# under 2 seconds"), or "in order to", when nothing but a space comes between: "No, order a chest X-ray" and "...the
# lab. Order a CBC" are instructions.
_NOUN_BEFORE = frozenset(
    "a an the this that her his their its my our your no any standing verbal written phone telephone in of lab capillary"
    .split()
)
# After such a word and a comma or a full stop, the verb is still a noun when one of these follows it: "No, refill
# history shows she picks up every month".
_COMPOUND_AFTER = frozenset("history number entry request requests status".split())
_ORDERS = [
    re.compile(
        rf"\b(?:place|put in|putting in|enter|send in|send over|send|call in|phone in|fax|write|writing|give|giving)\s+"
        rf"(?:(?:{_DET}|him|them|me|us|you)\s+)?(?:{_DET}\s+)?(?:\w+\s+)?(?:order|prescription|script|rx|refill)s?\b",
        re.IGNORECASE,
    ),
    re.compile(rf"\b(?:put|putting|get|getting)\s+(?:{_DET}\s+)?(?:\w+\s+)?orders?\s+in\b", re.IGNORECASE),
    # "In to" as speech recognition often writes it: "call that into his usual pharmacy".
    re.compile(r"\b(?:call|phone)\s+(?:it|that|this|them|those|these)\s+in(?:to)?\b", re.IGNORECASE),
    re.compile(rf"\b(?:send|sending)\b{_NOT_THE_PATIENT}(?:{_NEAR_THE_PHARMACY}|{_TO_THE_PHARMACY})", re.IGNORECASE),
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
# - "note" or "record" an allergy, or note, record, document or flag (or make a note) that the patient is allergic or
#   reacts to something, as an instruction: at the start of its clause, or after asking for it ("we need to record",
#   "make sure to note"); not after a subject that does the noting ("her records note a sulfa allergy", "I note a
#   penicillin allergy in her chart" are documentation), and never the statement alone ("she is allergic to
#   penicillin").
_CALL_IN = re.compile(r"\b(?:call|calling|phone|phoning)\s+in\b", re.IGNORECASE)
_NOT_AN_ORDER = re.compile(
    r"\s+(?:(?:today|tomorrow|later)\s+)?(?:sick|about|yesterday|to say|for an? appointment)\b",
    re.IGNORECASE,
)
_SOMEONE_ELSE_DID = re.compile(r"\b(?:did|was|were|had been)\s+$", re.IGNORECASE)
_SPEAKER = frozenset("i we you i'll we'll i'm we're i've we've you'll let's".split())
# "Note" or "record" an allergy, not "note that" (a conjunction, not a determiner: "note that her allergic rhinitis...").
_NOTE_ALLERGY = re.compile(rf"\b(?:record|note)\s+(?!that\b)(?:{_DET}\s+)?(?:new\s+)?(?:\w+\s+)?allerg", re.IGNORECASE)
# Noting that the patient is allergic or reacts to something, with nothing after it but the reaction: "note that she's
# allergic to penicillin", "record that she reacts to sulfa with hives". Not her allergic rhinitis, no known allergies,
# a negation ("she is not allergic"), or a sentence that goes on ("..., so we avoided amoxicillin").
# A word that would say more than the substance and its reaction: a joined clause, or when.
_NOT_MORE = (
    r"(?!(?:and|but|or|so|which|because|since|while|when|before|after|during|last|ago|yesterday|previously|years?|months?|"
    r"weeks?|days?)\b)"
)
_NOTED_ALLERGIC = re.compile(
    r"\b(?:note|record|document|flag|make\s+a\s+note)\s+that\s+(?:she|he|they|the\s+patient)"
    r"(?:['’]s\s+allergic|\s+(?:is|are|was|were)\s+allergic|\s+(?:reacts|reacted|(?:has\s+)?had\s+a\s+reaction))\s+to"
    rf"(?:\s+{_NOT_MORE}[\w'’-]+){{1,4}}?(?:\s+with(?:\s+{_NOT_MORE}[\w'’-]+){{1,4}})?\s*(?=[.;!?\n]|$)",
    re.IGNORECASE,
)
# Having it refilled, not that it was or someone is trying to: "have her lisinopril refilled", "get that refilled"; and
# sorting out a refill or a prescription, not that the pharmacy will.
_REFILLED = re.compile(rf"\b(?:have|get)\s+(?:(?:{_DET}|him|them|it)\s+)?(?:\w+\s+){{0,3}}?refilled\b", re.I)
_SORT_OUT = re.compile(
    rf"\bsort(?:ing)? out\s+(?:{_DET}\s+)?(?:\w+\s+)?(?:order|prescription|script|rx|refill)s?\b", re.IGNORECASE
)
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
    ("order", _REFILLED, _at_clause_start),
    ("order", _SORT_OUT, _at_clause_start),
    (ALLERGY_ADDITION, _NOTE_ALLERGY, _at_clause_start),
    (ALLERGY_ADDITION, _NOTED_ALLERGIC, _at_clause_start),
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
    ("order", re.compile(rf"\bsent\b{_NOT_THE_PATIENT}(?:{_NEAR_THE_PHARMACY}|{_TO_THE_PHARMACY})", re.IGNORECASE)),
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
    allergy change winning, and each with any piece of its sentence that says nothing once it is cut
    (_with_fragments()). Hyphens are ignored ("re-fill")."""
    return [(start, end, reason) for start, end, reason, _, _, _ in _instructions(text)]


Instruction = tuple[int, int, str, frozenset[str], int, int]


def _instructions(text: str) -> list[Instruction]:
    """directives(), each with the finer kinds of the instructions merged into it (REASON's keys), where the first of
    them was matched, which is after its lead-in or subject, and where it starts without the pieces of its sentence
    listed with it (_with_fragments()), which is where its rules read it from (weigh())."""
    found = _directives(text)
    letters = [i for i, character in enumerate(text) if character != "-"]
    if len(letters) < len(text):
        plain = "".join(text[i] for i in letters)
        found += [(letters[start], letters[end - 1] + 1, kind, letters[at]) for start, end, kind, at in _directives(plain)]
    merged: list[Instruction] = []
    for start, end, kind, at in sorted(found):
        if merged and start < merged[-1][1]:
            merged[-1] = _joined(merged[-1], (start, end, REASON[kind], frozenset({kind}), at, start))
        else:
            merged.append((start, end, REASON[kind], frozenset({kind}), at, start))
    return _with_fragments(text, merged)


def _joined(first: Instruction, second: Instruction) -> Instruction:
    """Two instructions listed as one, an allergy change winning."""
    reason = "allergy_change" if "allergy_change" in (first[2], second[2]) else first[2]
    return (min(first[0], second[0]), max(first[1], second[1]), reason, first[3] | second[3], min(first[4], second[4]),
            min(first[5], second[5]))


def _with_fragments(text: str, found: list[Instruction]) -> list[Instruction]:
    """The instructions, each listed with any piece of its sentence beside it that says nothing once it is cut
    (_fragment()), so what is cut is still what is listed: "Do not start metformin until we order an A1c", "We
    discussed ordering a sleep study", "Order a CBC, no". A condition before an instruction goes with it only when
    nothing else follows in its sentence ("If her hemoglobin drops below 7, order a transfusion"); otherwise it stays
    with what follows (_without_instructions())."""
    listed: list[Instruction] = []
    for instruction in found:
        opening = max((mark.end() for mark in PIECE_MARK.finditer(text, 0, instruction[0])), default=0)
        if listed and listed[-1][1] > opening and _fragment(text[listed[-1][1] : instruction[0]]):
            instruction = _joined(listed.pop(), instruction)
        listed.append(instruction)
    for k, (start, end, reason, kinds, at, own) in enumerate(listed):
        opening = max((mark.end() for mark in PIECE_MARK.finditer(text, 0, start)), default=0)
        closing = next((mark.start() for mark in PIECE_MARK.finditer(text, end)), len(text))
        if k + 1 < len(listed) and listed[k + 1][0] < closing:
            closing = None  # another instruction follows in the sentence
        after = text[end:closing] if closing is not None else ""
        if closing is not None and _fragment(after):
            end, after = closing - (len(after) - len(after.rstrip())), ""
        if not (k and listed[k - 1][1] > opening):
            before = text[opening:start]
            if _fragment(before) or (_condition(before) and closing is not None and not _says_something(after)):
                start = opening + len(before) - len(before.lstrip())
        listed[k] = (start, end, reason, kinds, at, own)
    return listed


def _fragment(piece: str) -> bool:
    """Whether a piece of a sentence beside an instruction says nothing of its own once the instruction is cut: it ends
    in a subordinator or "but" ("Do not start metformin until", "Stop warfarin only after"), its last negation is bare
    ("No", "I don't think"), or it is under three words and doesn't start a step of the plan ("We discussed", not "Avoid
    NSAIDs"). A heading is none of these, nor a piece that holds nothing but stopwords, lead-ins or the words a step
    of the plan opens with ("Okay so", "Next"), nor one over FRAGMENT_REACH words, which says something of its own
    (and is all a transcript without punctuation has before an instruction)."""
    if piece.rstrip().endswith(":"):
        return False
    words = [token.text for token in tokens(LEADING_CONNECTOR.sub("", TRAILING_CONNECTOR.sub("", piece)))]
    if (
        not _says_something(" ".join(words)) or len(words) > FRAGMENT_REACH or words[-1] in HEADINGS
        or all(word in OPENERS for word in words)
    ):
        return False
    negations = [i for i, word in enumerate(words) if ANY_NEGATION.search(word)]
    return (
        words[-1] in DANGLING
        or bool(negations) and all(
            word in STOPWORDS or word in LEAD_IN or word in BARE_AFTER_NEGATION for word in words[negations[-1] + 1 :]
        )
        or (len(words) < 3 and words[0] not in STEP_VERBS)
    )


def _condition(piece: str) -> bool:
    """Whether a piece of a sentence opens with a condition: "If her ferritin is low"."""
    words = [token.text for token in tokens(piece)]
    return bool(words) and words[0] in CONDITION_WORDS


def _directives(text: str) -> list[tuple[int, int, str, int]]:
    words = tokens(text)
    matches: list[tuple[int, int, str]] = []
    for match in _ORDER_VERB.finditer(text):
        before = [token for token in words if token.end <= match.start()][-1:]
        after = text[match.end() : match.end() + 4].lower()
        following = next((token.text for token in words if token.start >= match.end()), "")
        if (
            before and before[0].text in _NOUN_BEFORE
            and (not text[before[0].end : match.start()].strip() or following in _COMPOUND_AFTER)
        ) or after.startswith(" of"):
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
        negated, base = NEGATED_INSTRUCTION.search(text[clause:start]), clause
        aside = None if negated else ASIDE_BEFORE.search(text, 0, clause)
        if aside:  # read over an aside between commas: "do not, under any circumstances, order a CT"
            base = max((mark.end() for mark in CLAUSE_MARK.finditer(text, 0, aside.start())), default=0)
            negated = NEGATED_INSTRUCTION.search(f"{text[base : aside.start()]} {text[clause:start]}")
            negated = negated if negated and negated.group().split()[0].lower() != "no" else None
        if not negated and NEVER_BEFORE.search(text, 0, clause) and not text[clause:start].strip():
            continue  # "under no circumstances, order a CT"
        before = text[base : base + negated.start()] if negated else ""
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
        # Asking for it is part of it, so it is listed and cut together: "we need to record the new codeine allergy".
        asking = None if subject else re.search(rf"(?:{_ASKING.pattern})\s*$", text[clause:span_start], re.IGNORECASE)
        if asking:
            span_start = clause + asking.start()
            for token in reversed([token for token in words if clause <= token.start and token.end <= span_start]):
                if token.text not in LEAD_IN:
                    break
                span_start = token.start
        spans.append((span_start, _directive_end(text, words, end), kind, start))
    return spans


# An instruction not to: "don't order an A1c", "no need to order labs", "hold off on ordering an MRI", "sulfa should not
# be added", "do not, under any circumstances, order a CT", "under no circumstances, order a CT". It is not an instruction, so nothing is cut or reported; the negation rule keeps it in the value. Unless
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
# An aside between commas just before an instruction's clause, which a negation before it is read over (not a bare
# "no", which answers something: "no, wait, order a CBC").
ASIDE_BEFORE = re.compile(r",[^,.;:!?\n]{1,80},\s*$")
# A clause of its own before it that says never: "under no circumstances, order a CT".
NEVER_BEFORE = re.compile(r"\b(?:under|in|on|by)\s+no\s+(?:circumstances|case|account|means)\s*,\s*$", re.IGNORECASE)
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
CARRIES_ON = frozenset("to for of from include includes including with as that".split())
# A condition said in an instruction is part of it, subject and all: "order an MRI if he gets worse".
CONDITION_WORDS = frozenset("if unless when whenever once until till".split())
TARGET = re.compile(r"(?:allerg\w*|list|record|pharmacy)$", re.IGNORECASE)
# What can follow "allergy" and still name the list: "allergy list", "allergy record".
LIST_WORDS = frozenset("list record".split())
# A note's headings, which end an instruction said without punctuation before them.
HEADINGS = frozenset("assessment plan impression exam examination observations vitals subjective objective".split())
# Where a piece of a sentence beside an instruction ends: the sentence's end or a semicolon.
PIECE_MARK = re.compile(r"[!?;\n]|\.(?!\d)|(?<!\d)\.")
# Words a piece can't end in and say something of its own: "until", "only after", "but".
DANGLING = frozenset("after before until till unless if when whenever once because since while whether that than but".split())
# Words after a negation that leave it bare: "I don't think", "not sure".
BARE_AFTER_NEGATION = frozenset("think believe sure know want".split())
# The words a step of the plan starts with, which make two words a sentence: "Avoid NSAIDs".
STEP_VERBS = NEW_CLAUSE - frozenset("i we she he they you patient let's i'll we'll i'm we're she's he's".split())


def _directive_end(text: str, words: list[Token], end: int) -> int:
    """Where an instruction matched up to `end` stops: at the end of its sentence or a semicolon; at a word that starts
    another clause, a new subject or step of the plan (NEXT_CLAUSE), or a heading (HEADINGS); so a long one is cut and
    listed whole when punctuation ends it ("Order a CBC and a BMP and a lipid panel and a TSH and a urinalysis and a
    chest x-ray."). It reaches at most DIRECTIVE_REACH words past its verb, unless its sentence's punctuation ends it
    within MARKED_REACH: speech recognition that wrote no punctuation never makes a paragraph one instruction. Where the
    reach cuts it, it goes on while it lists more of what is ordered (a determiner and up to three words after it, "and",
    a number, a quantity or a word of a reference name: "... a urinalysis a chest x ray and an ecg"), so no tail of an
    order is left in the note.
    One that already names the allergy list or the pharmacy it goes to stops there ("take amoxicillin off her allergy
    list"), unless a word carries it on."""
    stop = min(_sentence_end(text, end), (text.find(";", end) + 1 or len(text) + 1) - 1)
    end += len(re.match(r"[\w'’]*", text[end:]).group())  # the whole of the word the match ended in ("allergies")
    following = [word for word in words if word.start >= end and word.end <= stop]
    if following and following[0].text in LIST_WORDS:
        end, following = following[0].end, following[1:]
    if TARGET.search(text[:end]) and not (following and following[0].text in CARRIES_ON):
        return end
    beyond: list[Token] = []
    if not (stop < len(text) and len(following) <= MARKED_REACH):
        following, beyond = following[:DIRECTIVE_REACH], following[DIRECTIVE_REACH:]
    joined = conditional = False
    for word in following:
        if word.text in CLAUSE_STARTS or word.text in HEADINGS or (not conditional and (
            (word.text in NEXT_CLAUSE and (joined or word is not following[0]))
            or (word.text in NEW_CLAUSE and (joined or "," in text[end : word.start]))
        )):
            return end
        conditional = conditional or word.text in CONDITION_WORDS
        joined = word.text == "and"
        if not joined:
            end = word.end
    if not beyond:
        return end
    # From the last article or "and" before the cut, so an item the cut fell inside is read whole ("a vitamin | D").
    back = max((i for i, word in enumerate(following) if word.text in ARTICLES or word.text == "and"), default=len(following))
    return max(end, _listing_end(text, following[back:] + beyond, following[back - 1].end if back else end))


ARTICLES = frozenset("a an the some another".split())


def _listing_end(text: str, words: list[Token], end: int) -> int:
    """Where a list of what is ordered, cut by the reach, ends: it goes on over items (is_item()), articles and up to
    three words after one that aren't stopwords ("an ecg", "a vitamin d level"), "and", numbers and words of an order's
    quantities, and stops at the first other word, or a word after a comma that is none of these."""
    i, after_article, last = 0, None, end
    while i < len(words):
        word = words[i].text
        if "," in text[last : words[i].start]:
            after_article = None
        size = next((k for k in range(min(MAX_ITEM_WORDS, len(words) - i), 0, -1)
                     if is_item([w.text for w in words[i : i + k]], "order")), 0)
        if size:
            after_article = None if after_article is None else after_article + size
            end = last = words[i + size - 1].end
            i += size
            continue
        if word in ARTICLES:
            after_article = 0
        elif word == "and":
            after_article = None
        elif word[0].isdigit() or word in _NUMBER_WORDS or word in _QUANTITY_WORDS:
            after_article = None if after_article is None else after_article + 1
        elif (after_article is not None and after_article < 3 and word not in STOPWORDS
              and word not in NEXT_CLAUSE | CLAUSE_STARTS | HEADINGS):
            after_article += 1
        else:
            break
        if word != "and":
            end = words[i].end
        last = words[i].end
        i += 1
    return end


SENTENCE = re.compile(r"(?<=[.!?;\n])\s+")
TRAILING_CONNECTOR = re.compile(r"(?:[\s,;:-]|\b(?:and|or|then|also|so)\b)+$", re.IGNORECASE)
LEADING_CONNECTOR = re.compile(r"^(?:[\s,;:-]|\b(?:and|or|then|also|so)\b)+", re.IGNORECASE)


def _without_instructions(value: str, text: str, listed: list[tuple[int, int]]) -> tuple[str, int]:
    """The value with exactly its instructions cut out, each as not_carried_over() lists it (directives()): what is cut
    is what is listed, never the rest of its sentence ("Order a CBC and recheck in 3 months." keeps "Recheck in 3
    months."). One the transcript `text` says word for word, but only outside what it lists (`listed`), stays: the
    transcript reads it in context as no instruction ("Any problems with refills? No. Refill history is regular."). A
    joining word or comma left at a cut ("and", "then") goes with it, and a sentence left with no word that says
    anything (only stopwords and lead-ins) goes too. Each side of a cut is a sentence of its own, as its source is, but
    a condition before it stays with what follows ("If her ferritin is low, order an iron panel and continue
    omeprazole." keeps "If her ferritin is low, continue omeprazole."). Once something is cut, what starts the value or
    follows a full stop starts with a capital ("Order a CBC; if it is low, start iron." keeps "If it is low, start
    iron."), and what follows a semicolon keeps its case ("Continue apixaban; do not stop it."). Also how many were
    cut."""
    said = tokens(text)
    kept: list[str] = []
    cut = 0
    for sentence in SENTENCE.split(value):
        found = [
            (start, end, kind) for start, end, kind in directives(sentence)
            if not _said_outside(tokens(sentence[start:end]), said, listed)
        ]
        if not found:
            kept.append(sentence)
            continue
        cut += len(found)
        bounds = [0, *(edge for start, end, _ in found for edge in (start, end)), len(sentence)]
        pieces = [sentence[bounds[i] : bounds[i + 1]] for i in range(0, len(bounds), 2)]
        rest = ""
        for i, piece in enumerate(pieces):
            piece = TRAILING_CONNECTOR.sub("", piece) if i < len(pieces) - 1 else piece
            piece = (LEADING_CONNECTOR.sub("", piece) if i else piece).strip()
            if piece and rest and piece[0].isalnum():
                if not (_says_something(rest) and _says_something(piece)):
                    rest += " " + piece
                elif _condition(rest):
                    rest += ", " + piece
                else:
                    rest += ". " + piece[0].upper() + piece[1:]
            elif piece:
                rest += piece
        if _says_something(rest):
            kept.append(rest if rest[-1] in ".!?;" else rest + ".")
    if cut:  # what now starts the value or follows a full stop starts with a capital; after a semicolon, as said
        kept = [
            piece[:1].upper() + piece[1:] if not i or kept[i - 1].rstrip()[-1:] in (".", "!", "?") else piece
            for i, piece in enumerate(kept)
        ]
    return " ".join(kept).strip(), cut


def _said_outside(words: list[Token], said: list[Token], listed: list[tuple[int, int]]) -> bool:
    """Whether the words are said in the transcript, word for word, and never inside what it lists."""
    run = [word.text for word in words]
    found = [
        (said[i].start, said[i + len(run) - 1].end)
        for i in range(len(said) - len(run) + 1)
        if run and [word.text for word in said[i : i + len(run)]] == run
    ]
    return bool(found) and not any(start < last and first < end for first, last in found for start, end in listed)


def _says_something(text: str) -> bool:
    """Whether the text holds a word that isn't a stopword or a lead-in."""
    return any(token.text not in LEAD_IN and token.text not in STOPWORDS for token in tokens(text))


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

# A subject with its verb (have, be or do, negated or not), which a negation or a doubt passes over rather than stop at
# it: "she has", "she's", "the patient has", "this is", "her knee pain is", "she doesn't have".
SUBJECT = r"(?:she|he|it|they|you|this|that|there|the\s+patient|(?:her|his|their|the|this|your)(?:\s+[a-z][\w'’-]*){1,3}?)"
_SUBJECT_AND_VERB = (
    rf"\b{SUBJECT}(?:['’](?:s|re|ve)\b|\s+(?:(?:has|have|had|is|are|was|were)(?:n['’]t|\s+not)?|"
    r"(?:does|did|do)(?:n['’]t|\s+not)?\s+have)\b)"
)
# What a negation or a doubt passes over, in this order, before the words it qualifies: "think" or "believe"; "that",
# "if" or "whether"; a clause between commas; "think" or "believe" again; "that", "if" or "whether"; a subject and its
# verb. "I don't think, given her normal oxygen, that she has pneumonia", "we do not, after reviewing the x-ray, believe
# she has pneumonia", "I wonder, since she was camping, if she has Lyme disease", "no evidence she has a fracture".
TO_THE_FINDING = re.compile(
    r"(?:\s*\b(?:think|believe)\b)?(?:\s*\b(?:that|if|whether)\b)?(?:\s*,[^,.;:!?\n]{1,80},)?(?:\s*\b(?:think|believe)\b)?"
    rf"(?:\s*\b(?:that|if|whether)\b)?(?:\s*{_SUBJECT_AND_VERB})?",
    re.IGNORECASE,
)
NEGATION = re.compile(
    r"\b(?:no\s+(?:evidence|signs?)(?:\s+of)?|doubt|doubtful|doesn't\s+look\s+like|does\s+not\s+look\s+like|"
    r"no(?![\s,]+(?:sorry|wait|correction)\b)(?!\s*,)|not|denies|denied|deny|without|never|none|negative for|absence of|"
    r"free of|ruled out|rules out|doesn't|does not|didn't|did not|isn't|is not|wasn't|was not|hasn't|has not|haven't|"
    r"have not|don't|do not|won't|will not|shouldn't|should not|can't|cannot|non)\b",
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
# Not certain: "possible community acquired pneumonia", "maybe pneumonia", "rule out appendicitis", "pneumonia
# suspected", "I think she has gout", "she might have gout", "cannot exclude a PE", "either pneumonia or bronchitis"; and
# a question, whose sentence opens with a verb and its subject ("does she have", "is this") or with "any".
QUESTION_OPENING = r"(?:^|(?<=[.!?\n]))\s*(?:(?:does|do|did|is|are|was|were|has|have|had|can|could|will|would|should)\s+" \
    r"(?:she|he|it|they|there|this|that|you|the\s+patient)|any)\b"
CERTAINTY = re.compile(
    rf"{QUESTION_OPENING}|\b(?:(?:she|he|it|they|you|this|the\s+patient)\s+(?:might|may|could)\s+(?:have|be)|"
    r"maybe|perhaps|possible|possibly|probable|probably|presumably|suspected|suspects?|suspicious for|suspicion of|likely|"
    r"unlikely|think|thinks|believe|believes|wonder|wondering|doubt|doubtful|not\s+sure|not\s+certain|not\s+convinced|"
    r"either|(?:a|some)\s+chance|concern(?:ed)?\s+that|worried|worry|doesn't\s+look\s+like|does\s+not\s+look\s+like|"
    r"(?:can't|cannot)\s+(?:exclude|rule\s+out)|rule out|r/o|risk of|risk for|concern for|concerning for|questionable|"
    r"presumed)\b",
    re.IGNORECASE,
)
POST_CERTAINTY = re.compile(
    r"\b(?:suspected|likely|possible|probable|unlikely|questionable|presumed)\b(?=\s*(?:[.,;:!?\n]|$))", re.IGNORECASE
)
# Doubt said after what it qualifies, which reaches back over all of the sentence before it (commas included, to a
# word that ends a cue's reach): closing a clause, after a comma or not ("she has gout, I think", "pneumonia
# probably", "..., most likely", "gout, pseudogout, septic arthritis, possibly"); an alternative offered ("she has
# pneumonia, or maybe bronchitis"); or a question mark.
CLOSING_HEDGE = re.compile(
    r"(?:,\s*|\s+)(?:(?:i|we)\s+(?:think|believe|suspect|guess|would\s+say)|probably(?:\s+not)?|possibly|maybe|perhaps|"
    r"presumably|(?:most\s+)?(?:un)?likely)(?=\s*(?:[.,;:!?\n]|$))|,?\s+or\s+(?:maybe|possibly|perhaps|probably)\b|\?",
    re.IGNORECASE,
)
# In the past: "a history of MI in 2019", "status post appendectomy".
# Not the heading "history of present illness", said without its colon.
HISTORY = re.compile(r"(?<!family )\b(?:history of(?! present illness)|hx of|h/o|status post|s/p)\b", re.IGNORECASE)
# Where a cue's reach ends, besides punctuation: a contrast, or a new subject ("no fever, she is eating well").
TERMINATORS = frozenset(
    """but however although though except apart aside yet which who whereas he she they i we you patient he's she's
    they're i'm we're i've""".split()
)
# After a comma, words that end a negation's list: something said to be there, how it is going, or someone telling it
# ("denies fever, reports a cough", "no rash, the cough is better", "negative for nausea, positive for headache",
# "without aura, improving", "no orthostatic hypotension, still symptomatic").
LIST_ENDS = frozenset(
    """has have had is are was were with notes noted endorses endorsed reports reported complains complaining admits
    admitted positive says said states stated feels felt improving improved worsening worse better stable unchanged
    resolved resolving well still otherwise""".split()
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
class Qualifier(NamedTuple):
    """One kind of qualifier: its reason code; its cues before the words they qualify, cues after them, and cues closing
    a sentence that reach back over all of it; whether a cue before passes over a subject and its verb
    (TO_THE_FINDING); and the words that end its reach."""

    reason: str
    before: re.Pattern
    after: re.Pattern | None
    closing: re.Pattern | None
    past_the_subject: bool
    ends: frozenset[str]


QUALIFIERS = (
    Qualifier("negation_dropped", NEGATION, POST_NEGATION, None, True, TERMINATORS),
    Qualifier("experiencer_dropped", EXPERIENCER, POST_EXPERIENCER, None, False, TERMINATORS | REPORTING),
    Qualifier("certainty_dropped", CERTAINTY, POST_CERTAINTY, CLOSING_HEDGE, True, TERMINATORS),
    Qualifier("history_dropped", HISTORY, None, None, False, TERMINATORS),
)


def _reach(text: str, words: list[Token], qualifier: Qualifier) -> tuple[set[int], set[int]]:
    """The words the text's cues of one kind reach, and the cues' own words (which are neither reached nor plain:
    "mother" in "mother had a stroke" is who had it; "she has" in "I think she has gout"). No cue reaches past a line
    break, forwards or back, over its subject or a relative clause included: in dictation a line break is a spoken new
    paragraph, which ends a cue's reach as a full stop does ("I don't think {new paragraph} She has pneumonia.")."""
    before, after, closing, ends = qualifier.before, qualifier.after, qualifier.closing, qualifier.ends
    reached: set[int] = set()
    cue_words: set[int] = set()

    def stops(between: str, word: str, marks: str = ".,;:?!\n") -> bool:
        return word in ends or any(mark in between for mark in marks)

    for cue in before.finditer(text):
        # A negation carries through a list after a comma ("denies fever, chills, night sweats"), up to the word that
        # ends it ("... but reports weight loss"), and not into a clause that says something else ("no fever, the cough
        # is better", "no fever, reports a cough").
        listing = before is NEGATION
        relative = WHO_HAS.match(text, cue.end()) if before is EXPERIENCER else None
        relative = None if relative and "\n" in relative.group() else relative
        if before is EXPERIENCER and not relative and _object_of_a_preposition(text, cue.start()):
            continue
        edge = relative.end() if relative else TO_THE_FINDING.match(text, cue.end()).end() if qualifier.past_the_subject else cue.end()
        if "\n" in text[cue.end() : edge]:  # a new paragraph ends a cue's reach, as a full stop does
            edge = cue.end()
        cue_words |= {i for i, token in enumerate(words) if cue.start() <= token.start < edge}
        ahead = [i for i, token in enumerate(words) if token.start >= edge]
        crossed = False  # a comma, into more of the list
        for k, i in enumerate(ahead[: LIST_SCOPE if listing else NEGATION_SCOPE]):
            next_clause = k and words[i].text in NEXT_CLAUSE and words[i - 1].text != "to"
            between = text[edge : words[i].start]
            if listing and k and "," in between and not stops(between, words[i].text, ".;:?!\n"):
                # Only a negation of something named ("no fever, chills"), not of a number ("124 wait no 118, ...").
                if not any(_is_content(words[j].text) for j in reached & set(ahead[:k])) or not _more_of_the_list(
                    text, words, i, ends
                ):
                    break
                crossed = True
            elif stops(between, words[i].text) or next_clause or (crossed and words[i].text in LIST_ENDS | CLAUSE_STARTS) or (
                    listing and k >= NEGATION_SCOPE and "," not in text[cue.end() : words[i].start]):
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
    for cue in closing.finditer(text) if closing else ():
        cue_words |= {i for i, token in enumerate(words) if cue.start() <= token.start < cue.end()}
        edge = text.rfind("\n", cue.start(), cue.end()) + 1 or cue.start()  # not back past a new paragraph
        for i in reversed([i for i, token in enumerate(words) if token.end <= cue.start()]):
            if stops(text[words[i].end : edge], words[i].text, ".;:?!\n"):
                break
            reached.add(i)
            edge = words[i].start
    return reached, cue_words


def _more_of_the_list(text: str, words: list[Token], first: int, ends: frozenset[str]) -> bool:
    """Whether what follows a comma is more of a list a negation carries through, up to the first word that ends it
    (_reach() stops there): it doesn't open with one, and the first of them before the next comma, semicolon or full
    stop is a contrast ("but", "except"), a word starting a clause ("so", "because") or a step of the plan, or else a
    new subject (_SUBJECTS) or a word saying something is there or someone told it (LIST_ENDS: "has", "is", "with",
    "reports", "endorses") after "and" or "or". So "denies fever, chills or night sweats but reports weight loss"
    carries to "but"; "no fever, the cough is better" and "without high steroid doses, so 3 months ago we added ..."
    don't cross the comma, what follows being a clause of its own."""
    end = min(
        _sentence_end(text, words[first].start),
        *((text.find(mark, words[first].start) + 1 or len(text) + 1) - 1 for mark in ",;"),
    )
    piece = [word for word in words[first:] if word.end <= end]
    for k, word in enumerate(piece):
        if not (word.text in ends or word.text in NEXT_CLAUSE or word.text in CLAUSE_STARTS or word.text in LIST_ENDS):
            continue
        if not k:
            return False
        if word.text in _SUBJECTS or word.text in LIST_ENDS:
            return piece[k - 1].text in ("and", "or")
        return True
    return True


# The words a new subject starts with, which end a negation's list only after "and" or "or": "chills and she ...".
_SUBJECTS = frozenset("i we you he she they patient which who he's she's they're i'm we're i've let's i'll we'll".split())


def _object_of_a_preposition(text: str, start: int) -> bool:
    """Whether the words before `start` are a preposition, and maybe a possessive: "with her", "by his"."""
    before = [token.text for token in tokens(text[:start])][-2:]
    if before and before[-1] in {"her", "his", "their", "the", "a"}:
        before = before[:-1]
    return bool(before) and before[-1] in NOT_EXPERIENCER_AFTER


def _qualifiers(text: str, words: list[Token]) -> dict[str, set[int]]:
    """For each kind of qualifier, by its reason code, the words its cues reach in the text."""
    return {qualifier.reason: _reach(text, words, qualifier)[0] for qualifier in QUALIFIERS}


def _dropped(value: str, spans: list[tuple[int, int]], transcript: _Transcript) -> set[str]:
    """The qualifiers that reach words of the value's sources, in the transcript, which the value uses without them,
    even if another source has those words plain: a negation, someone else having it, uncertainty, or the past. Read
    in the transcript, so a source can't leave its qualifier out ("chest pain" quoted from "no chest pain")."""
    words = tokens(value)
    found = set()
    for qualifier in QUALIFIERS:
        reason = qualifier.reason
        reached, cues = _reach(value, words, qualifier)
        content = lambda word: _is_content(word, numbers=reason == "certainty_dropped")  # noqa: E731
        plain = {stem(word.text) for i, word in enumerate(words) if content(word.text) and i not in reached | cues}
        plain -= {stem(words[i].text) for i in reached if content(words[i].text)}
        qualified = {
            stem(transcript.words[i].text)
            for i in transcript.qualified[reason]
            if content(transcript.words[i].text)
            and any(start <= transcript.words[i].start and transcript.words[i].end <= end for start, end in spans)
        }
        if qualified & plain:
            found.add(reason)
    return found


def _is_content(word: str, numbers: bool = False) -> bool:
    """A word a qualifier can apply to: not a function word, but "MI" or "PE" is one; a number only for a doubt ("her
    temperature was maybe 38")."""
    return (word.isalpha() and len(word) >= 2 and word not in STOPWORDS) or (numbers and word[0].isdigit())


# ---- Proposals (the expansion plan's step 15; FR-14 as reworded) --------------------------------------------------

MAX_PROPOSALS = 20  # at most, in one dictation, as the on-prem app stages them
TAKEN_BACK_REACH = 25  # at most, words after an instruction in which taking it back holds it back
FRAGMENT_REACH = 10  # at most, words of a piece of an instruction's sentence that may be listed with it
# Why a listed instruction isn't proposed, in the order the rules are tried: the first that applies is the one given.
HELD_BACK = (
    "not_final", "to_the_software", "mixed_kinds", "not_an_addition", "negated", "hedged", "for_later", "taken_back",
    "in_the_past", "unclear", "question", "quoted", "not_imperative", "names_no_item", "not_plain", "too_many",
)  # fmt: skip
# What may come between the start of an instruction's clause and the instruction: its lead-in (LEAD_IN) and the words
# a clinician starts a step of the plan with.
OPENERS = LEAD_IN | frozenset("okay ok alright so plan next finally lastly first second third going to now i'd like want need".split())
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
    for start, end, reason, kinds, at, own in _instructions(text):
        kind = "order" if kinds == {"order"} else ALLERGY_ADDITION if kinds == {ALLERGY_ADDITION} else reason
        held_back = "not_final" if not transcript.final else _held_back(text, words, unclear, own, end, kinds, at)
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
    following = _next_sentence(text, sentence_end)
    next_sentence_end = following[1] if following else min(sentence_end + 1, len(text))
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
    kind = "order" if kinds == {"order"} else ALLERGY_ADDITION
    said = _whole([token for token in words if start <= token.start and token.end <= end], text)
    of = "order" if kind == "order" else "allergen"
    if not any(is_item(said[i : i + size], of) for i in range(len(said)) for size in range(1, MAX_ITEM_WORDS + 1)):
        return "names_no_item"
    if not closed(text[sentence:sentence_end], kind) or _neighbours_hold_it(text, sentence, sentence_end, kind):
        return "not_plain"
    return None


# ---- Items: what the reference data names (catalog.py) -----------------------------------------------------------

# Never in an item: a negation or a number word ("Order a CBC, no.", "Order another one.", though the reference data has
# "No. 40" and "next choice one dose"). And an item never starts with a word of the closed classes (_CLOSED_WORDS, below).
NEVER_IN_AN_ITEM = (
    frozenset("no not none never nothing without nor neither".split()) | frozenset(UNITS) | frozenset(TENS)
    | frozenset(ORDINAL_UNITS) | frozenset(ORDINAL_TENS) | {"hundred", "thousand"}
)


@functools.cache
def _names(kind: str) -> tuple[list[tuple[str, ...]], dict[str, list[tuple[int, int, int]]], frozenset[str], frozenset[int]]:
    """The names of one kind ("order" or "allergen"), each as its words; their standalone runs, by first word, each as
    the name, its start and its end; every word they hold; and which of them are medications (marked "+"). Read from
    catalog.py the first time a proposal or a noted allergy is weighed, not at start."""
    from . import catalog

    names: list[tuple[str, ...]] = []
    runs: dict[str, list[tuple[int, int, int]]] = {}
    medications: set[int] = set()
    for line in (catalog.ORDERABLES if kind == "order" else catalog.ALLERGENS).split("\n"):
        if line.startswith("+"):
            medications.add(len(names))
        marked = line.lstrip("+").split()
        if not marked:
            continue
        words = tuple(word.strip("^$") for word in marked)
        start = None
        for i, word in enumerate(marked):
            start = i if word.startswith("^") else start
            if word.endswith("$") and start is not None:
                runs.setdefault(words[start], []).append((len(names), start, i + 1))
        names.append(words)
    return names, runs, frozenset(word for name in names for word in name), frozenset(medications)


def is_item(words: list[str], kind: str) -> bool:
    """Whether the words (lower case) name an item of one kind as the reference data names it: with its numbers left
    out, they are a run of one name's words that begins with one of its standalone runs whole (catalog.py: a generic
    head, an abbreviation in parentheses, a brand in brackets, a common catalog item's name) and may go on along it. So
    "lisinopril" and "lisinopril 10 mg oral tablet" are, "chest" or "morning" alone isn't; none starts with a stopword,
    a single letter or a word of the closed classes, or holds a negation or a number word (NEVER_IN_AN_ITEM), and a
    number is part of one only between two of its words, and never a zero ("lisinopril 10 mg", not "CBC 2019" or
    "lisinopril 0 mg"). A word a plain instruction reads whole (ONE_WORD: "A1c", "B12") is compared by its letters, as
    the catalog writes names without their numbers: "a1c" is "^a c$", the common name of Hemoglobin A1c. Of kind "order"
    any orderable, MEDICATION only a medication, and otherwise an allergen."""
    if not words or words[0][0].isdigit() or words[-1][0].isdigit() or any(_is_zero(word) for word in words):
        return False
    words = [word for word in words if not word[0].isdigit()]
    if len(words[0]) < 2 or words[0] in STOPWORDS or words[0] in _CLOSED_WORDS or any(
        word in NEVER_IN_AN_ITEM for word in words
    ):
        return False
    words = [part for word in words for part in (re.findall(r"[a-z]+", word) if ONE_WORD.fullmatch(word) else [word])]
    names, runs, _, medications = _names("order" if kind in ("order", MEDICATION) else "allergen")
    return any(
        end - start <= len(words) and names[i][start : start + len(words)] == tuple(words)
        and (kind != MEDICATION or i in medications)
        for i, start, end in runs.get(words[0], ())
    )


# ---- A plain instruction: the closed sentence a proposal is made from ---------------------------------------------

# An instruction is proposed only when every word of its sentence falls, in this order, into one of these closed
# classes (closed()); no list of what must not be said decides it:
# 1. lead-ins (PLAIN_LEADS), each maybe followed by a comma;
# 2. the verb in one of its plain forms (ORDER_FORMS, ALLERGY_FORMS), an -ing form only after "I'm", "I am", "we're" or
#    "we are";
# 3. determiners and possessives (DETERMINERS, and "new" after "a" or "an"), and for an order the modifiers fasting,
#    stat, urgent, routine and repeat (ORDER_MODIFIERS);
# 4. an item: an orderable for an order, but a medication for refill, prescribe, renew, e-prescribe, write a prescription
#    or script for, call in and send (MEDICINES), and an allergen for an allergy to add, as the reference data names it
#    (is_item()); every item of a joined list one the verb takes;
# 5. an order's quantities (QUANTITY): numbers, doses, forms, routes, frequencies, durations, supply, refills; a number
#    only in one of them or between two words of the item's name ("Order a CBC 2019" isn't plain), never a zero ("0
#    times a day"; "no refills" is a phrase of its own);
# 6. trailers (TRAILERS); the allergy list for an allergy to add ("to her allergies"); the pharmacy for "send" ("to her
#    pharmacy"); and "and" or a comma joining another item, with its own determiners and quantities.
PLAIN_LEADS = tuple(tuple(lead.split()) for lead in (
    "okay", "ok", "alright", "so", "and", "also", "then", "now", "next", "finally", "lastly", "please", "plan", "plan :",
    "go ahead and", "i'll", "i will", "i'm going to", "i am going to", "we'll", "we will", "we're going to",
    "we are going to", "let's", "let us", "i'd like to", "i want to", "we need to", "will",
))
DOING = (("i'm",), ("i", "am"), ("we're",), ("we", "are"))
ITEMS = "items"  # where a form takes its items
MEDICINES = "medicines"  # where a form takes its items, medications only
MEDICATION = "medication"  # the kind of item a refill, a prescription or the pharmacy takes
# Each plain form of an order's verb, and its -ing form, with what it takes: its items (any orderable for an order,
# medications only for the rest), and for "send" and "call in" the pharmacy.
_PHARMACY = r"(?:to|into) (?:her|his|their|the) (?:usual )?pharmacy "
ORDER_FORMS = tuple(
    (tuple(verb.split()), tuple(doing.split()), steps) for verb, doing, steps in (
        ("order", "ordering", (ITEMS,)), ("refill", "refilling", (MEDICINES,)),
        ("prescribe", "prescribing", (MEDICINES,)), ("renew", "renewing", (MEDICINES,)),
        ("e prescribe", "e prescribing", (MEDICINES,)),
        ("place an order for", "placing an order for", (ITEMS,)), ("put in an order for", "putting in an order for", (ITEMS,)),
        *((f"{verb} {whom}a {paper} for", f"{doing} {whom}a {paper} for", (MEDICINES,))
          for verb, doing in (("write", "writing"),) for whom in ("", "her ", "him ", "them ")
          for paper in ("prescription", "script")),
        ("call in", "calling in", (MEDICINES, _PHARMACY)),
        ("send", "sending", (MEDICINES, _PHARMACY)),
        ("send a prescription for", "sending a prescription for", (MEDICINES, _PHARMACY)),
    )
)
# Each plain form of adding to the allergy list, as what follows the lead-ins: words (a pattern) or the items.
_LIST = r"(?:to|on) (?:(?:her|his|their|the) )?(?:(?:drug|medication) )?(?:allergies|allergy list|list of allergies) "
_ALLERGY_VERB = r"(?:add|record|note|document|enter) (?:an? )?(?:new )?"
ALLERGY_FORMS = (
    (r"(?:add|put) ", ITEMS, _LIST),  # "add penicillin to her allergies"
    (r"add ", ITEMS, r"as an? allergy "),  # "add penicillin as an allergy"
    (r"list ", ITEMS, r"as (?:an? )?allergy "),  # "list penicillin as an allergy"
    (_ALLERGY_VERB + r"allergy to ", ITEMS),  # "add an allergy to codeine"
    (_ALLERGY_VERB, ITEMS, r"allergy "),  # "record a penicillin allergy"
    (ITEMS, r"(?:should|needs to|must|will) be added ", _LIST),  # "penicillin should be added to her allergies"
)
DETERMINERS = frozenset("a an the her his their some another".split())
# What an order may say of its item before it, after its determiners: "a fasting lipid panel", "a stat CBC".
ORDER_MODIFIERS = frozenset("fasting stat urgent routine repeat".split())
JOINS = ((",", "and"), (",",), ("and",))
TRAILERS = tuple(tuple(trailer.split()) for trailer in ("today", "now", "stat", "please", "too", "as well", "also"))
MAX_ITEM_WORDS = 8  # at most, words of one item's name
_NUMBER_WORDS = frozenset(UNITS) | frozenset(TENS) | {"hundred"}
# An order's quantities, each a run of words as said, with a number as "#" (closed lists: no other word counts).
_PERIODS = r"days?|weeks?|months?|years?"
_DOSE_UNITS = (
    r"mg|mcg|micrograms?|milligrams?|g|grams?|ml|milliliters?|units?|percent|puffs?|tablets?|tabs?|capsules?|caps?|drops?|"
    r"sprays?|patch(?:es)?|doses?|inhalers?|pens?|vials?|bottles?|tubes?"
)
QUANTITY = re.compile(
    r"(?:(?:, )?(?:"
    rf"# (?:{_DOSE_UNITS})|(?:a )?# (?:day|week|month) supply|"
    r"tablets?|capsules?|cream|ointment|gel|lotion|solution|suspension|syrup|liquid|inhaler|patch|injection|drops|spray|"
    r"pen|tube|by mouth|po|oral|orally|topical|topically|iv|im|subcutaneously|sublingually|inhaled|nasally|rectally|vaginally|"
    r"(?:once|twice|# times) (?:a|per|every) (?:day|week|month)|(?:once|twice|# times) daily|daily|nightly|weekly|"
    r"monthly|bid|tid|qid|qhs|qd|qod|qam|qpm|hs|a day|per day|"
    rf"every # (?:hours?|{_PERIODS})|every (?:day|morning|evening|night|other day)|at (?:bedtime|night)|"
    r"in the (?:morning|evening)|(?:with|after|before) (?:meals|food|breakfast|dinner|supper)|"
    rf"for # (?:{_PERIODS})|for (?:a|another) (?:day|week|month|year)|x # (?:{_PERIODS})|(?:with )?(?:#|no) refills?"
    r") )*"
)

# Every word an order's quantities may hold.
_QUANTITY_WORDS = frozenset(re.findall(r"[a-z]{2,}", QUANTITY.pattern))
# Every word of the closed classes but the items: none starts an item.
_CLOSED_WORDS = frozenset(
    word
    for text in (
        *(" ".join(lead) for lead in PLAIN_LEADS), *(" ".join(verb + ing) for verb, ing, _ in ORDER_FORMS),
        *(step for form in ALLERGY_FORMS for step in form if step != ITEMS), _PHARMACY, _LIST, " ".join(DETERMINERS),
        " ".join(ORDER_MODIFIERS), *(" ".join(trailer) for trailer in TRAILERS), QUANTITY.pattern, "and new",
    )
    for word in re.findall(r"[a-z']{2,}", text)
)


def closed(sentence: str, kind: str) -> bool:
    """Whether a sentence is a plain instruction (see above), every word of it accounted for: an order (kind "order")
    or an allergy to add (ALLERGY_ADDITION)."""
    words = _closed_words(sentence)
    if words is None:
        return False
    for at in _past_the_lead_ins(words):
        if kind == "order":
            doing = next((at + len(lead) for lead in DOING if tuple(words[at : at + len(lead)]) == lead), None)
            forms = [
                (start + len(form), steps)
                for verb, ing, steps in ORDER_FORMS
                for form, start in ((verb, at), (ing, doing))
                if start is not None and tuple(words[start : start + len(form)]) == form
            ]
        else:
            forms = [(at, form) for form in ALLERGY_FORMS]
        if any(_trailers(words, end) for start, steps in forms for end in _steps(words, start, steps, kind)):
            return True
    return False


def _closed_words(sentence: str) -> list[str] | None:
    """The sentence's words in lower case, a comma and the colon after "plan" each a word of its own, and a word
    written as letters and digits read whole (ONE_WORD); None when any other mark comes between two words (a semicolon,
    a dash, a bracket, a quote...)."""
    whole = [match.span() for match in ONE_WORD.finditer(sentence)]
    words: list[str] = []
    last = 0
    for match in WORD.finditer(sentence):
        raw = sentence[last : match.start()]
        between = raw.strip()
        word = match.group().lower().replace("’", "'")
        if not raw and words and any(start < match.start() < end for start, end in whole):
            words[-1] += word
            last = match.end()
            continue
        if between in (",", ":") and words and (between == "," or words[-1] == "plan"):
            words.append(between)
        elif between and raw != "-":
            return None
        words.append(word)
        last = match.end()
    return words if sentence[last:].strip() in ("", ".", "!") else None


# A word written as letters, digits and letters, or as one letter and digits, with nothing between them, which a plain
# instruction and its item reads whole ("A1c", "HbA1c", "B12", "D3", "T4"): never a count ("x2"), a number and its unit
# ("10mg"), or a name and a number ("CBC2"), which are read as before. Only the closed sentence and the item check read
# words this way; FR-14's rule (directives()) and every field check read them as tokens() does.
ONE_WORD = re.compile(r"(?<![A-Za-z0-9])(?:[A-Za-z]+\d+[A-Za-z]+|[A-WYZa-wyz]\d+)(?![A-Za-z0-9])")


def _whole(found: list[Token], text: str) -> list[str]:
    """Tokens' words as a plain instruction reads them, each run of them ONE_WORD spans read whole."""
    whole = [match.span() for match in ONE_WORD.finditer(text, found[0].start, found[-1].end)] if found else []
    words: list[str] = []
    for i, token in enumerate(found):
        if i and found[i - 1].end == token.start and any(start < token.start < end for start, end in whole):
            words[-1] += token.text
        else:
            words.append(token.text)
    return words


def _past_the_lead_ins(words: list[str]) -> list[int]:
    """Where the verb may start: after any run of lead-ins, each maybe followed by a comma."""
    reached, todo = {0}, [0]
    while todo:
        at = todo.pop()
        for lead in PLAIN_LEADS:
            end = at + len(lead)
            if tuple(words[at:end]) == lead:
                for after in (end, end + 1) if words[end : end + 1] == [","] else (end,):
                    if after not in reached:
                        reached.add(after)
                        todo.append(after)
    return sorted(reached)


def _steps(words: list[str], at: int, steps: tuple, kind: str) -> set[int]:
    """Where the words from `at` may end, having said each step in turn: words (a pattern) or the items."""
    ends = {at}
    for step in steps:
        if step in (ITEMS, MEDICINES):
            of = MEDICATION if step == MEDICINES else "order" if kind == "order" else "allergen"
            ends = {end for start in ends for end in _items(words, start, of)}
        else:
            ends = {start + len(match.group().split()) for start in ends
                    if (match := re.match(step, " ".join(words[start:]) + " "))}
    return ends


def _items(words: list[str], at: int, kind: str) -> set[int]:
    """Where a run of items of one kind said from `at` may end: each with its determiners and an order's quantities,
    joined by "and" or a comma."""
    ends: set[int] = set()
    todo, seen = [at], set()
    while todo:
        start = todo.pop()
        if start in seen:
            continue
        seen.add(start)
        while start < len(words) and (
            words[start] in DETERMINERS or (words[start] == "new" and start > at and words[start - 1] in ("a", "an"))
            or (kind == "order" and words[start] in ORDER_MODIFIERS)
        ):
            start += 1
        for end in range(start + 1, min(len(words), start + MAX_ITEM_WORDS) + 1):
            if "," in words[start:end] or not is_item(words[start:end], kind):
                continue
            end = _quantities(words, end) if kind != "allergen" else end
            ends.add(end)
            todo += [end + len(join) for join in JOINS if tuple(words[end : end + len(join)]) == join]
    return ends


def _quantities(words: list[str], at: int) -> int:
    """Where an order's quantities said from `at` end (QUANTITY). A zero is no number of them ("0 times a day", "for
    zero days"); "no refills" is a phrase of its own."""
    shape = ["0" if _is_zero(word) else "#" if word[0].isdigit() or word in _NUMBER_WORDS else word for word in words[at:]]
    return at + QUANTITY.match(" ".join(shape) + " ").group().count(" ")


def _is_zero(word: str) -> bool:
    """Whether a word is a zero: "zero", "0", "0.0"."""
    return word == "zero" or (word[0].isdigit() and not word.strip("0."))


def _trailers(words: list[str], at: int) -> bool:
    """Whether nothing but trailers, each maybe after a comma, follows `at`."""
    while at < len(words):
        if words[at] == ",":
            at += 1
        trailer = next((t for t in TRAILERS if tuple(words[at : at + len(t)]) == t), None)
        if trailer is None:
            return False
        at += len(trailer)
    return True


# The sentences next to a plain instruction are read for what may take it back. Those after it are read past line
# breaks and heading lines ("Addendum:", "Correction:", "Plan:"), stopping only at the heading of another of the note's
# own sections (_SECTIONS: Subjective, Objective, Exam, Examination, Observations, Vitals, Assessment, Impression),
# within TAKEN_BACK_REACH words, up to a sentence that holds an instruction of its own. Each of them, the first and every
# later one (_sentences_after_hold_it()):
# - when it has BARE_SENTENCE words or fewer, or opens with a clause that short before a comma, holds it back unless
#   that is accounted for (_accounted()), as the closed sentence's words are: a step of the plan said with its verb and
#   what follows it (_PLAN_STEPS: "Recheck in one week.", "Continue amlodipine."; not stop, hold, avoid or discontinue,
#   which said alone take it back), a heading with what follows it accounted for too ("Assessment: stable." is not,
#   "Plan: recheck in one week." is), lead-ins alone ("Okay.", "Next,") or thanks. So "Nah.", "Oops.", "Kidding.",
#   "Questionable." or "Hang on, let me check her pressure first." hold it back, though no list names them; a longer
#   sentence whose words of refusal, doubt or reassignment no list holds ("She turned down the blood test.") does not;
# - holds it back with a negation (NEGATION), a word of error (_ERROR) or a condition (CONDITION_WORDS) anywhere in it,
#   whatever it opens with ("She is not willing to have blood drawn today.", "Recommend she not have any blood drawn
#   today.", "The last line was a mistake.", "Patient prefers to wait until after the holidays.");
# - holds it back when it has BARE_SENTENCE words or fewer and a negation, a doubt or a retraction word (_NEXT_DOUBT),
#   or points back at it (_POINTS_BACK); when it points back, or names its item again (by another of its names too:
#   "Leave the blood count for now." after "Order a CBC.", _named_again()), and holds a negation or a word of removing
#   it (_REMOVES: "We don't really need that one", "Take that out of the plan", "She had one done two days ago"); and,
#   for an allergy to add, when it speaks of an allergy or a reaction with a negation, someone else or a word pointing
#   back ("She is not truly allergic to penicillin", "It was her mother who reacted", "Turns out it was a viral rash").
# The first of them also holds it back when it opens with "no", "not", "or", "instead", "actually", "hold" or "wait", or
# goes on with what the instruction's sentence said ("with", "for", "because", "since", "which", "to": "Add
# nitrofurantoin to her allergy list. with a reaction rash", a full stop heard where none was said) (_NEXT_OPENING);
# points back within its first three words ("Nix that, she had one Friday", a retraction misheard: "Cancer that order
# for now"); or is only a condition, a time for later, or says it was done already, ordered or sent ("Order a CBC. In
# two weeks.", "Already done."), unless it opens a step of the plan of its own ("Recheck in one week."). A later one
# also holds it back when it retracts it (TAKEN_BACK) or opens like a retraction (_LATER_OPENING).
# Before it, when the previous sentence is a bare negation or doubt of BARE_SENTENCE words or fewer, a bare sentence
# naming someone else ("Mom insists.", "Her mother asked."), for an allergy to add one about an allergy, a reaction or its
# item with someone else ("Her sister reacted to penicillin."), a condition said as a sentence of its own ("If her
# potassium is low."), a heading line but Plan, Orders or Labs (PROPOSAL_HEADINGS), or introduces a report or a list of
# what not to do (_REPORT_BEFORE: quoting, reading from, per, according to, says, a request, a question, the following,
# avoid, not to do); when it is a retraction (TAKEN_BACK: "Hold off on these for now."); and when a word of refusing,
# removing or putting off (_NOT_TO_DO: "Things she refused.", "Skip these today.", "what we're not doing") is said
# anywhere before it in its paragraph, within TAKEN_BACK_REACH words, whatever is said between ("Not to do today. She is
# frail. Order a CBC."), unless it is said of what came before it ("Skip that.").
BARE_SENTENCE = 4
_NEXT_DOUBT = frozenset(
    "no nope not never none nothing sure maybe or wrong hold wait think instead rather actually probably perhaps possibly "
    "likely unlikely might could would may".split()
)
_BARE_BEFORE = frozenset("no not nothing none never sure probably perhaps possibly maybe likely unlikely might could would may".split())
_NEXT_OPENING = frozenset("no not nope never or instead rather actually hold wait with for because since which to".split())
_POINTS_BACK = frozenset("that it this these those them one".split())
_A_NUMBER_OF = frozenset("day days week weeks month months year years hour hours time times more".split())  # "one week"
_REMOVES = frozenset(
    "no not never none nothing remove erase omit withdraw leave out forget against bother need necessary prefer wrong done "
    "already ago yesterday".split()
)
_ALLERGY_WORDS = frozenset("allergy allergies allergic react reacts reacted reaction tolerate tolerated tolerates rash hives".split())
_REPORT_BEFORE = re.compile(
    r"\b(?:quoting|quote|reading from|according to|says|said|states|stated|request|requests|requested|question|"
    r"hypothetical(?:ly)?|the following|avoid|not to do|not do|not be doing|do not|decided against|would not|wants|"
    r"(?:not|n't) want|"
    r"letter|records)\b|^\s*per\b",
    re.IGNORECASE,
)
# What a short sentence after an instruction may be and still be taken as unrelated to it (_accounted()): a step of the
# plan opened by one of these with what follows it, lead-ins alone, or thanks.
_PLAN_STEPS = (STEP_VERBS - frozenset("stop hold avoid discontinue".split())) | {"follow"}
_ALONE = frozenset("okay ok alright so and also then now next finally lastly plan".split())
_THANKS = (("thanks",), ("thank", "you"))
_ERROR = re.compile(r"\b(?:mistakes?|mistaken|errors?|wrong|incorrect)\b", re.IGNORECASE)
_CONDITION = re.compile(rf"\b(?:{'|'.join(sorted(CONDITION_WORDS, key=len, reverse=True))})\b", re.IGNORECASE)
# The headings of a note's own sections but Plan: reading after an instruction stops at one.
_SECTIONS = HEADINGS - {"plan"}
# A list of what not to do, or of what was refused, removed or put off, which holds back the instructions after it in
# its paragraph.
_NOT_TO_DO = re.compile(
    r"\b(?:avoid|not to do|not do|not be doing|not doing|do not|decided against|would not|declin(?:e|ed|es|ing)|"
    r"refus(?:e|ed|es|ing)|skip(?:s|ped|ping)?|hold(?:ing)? off|holding|struck|cancell?(?:ed|ing)|delet(?:ed|ing)|"
    r"remov(?:ed|ing)|reject(?:ed|ing)?|defer(?:red|ring)?|off the table)\b|(?:\bnot|n't) want\b",
    re.IGNORECASE,
)


def _neighbours_hold_it(text: str, sentence: int, sentence_end: int, kind: str) -> bool:
    """Whether the sentences before or after a plain instruction may take it back, doubt it, put it off or report it
    (see above)."""
    previous_end = len(text[:sentence].rstrip())  # back over the blank lines between paragraphs
    previous_start = max((mark.end() for mark in SENTENCE_MARK.finditer(text, 0, max(previous_end - 1, 0))), default=0)
    previous = [token.text for token in tokens(text[previous_start:previous_end])]
    said_before = text[previous_start:previous_end].strip()
    # A heading line it stands under, but Plan, Orders or Labs ("Next visit:", "Declined:", "If symptoms persist:"); a
    # condition said as a sentence of its own ("If her potassium is low. Then order a BMP.").
    if said_before.endswith(":") and said_before[:-1].strip().lower() not in PROPOSAL_HEADINGS:
        return True
    if said_before and _CONDITION_OPENING.match(said_before) and not directives(said_before):
        return True
    if previous and len(previous) <= BARE_SENTENCE and any(
        word in _BARE_BEFORE or word.endswith("n't") for word in previous
    ):
        return True
    holding = _names("order" if kind == "order" else "allergen")[2]
    named = {token.text for token in tokens(text[sentence:sentence_end]) if token.text not in STOPWORDS and token.text in holding}
    # Someone else before it: a bare sentence naming them ("Mom insists."), or, for an allergy to add, one about an
    # allergy, a reaction or its item ("Her sister reacted to penicillin.").
    if previous and _SOMEONE_ELSE.search(said_before) and (
        len(previous) <= BARE_SENTENCE
        or (kind == ALLERGY_ADDITION and any(word in _ALLERGY_WORDS or word in named for word in previous))
    ):
        return True
    # A report or a list introduced before a run of instructions holds each of them: "Things not to do today. Order a
    # CT of the head. Refill her oxycodone."
    end = sentence
    while end:
        start = max((mark.end() for mark in SENTENCE_MARK.finditer(text, 0, max(end - 1, 0))), default=0)
        if _REPORT_BEFORE.search(text[start:end]):
            return True
        if not directives(text[start:end]):
            break
        end = start
    if _not_to_do_before(text, sentence):
        return True
    return _sentences_after_hold_it(text, sentence, sentence_end, kind, named - _CLOSED_WORDS)


def _sentences_after_hold_it(text: str, sentence: int, sentence_end: int, kind: str, named: set[str]) -> bool:
    """Whether a sentence after a plain instruction, within TAKEN_BACK_REACH words, read past line breaks and heading
    lines, up to one holding an instruction of its own, may take it back, doubt it, put it off or report it (see
    above). `named` is the instruction's item words."""
    at, left, first = sentence_end, TAKEN_BACK_REACH, True
    while left > 0 and (after := _next_sentence(text, at)) is not None:
        said = text[after[0] : after[1]]
        following = [token.text for token in tokens(said)]
        at = after[1]
        if not following:
            continue
        bare = [
            "1" if word == "one" and following[i + 1 : i + 2] and following[i + 1] in _A_NUMBER_OF
            else word.removesuffix("'s")
            for i, word in enumerate(following)
        ]
        negated = any(word in _REMOVES or word.endswith("n't") for word in bare)
        doubted = any(word in _NEXT_DOUBT for word in bare)
        # Naming its item again counts as pointing back at it: "Forget about the CBC.", "Leave the blood count for now."
        renamed = _named_again(bare, text[sentence:sentence_end], kind)
        pointing = renamed or any(word in _POINTS_BACK or word in named for word in bare)
        # For an allergy: "that" counts only first ("That was her sister actually"), not as a conjunction ("Add a note to
        # her allergies that the reaction was hives").
        pointing_at_allergy = renamed or bare[0] == "that" or any(
            word in _POINTS_BACK - {"that"} or word in named for word in bare
        )
        if (
            (len(following) <= BARE_SENTENCE and any(word in _NEXT_DOUBT | _POINTS_BACK or word.endswith("n't") for word in bare))
            or (pointing and (negated or doubted))
            or (kind == ALLERGY_ADDITION and any(word in _ALLERGY_WORDS for word in bare) and (
                negated or pointing_at_allergy or _SOMEONE_ELSE.search(" ".join(bare)) is not None))
        ):
            return True
        if first and (bare[0] in _NEXT_OPENING or any(word in _POINTS_BACK for word in bare[:3])):
            return True
        if directives(said):
            return False
        if first and following[0] not in NEXT_CLAUSE and (
            _CONDITION_OPENING.match(said) or FOR_LATER.search(said) or PAST.search(said) or _DONE.search(said)
        ):
            return True
        if not first and (TAKEN_BACK.search(said) or following[0] in _LATER_OPENING):
            return True
        opening = said.split(",", 1)[0]
        if (len(following) <= BARE_SENTENCE or len(tokens(opening)) <= BARE_SENTENCE) and not _accounted(opening):
            return True
        if NEGATION.search(said) or _ERROR.search(said) or _CONDITION.search(said):
            return True
        left -= len(following)
        first = False
    return False


def _next_sentence(text: str, mark: int) -> tuple[int, int] | None:
    """Where the sentence after the one a sentence mark at `mark` ends starts and ends, read past line breaks and heading
    lines ("Addendum:", "Correction:", "Plan:"); None at the end of the text, or at the heading of another of the note's
    own sections (_SECTIONS: "Assessment:", "Exam:"), where another part of the note starts."""
    while True:
        start = mark + 1
        while start < len(text) and text[start].isspace():
            start += 1
        if start >= len(text):
            return None
        end = _sentence_end(text, start)
        said = text[start:end].rstrip()
        if not said.endswith(":"):
            return start, end
        if said[:-1].strip().lower() in _SECTIONS:
            return None
        mark = end


def _opens_a_step(words: list[str]) -> bool:
    """Whether a sentence's words open a step of the plan with what follows its verb: "Recheck in one week."."""
    return len(words) > 1 and words[0] in _PLAN_STEPS


def _accounted(said: str) -> bool:
    """Whether a short sentence after a plain instruction, or the short clause it opens with, is accounted for as
    unrelated to it: a step of the plan; a heading, with what follows it accounted for too; lead-ins alone; or thanks
    (see above)."""
    words = [token.text for token in tokens(said)]
    if words and words[0] in HEADINGS | PROPOSAL_HEADINGS and (heading := re.match(r"\s*[\w ]+?\s*:", said)):
        return not tokens(said[heading.end() :]) or _accounted(said[heading.end() :])
    return bool(words) and (
        _opens_a_step(words) or all(word in _ALONE for word in words) or tuple(words) in _THANKS
    )


def _named_again(words: list[str], sentence: str, kind: str) -> bool:
    """Whether words name the item an instruction's sentence names again, by any of its names: two words in a row, neither
    a stopword, of a name whose standalone run the sentence says ("blood count", of "Complete blood count (CBC)", after
    "Order a CBC")."""
    names, runs, _, _ = _names("order" if kind == "order" else "allergen")
    said = [token.text for token in tokens(sentence)]
    pairs = {
        pair
        for i, word in enumerate(said)
        for n, start, end in runs.get(word, ())
        if tuple(said[i : i + end - start]) == names[n][start:end]
        for pair in zip(names[n], names[n][1:])
        if not set(pair) & STOPWORDS
    }
    return any(pair in pairs for pair in zip(words, words[1:]))


def _not_to_do_before(text: str, sentence: int) -> bool:
    """Whether a word of refusing, removing or putting off (_NOT_TO_DO) is said before an instruction's sentence in its
    paragraph, within TAKEN_BACK_REACH words, whatever is said between, or a retraction (TAKEN_BACK) in the sentence
    right before it; either unless said of what came before it ("Skip that."). A blank line ends the paragraph, and a
    heading line opens it."""
    at, left = sentence, TAKEN_BACK_REACH
    while left > 0:
        end = len(text[:at].rstrip())
        if not end or re.search(r"\n[ \t]*\n", text[end:at]):
            return False
        start = max((mark.end() for mark in SENTENCE_MARK.finditer(text, 0, end - 1)), default=0)
        said = text[start:end]
        words = [token.text for token in tokens(said)]
        of_what_came_before = any(word in ("that", "it") for word in words) and not any(
            word in ("these", "following", "next", "below") for word in words)
        if not of_what_came_before and (_NOT_TO_DO.search(said) or (at == sentence and TAKEN_BACK.search(said))):
            return True
        if said.strip().endswith(":"):
            return False
        left -= len(tokens(said))
        at = start
    return False


_SOMEONE_ELSE = re.compile(rf"\b(?:{FAMILY})\b", re.IGNORECASE)
# The headings a proposal may stand under, on a line before it.
PROPOSAL_HEADINGS = frozenset({"plan", "orders", "labs", "assessment and plan"})
# A sentence that opens with a condition: "If her fever returns.", "Only if...", "In case...", "Depending on...".
_CONDITION_OPENING = re.compile(
    r"\s*(?:(?:only|but|and)\s+)?(?:if|unless|when|whenever|once|should|in case|provided|assuming|depending|until|after|"
    r"before|pending)\b",
    re.IGNORECASE,
)
_DONE = re.compile(r"\b(?:done|ordered|sent)\b", re.IGNORECASE)
# How a later sentence opens that takes it back: "No, just the BMP.", "Actually, let's not.", "Wait, that was her sister."
_LATER_OPENING = frozenset("no not nope never or instead rather actually hold wait".split())


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
