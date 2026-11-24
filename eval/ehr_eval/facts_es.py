"""What each Spanish script says, written down by hand, and a reading of the English shown that shares nothing with the
checks it measures (the expansion plan's step 17). scoring_es.py counts as changed what a shown statement says that
the facts of the Spanish it cites don't: it must never read the English with translation.py's, spanish.py's or
validation.py's own readers, or a mistake in them would be measured as right. It imports only the standard library
(tests/test_facts_es.py checks it).

Each script lists the facts it says, one entry for each passage of its display text that says some ([[facts]]):

    [[facts]]
    said = "Toma losartán 50 mg en la mañana y amlodipino 10 mg en la noche."   # word for word, once in the text
    items = [{ of = "losartan", action = "take", number = "50 mg", frequency = "morning" },
             { of = "amlodipine", action = "take", number = "10 mg", frequency = "night" }]

An item is what something said is of (`of`, with any other English names in `aka`): a drug, a vital sign or result
(VITALS), a finding, a part of the body or a habit, as the English names it; and what is said of it, each a string or
a list of them, written as English writes them: number (with its unit: "50 mg", "152/94", "8.4%", a dose's number
being the drug's), frequency (FREQUENCIES' keys: "daily", "every 8 hours", "as needed", "night", "fasting"...),
duration ("10 years"), date ("2019", "March 3, 2026", "today"), route, side ("left", "right", "both"), severity,
polarity (POLARITY's keys: "high", "uncontrolled", "better"...; a hyper- or hypo- word in `of` is its own), action
(ACTIONS' keys, in a past form, "stopped", "took", when it was said in the past), negated (true for a finding said to
be absent, false for one said to be present), doubt (true when it is said with doubt: "posible", "probable",
"compatible con", "descartar"), history (true when it is the patient's past), who (the relative who has it). An entry
whose passage holds a number written so that it reads two ways ("5.000") also says refuse = "ambiguous_number": the
checks can't know which reading was said and leave its field for the clinician while the transcript writes it so. Its
items say what was said ("cinco mil unidades", 5000 units), and a statement shown from it is compared with them as any
other, however the transcript writes the number; scoring_es.py lists every one shown (ambiguous_offered).

The reading of a statement's English (claims()): it is cut into clauses at commas, semicolons, full stops and "and",
"or", "but", "then"; each name of an item (the script's, VITALS', the drug list's) is found in it; each number with
its unit, frequency, route, side, severity, polarity, action, doubt, history and relative is tied to the nearest name
in its clause (or to none); a negation ("no", "not", "denies", "without", "negative for") reaches every name after it
up to the end of the sentence, a semicolon, "but" or "with", and "ruled out" or "excluded" the names before it in its
clause. Four readings it doesn't make (each was a misreading of the scorer's, step 17): an age said in words ("34
years old") is the age's number, as "34-year-old" is, not how long something lasted, and nothing but its number is said
of an age; a weight lifted, carried or borne ("avoid lifting weight") is a load, not the vital sign; an increase or
decrease after "has", "was" and the like, in a clause naming no drug, dose or number ("the swelling has decreased"), is
a trend, not a medication change; and a doubt before a cause ("probably due to", CAUSES) is the doubt of that link,
said of neither thing it links, with or without a comma before it, so it answers to either one's doubt in the facts and
is added only if neither has one; and "unlikely", "may be" and "could be" are doubt, as "possible" and "might" are
(DOUBT; "unlikely" only doubt, not a negation, so "probable" given as "unlikely" isn't told apart). Then (compare()), against the items of the entries whose passages the statement cites, it is changed when it
says a number, unit, frequency, duration, date, route, side, severity, polarity, drug or abbreviation those items
don't say of what it says it of (a value of another item is changed too: "glucose 140" as "BP 140"), and when it says
an action, negation, doubt, past or relative other than theirs, or leaves out one they say of something it names.
What it may leave out: any number, frequency and the like, as the checks allow. What it doesn't read: the meaning of
any other word.
"""

from __future__ import annotations

import difflib
import re
import unicodedata
from dataclasses import dataclass
from decimal import Decimal, InvalidOperation

# ---- What the English says, in closed lists ---------------------------------------------------------------------

# Vital signs, results and scores, by the name the facts use, with the names English gives them.
VITALS = {
    "BP": ["bp", "blood pressure", "ta"],
    "HR": ["hr", "heart rate", "pulse"],
    "RR": ["rr", "respiratory rate"],
    "temperature": ["temperature", "temp"],
    "SpO2": ["spo2", "sao2", "sato2", "o2 sat", "oxygen saturation", "saturation"],
    "weight": ["weight", "weighs"],
    "height": ["height", "length"],
    "A1c": ["a1c", "hba1c", "hemoglobin a1c", "glycated hemoglobin", "glycosylated hemoglobin"],
    "glucose": ["glucose", "blood sugar", "sugar"],
    "LDL": ["ldl"],
    "HDL": ["hdl"],
    "cholesterol": ["cholesterol"],
    "TSH": ["tsh"],
    "INR": ["inr"],
    "GFR": ["gfr", "egfr", "filtration rate", "glomerular filtration rate"],
    "creatinine": ["creatinine"],
    "potassium": ["potassium"],
    "sodium": ["sodium"],
    "LVEF": ["lvef", "ejection fraction"],
    "PHQ-9": ["phq-9", "phq9"],
    "age": ["year-old", "years old", "month-old", "months old", "age", "aged"],
    "smoking": ["smoking", "smoke", "smokes", "smoked", "smoker", "tobacco", "cigarettes"],
    "alcohol": ["alcohol", "drink", "drinks", "drinking"],
}
# Words that may follow a number, and how they are written in a value.
UNITS = {
    "mg": "mg", "milligram": "mg", "milligrams": "mg", "mcg": "mcg", "μg": "mcg", "microgram": "mcg",
    "micrograms": "mcg", "g": "g", "gram": "g", "grams": "g", "kg": "kg", "kilo": "kg", "kilos": "kg",
    "kilogram": "kg", "kilograms": "kg", "lb": "lb", "lbs": "lb", "pound": "lb", "pounds": "lb", "oz": "oz",
    "ml": "ml", "milliliter": "ml", "milliliters": "ml", "cc": "ml", "l": "l", "liter": "l", "liters": "l",
    "cm": "cm", "mm": "mm", "%": "%", "percent": "%", "degree": "degrees", "degrees": "degrees", "°": "degrees",
    "°c": "degrees", "celsius": "degrees", "°f": "°F", "fahrenheit": "°F", "mmhg": "mmHg", "bpm": "bpm",
    "unit": "units", "units": "units", "iu": "units", "puff": "puffs", "puffs": "puffs", "tablet": "tablets",
    "tablets": "tablets", "tab": "tablets", "tabs": "tablets", "pill": "tablets", "pills": "tablets",
    "capsule": "capsules", "capsules": "capsules", "drop": "drops", "drops": "drops", "mg/dl": "mg/dL",
    "mmol/l": "mmol/L", "meq/l": "mEq/L", "pack": "packs", "packs": "packs", "breaths": "breaths",
}
# A unit of measurement English may add to a number said without one; any other is a change.
MEASUREMENT = {"mmHg", "bpm", "degrees", "%", "breaths"}
PERIODS = {"minute": "minutes", "minutes": "minutes", "min": "minutes", "mins": "minutes", "hour": "hours",
           "hours": "hours", "hr": "hours", "hrs": "hours", "day": "days", "days": "days", "week": "weeks",
           "weeks": "weeks", "wk": "weeks", "month": "months", "months": "months", "year": "years", "years": "years"}
NUMBER_WORDS = {"one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7, "eight": 8, "nine": 9,
                "ten": 10, "eleven": 11, "twelve": 12, "fifteen": 15, "twenty": 20, "thirty": 30, "forty": 40,
                "fifty": 50, "hundred": 100}
FRACTIONS = {"half": "0.5", "a half": "0.5", "a quarter": "0.25", "one quarter": "0.25", "quarter": "0.25",
             "three quarters": "0.75"}
# How often, by key, with the phrases English says it in ("N" is any number, N times a day being "N times daily").
FREQUENCIES = {
    "daily": ["once a day", "once daily", "once per day", "daily", "a day", "per day", "every day", "each day", "qd"],
    "twice daily": ["twice a day", "twice daily", "twice per day", "two times a day", "bid"],
    "N times daily": ["N times a day", "N times daily", "N times per day"],
    "every N hours": ["every N hours", "every N hour", "qNh"],
    "weekly": ["once a week", "weekly", "every week", "a week", "per week"],
    "twice weekly": ["twice a week", "two times a week"],
    "N times weekly": ["N times a week", "N times per week"],
    "monthly": ["once a month", "monthly", "every month"],
    "every other day": ["every other day", "on alternate days", "alternate days", "every second day"],
    "every third day": ["every third day", "every 3 days", "every three days"],
    "as needed": ["as needed", "if needed", "when needed", "prn", "if necessary", "when necessary", "as required",
                  "when he needs it", "when she needs it", "as necessary"],
    "morning": ["in the morning", "in the mornings", "every morning", "mornings"],
    "afternoon": ["in the afternoon", "in the afternoons", "every afternoon"],
    "night": ["at night", "nightly", "every night", "in the evening", "in the evenings", "at bedtime", "bedtime"],
    "fasting": ["fasting", "on an empty stomach", "empty stomach"],
    "before meals": ["before meals", "before breakfast", "before eating", "before food"],
    "with food": ["with food", "with meals", "with a meal", "with breakfast"],
    "after meals": ["after meals", "after breakfast", "after eating", "after food", "after a meal"],
}
ROUTES = {"oral": ["oral", "orally", "by mouth", "po", "p.o."], "inhaled": ["inhaled", "inhalation", "nebulized"],
          "intravenous": ["intravenous", "intravenously", "iv", "iv push", "i.v."],
          "intramuscular": ["intramuscular", "intramuscularly", "im"],
          "subcutaneous": ["subcutaneous", "subcutaneously", "subcut", "sc", "sq"],
          "topical": ["topical", "topically"], "sublingual": ["sublingual", "sublingually", "sl"],
          "intrathecal": ["intrathecal", "intrathecally"], "rectal": ["rectal", "rectally"],
          "nasal": ["nasal", "intranasal", "intranasally"], "transdermal": ["transdermal"]}
SIDES = {"left": ["left", "lt"], "right": ["right", "rt"], "both": ["both", "bilateral", "bilaterally"]}
SEVERITY = {"mild": ["mild", "mildly", "slight", "slightly"], "moderate": ["moderate", "moderately"],
            "severe": ["severe", "severely", "intense", "marked"]}
POLARITY = {"high": ["high", "elevated", "raised"], "low": ["low", "decreased", "reduced", "diminished"],
            "normal": ["normal"], "abnormal": ["abnormal"],
            "better": ["better", "improved", "improvement", "improving"],
            "worse": ["worse", "worsening", "worsened", "deterioration"],
            "controlled": ["controlled", "well controlled", "well-controlled"],
            "uncontrolled": ["uncontrolled", "not controlled", "poorly controlled", "badly controlled"],
            "positive": ["positive"], "negative": ["negative"], "allergic": ["allergic", "allergy"],
            "tolerates": ["tolerates", "tolerated", "tolerate"]}
POLAR_PREFIXES = ("hyper", "hypo", "tachy", "brady")
# Medication changes: each form, its key, and whether it is past.
ACTIONS = {
    "start": (["start", "starts", "starting", "begin", "begins", "initiate", "initiates", "add", "adds", "adding"],
              ["started", "began", "initiated", "added"]),
    "stop": (["stop", "stops", "stopping", "discontinue", "discontinues", "suspend", "cease", "quits"],
             ["stopped", "discontinued", "suspended", "ceased", "quit"]),
    "hold": (["hold", "holds", "omit", "skip"], ["held", "omitted", "skipped"]),
    "continue": (["continue", "continues", "continuing", "keep", "keeps", "maintain", "maintains"],
                 ["continued", "kept", "maintained"]),
    "increase": (["increase", "increases", "raise", "uptitrate"], ["increased"]),
    "decrease": (["decrease", "decreases", "reduce", "reduces", "lower", "lowers", "taper"],
                 ["decreased", "reduced", "lowered", "tapered"]),
    "switch": (["switch", "switches", "switching", "change", "replace"], ["switched", "changed", "replaced"]),
    "take": (["take", "takes", "taking", "use", "uses", "using", "receive", "receives", "receiving"],
             ["took", "taken", "used", "received"]),
}
NEGATIONS = ["no", "not", "denies", "deny", "denied", "without", "negative for", "never", "none", "nor", "neither",
             "free of", "absence of", "absent"]
AFTER_NEGATIONS = ["ruled out", "was ruled out", "were ruled out", "excluded", "was excluded"]
NEGATION_ENDS = {"but", "however", "although", "though", "except", "with", ";", "."}
# "unlikely", "may be" and "could be" are doubt too (the owner's ruling of 2026-11-24), "may" only before "be": "May 3"
# is a date and "may take" leave to do something. "unlikely" is read as a doubt and nothing more, so "probable"
# given as "unlikely" isn't counted (both are doubt).
DOUBT = ["possible", "possibly", "probable", "probably", "likely", "unlikely", "suspected", "suspect", "suspicion of",
         "maybe", "perhaps", "might", "may be", "could be", "rule out", "r/o", "questionable", "consistent with",
         "compatible with", "suggestive of", "suggests", "concerning for", "i think", "presumed", "cannot exclude",
         "can't exclude"]
HISTORY = ["history of", "previous", "previously", "prior", "past", "old", "remote", "formerly", "used to",
           "in the past", "last year"]
PRESENT = ["acute", "current", "currently", "active", "ongoing", "new"]
RELATIVES = ["mother", "father", "brother", "sister", "son", "daughter", "husband", "wife", "grandmother",
             "grandfather", "aunt", "uncle", "cousin", "parent", "parents", "sibling"]
HAVING = {"has", "had", "have", "with", "died", "diagnosed", "suffers", "suffered", "'s"}
DATES = ["today", "yesterday", "tomorrow", "tonight", "last night", "this morning", "last week", "last month",
         "next week", "next month", "now", "immediately", "stat"]
MONTHS = ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october",
          "november", "december"]
# Clinical abbreviations English writes, in any case: each must name an item the Spanish cited says.
ABBREVIATIONS = {"bp", "hr", "rr", "spo2", "sao2", "copd", "hiv", "aids", "nsaid", "nsaids", "mi", "cbc", "ldl",
                 "hdl", "tsh", "inr", "gfr", "egfr", "lvef", "ef", "phq-9", "ecg", "ekg", "ct", "mri", "covid",
                 "covid-19", "a1c", "hba1c", "dm", "htn", "chf", "uti", "uri", "dvt", "pe", "cva", "tia", "gerd",
                 "bph", "ckd", "aki", "afib", "cad", "ards", "ssri", "ace", "arb", "ppi", "ivc"}
CLAUSE_WORDS = {"and", "or", "but", "then"}
# A doubt before one of these is the doubt of a cause ("Urticaria, probably due to amoxicillin"): of the link between what
# was named and what caused it, which a fact list may give either of them, comma or none.
CAUSES = [["due", "to"], ["secondary", "to"], ["caused", "by"], ["related", "to"], ["because", "of"],
          ["attributed", "to"], ["from"]]
# A weight lifted, carried or borne is a load, not the vital sign ("avoid lifting weight").
LOADS = {"lift", "lifts", "lifting", "lifted", "carry", "carries", "carrying", "carried", "bear", "bears", "bearing",
         "heavy"}
# Before an increase or decrease said of something already named ("the swelling has decreased"), a finding's trend.
PERFECT_OR_PASSIVE = {"has", "have", "had", "been", "is", "are", "was", "were"}
DOSE_WORDS = {"dose", "doses", "dosage", "dosing"}

TOKEN = re.compile(r"\d+(?:[.,]\d+)*(?:/\d+(?:[.,]\d+)?)?|°[cf]?|%|[a-zμ][a-z0-9μ]*(?:[-'/.][a-z0-9]+)*|[,;:.()]")


# ---- The facts ------------------------------------------------------------------------------------------------------

KINDS = ("number", "frequency", "duration", "date", "route", "side", "severity", "polarity", "action")
VALUES = ("drug", *KINDS)  # every kind of value a statement says of something
FLAGS = ("negated", "doubt", "history", "who")
ITEM_KEYS = {"of", "aka", *KINDS, *FLAGS}


class FactsError(ValueError):
    """A fact list that doesn't follow the format (the module's header)."""


@dataclass(frozen=True)
class Item:
    of: str
    names: tuple[str, ...]  # its names, folded (fold())
    values: dict[str, frozenset[str]]  # kind: what is said of it, as claims() writes it
    negated: bool | None
    doubt: bool
    history: bool
    who: str | None


@dataclass(frozen=True)
class Entry:
    said: str
    items: tuple[Item, ...]
    refuse: str | None


def entries(facts: list[dict] | tuple[dict, ...]) -> list[Entry]:
    """A script's fact list, checked against the format."""
    found = []
    for fact in facts:
        if not isinstance(fact, dict) or not set(fact) <= {"said", "items", "refuse"} or not isinstance(
                fact.get("said"), str) or not fact["said"].strip():
            raise FactsError(f"a fact needs said (a passage of the text) and items or refuse: {fact!r}")
        items = []
        for raw in fact.get("items", []):
            if not isinstance(raw, dict) or not isinstance(raw.get("of"), str) or not set(raw) <= ITEM_KEYS:
                raise FactsError(f"an item needs of, and only {sorted(ITEM_KEYS)}: {raw!r}")
            items.append(_item(raw))
        if not items and not fact.get("refuse"):
            raise FactsError(f"{fact['said']!r} says nothing: give items, or refuse")
        found.append(Entry(fact["said"], tuple(items), fact.get("refuse")))
    return found


def _strings(value: object) -> list[str]:
    if isinstance(value, str):
        return [value]
    if isinstance(value, list) and all(isinstance(v, str) for v in value):
        return value
    raise FactsError(f"{value!r} isn't a string or a list of strings")


def _item(raw: dict) -> Item:
    names = [raw["of"], *_strings(raw.get("aka", []))]
    values: dict[str, set[str]] = {}
    for kind in KINDS:
        for said in _strings(raw.get(kind, [])):
            read = _read_value(kind, said)
            if read is None:
                raise FactsError(f"{raw['of']}: {kind} {said!r} isn't one the reading knows")
            values.setdefault(kind, set()).add(read)
    names_folded = tuple(" ".join(fold(t) for t in words(name)) for name in names)
    canonical = next((label for label, aliases in VITALS.items() if raw["of"] == label or raw["of"].lower() in aliases),
                     None)
    if canonical:
        names_folded += tuple(" ".join(fold(t) for t in words(alias)) for alias in [canonical, *VITALS[canonical]])
    for name in names:
        if _polar_word(name.lower()) is not None:
            values.setdefault("polarity", set()).add(name.lower())
    return Item(raw["of"], names_folded, {k: frozenset(v) for k, v in values.items()}, raw.get("negated"),
                bool(raw.get("doubt")), bool(raw.get("history")), raw.get("who"))


def _read_value(kind: str, said: str) -> str | None:
    """A value as the facts write it, as claims() writes what the English says: the key of an action, how often, a
    route, side, severity or polarity; a number, duration or date read as the English is."""
    if kind == "action":
        for action, (_, past) in ACTIONS.items():
            if said == action:
                return action
            if said in past:
                return f"{action}, past"
        return None
    if kind == "frequency":
        known = said in FREQUENCIES or any("N" in key and re.fullmatch(key.replace("N", r"\d+"), said) for key in FREQUENCIES)
        return said if known else None
    if kind in ("route", "side", "severity", "polarity"):
        return said if said in {"route": ROUTES, "side": SIDES, "severity": SEVERITY, "polarity": POLARITY}[kind] else None
    read = [claim.value for claim in claims(said, Lexicon()) if claim.kind == kind or (kind, claim.kind) == (
        "number", "duration")]
    return read[0] if len(read) == 1 else None


# ---- Reading the English --------------------------------------------------------------------------------------------


def words(text: str) -> list[str]:
    return [t for t in TOKEN.findall(text.lower()) if t not in ",;:.()"]


def fold(word: str) -> str:
    """A word without a plural's s, so "knees" is "knee"."""
    return word[:-1] if len(word) > 3 and word.endswith("s") and not word.endswith("ss") else word


@dataclass(frozen=True)
class Claim:
    kind: str  # one of KINDS, or drug, abbreviation, negated, doubt, history, present, who
    label: str | None  # the item it is said of, by the facts' name, or None
    value: str


@dataclass
class Lexicon:
    """The names the reading knows: the script's items, VITALS, and the drug list."""

    items: tuple[Item, ...] = ()
    drugs: frozenset[str] = frozenset()

    def names(self) -> list[tuple[tuple[str, ...], str, bool]]:
        """Each name as folded words, the label it names (the script's own first), and whether it is a drug; longest
        first."""
        found: dict[tuple[str, ...], tuple[str, bool]] = {}
        for item in self.items:
            for name in item.names:
                found.setdefault(tuple(name.split()), (item.of, item.of.lower() in self.drugs))
        for label, aliases in VITALS.items():
            for alias in [label.lower(), *aliases]:
                found.setdefault(tuple(fold(t) for t in words(alias)), (label, False))
        for drug in self.drugs:
            found.setdefault(tuple(fold(t) for t in words(drug)), (drug, True))
        return sorted(((name, label, drug) for name, (label, drug) in found.items()), key=lambda n: -len(n[0]))


def _tokens(text: str) -> list[str]:
    return TOKEN.findall(unicodedata.normalize("NFC", text).lower())


def _phrases(table: dict[str, list[str]]) -> list[tuple[list[str], str]]:
    return sorted(((_tokens(phrase), key) for key, phrases in table.items() for phrase in phrases),
                  key=lambda found: -len(found[0]))


def _match(toks: list[str], at: int, phrase: list[str]) -> bool:
    if at + len(phrase) > len(toks):
        return False
    for want, got in zip(phrase, toks[at : at + len(phrase)], strict=True):
        if want == "n":  # FREQUENCIES' N, lower case as every phrase is read
            if not _is_number(got):
                return False
        elif got != want:
            return False
    return True


def _quantity(toks: list[str], at: int) -> tuple[str, int] | None:
    """A number starting at toks[at], and where it ends: digits; a number word or "a" before what it counts; a
    fraction."""
    for phrase, value in sorted(FRACTIONS.items(), key=lambda fraction: -len(fraction[0])):
        if _match(toks, at, _tokens(phrase)):
            return value, at + len(_tokens(phrase))
    token = toks[at]
    counted = at + 1 < len(toks) and (toks[at + 1] in UNITS or toks[at + 1].split("-")[0] in PERIODS or toks[
        at + 1 : at + 4] == ["and", "a", "half"] or toks[at + 1] in ("times", "year-old", "month-old"))
    if re.fullmatch(r"\d+(?:[.,]\d+)*(?:/\d+(?:[.,]\d+)?)?", token) or (token in NUMBER_WORDS and counted):
        return _number(token), at + 1
    if token in ("a", "an") and counted and toks[at + 1] not in ("times",):
        return "1", at + 1
    return None


def _is_number(token: str) -> bool:
    return bool(re.fullmatch(r"\d+(?:[.,]\d+)*(?:/\d+(?:[.,]\d+)?)?", token)) or token in NUMBER_WORDS


def _number(token: str) -> str:
    if token in NUMBER_WORDS:
        return str(NUMBER_WORDS[token])
    if re.fullmatch(r"\d{1,3}(?:,\d{3})+", token):  # 1,000: English's thousands
        token = token.replace(",", "")
    token = token.replace(",", ".")
    if "/" in token:
        return "/".join(_number(part) for part in token.split("/"))
    try:
        value = Decimal(token)
    except InvalidOperation:
        return token
    text = format(value.normalize(), "f")
    return text


def _period(value: str, unit: str) -> str:
    return f"{value} {PERIODS[unit].removesuffix('s') if value == '1' else PERIODS[unit]}"


def _polar_word(word: str) -> str | None:
    for prefix in POLAR_PREFIXES:
        if word.startswith(prefix) and len(word) > len(prefix) + 3:
            return f"{prefix}{word[len(prefix):][:4]}"
    return None


def claims(english: str, lexicon: Lexicon) -> list[Claim]:
    """What the English says, each tied to the item it is said of (the module's header)."""
    toks = _tokens(english)
    kinds: list[tuple[int, int, str, str]] = []  # start, end, kind, value
    taken = [False] * len(toks)

    def take(start: int, end: int, kind: str, value: str) -> None:
        kinds.append((start, end, kind, value))
        for i in range(start, end):
            taken[i] = True

    # Names first, longest first: an item, a vital sign, a drug.
    folded = [fold(t) for t in toks]
    labels: list[tuple[int, int, str, bool]] = []
    for name, label, drug in lexicon.names():
        for i in range(len(toks) - len(name) + 1):
            if label == "weight" and i and toks[i - 1] in LOADS:
                continue
            if tuple(folded[i : i + len(name)]) == name and not any(taken[i : i + len(name)]):
                labels.append((i, i + len(name), label, drug))
                for j in range(i, i + len(name)):
                    taken[j] = True
    names_at = {i for start, end, _, _ in labels for i in range(start, end)}

    def free(start: int, end: int) -> bool:
        return not any(taken[start:end])

    i = 0
    while i < len(toks):
        if taken[i]:
            i += 1
            continue
        for phrase, key in _phrases(FREQUENCIES):
            if _match(toks, i, phrase) and free(i, i + len(phrase)):
                number = next((_number(t) for t, w in zip(toks[i:], phrase, strict=False) if w == "n"), None)
                take(i, i + len(phrase), "frequency", key.replace("N", number) if number else key)
                break
        i += 1
    for phrase, key in _phrases({"__": DATES}):
        for i in range(len(toks) - len(phrase) + 1):
            if _match(toks, i, phrase) and free(i, i + len(phrase)):
                take(i, i + len(phrase), "date", " ".join(phrase))
    for i, tok in enumerate(toks):
        if taken[i]:
            continue
        if tok in MONTHS and i + 1 < len(toks) and re.fullmatch(r"\d{1,2}", toks[i + 1]):
            end = i + 2
            date = f"{tok} {toks[i + 1]}"
            if end + 1 < len(toks) and toks[end] == "," and re.fullmatch(r"\d{4}", toks[end + 1]):
                date, end = f"{date}, {toks[end + 1]}", end + 2
            take(i, end, "date", date)
        elif re.fullmatch(r"(19|20)\d\d", tok) and not (i + 1 < len(toks) and toks[i + 1] in UNITS):
            take(i, i + 1, "date", tok)
    # Numbers: in digits, in words before what they count, a fraction ("half", "a quarter of a tablet"), or "a" before
    # a unit; then "and a half", "out of N", and their unit or period.
    i = 0
    while i < len(toks):
        quantity = None if taken[i] else _quantity(toks, i)
        if quantity is None:
            i += 1
            continue
        value, end = quantity
        if toks[end : end + 3] == ["and", "a", "half"]:
            value, end = _number(str(Decimal(value) + Decimal("0.5"))), end + 3
        if toks[end : end + 2] == ["out", "of"] and end + 2 < len(toks) and _is_number(toks[end + 2]):
            value, end = f"{value}/{_number(toks[end + 2])}", end + 3
        if value in FRACTIONS.values() and toks[end : end + 1] in (["of"], ["a"], ["an"]):
            # "a quarter of a tablet", "half a tablet"
            end += 2 if toks[end] == "of" and toks[end + 1 : end + 2] in (["a"], ["an"]) else 1
        unit = toks[end] if end < len(toks) else ""
        if unit in ("year-old", "month-old"):
            take(i, end + 1, "number", f"{value}{' months' if unit == 'month-old' else ''}")
            labels.append((end, end + 1, "age", False))
        elif (period := unit.split("-")[0]) in PERIODS and PERIODS[period] in ("years", "months") and toks[
                end + 1 : end + 2] == ["old"]:  # "34 years old", an age as "34-year-old" is, not how long something lasted
            take(i, end + 2, "number", f"{value}{' months' if PERIODS[period] == 'months' else ''}")
        elif period in PERIODS:  # "6 weeks", "18-month vaccines"
            take(i, end + 1, "duration", _period(value, period))
        elif unit in UNITS:
            if toks[end + 1 : end + 4] == ["and", "a", "half"]:  # "a tablet and a half"
                value, end = _number(str(Decimal(value) + Decimal("0.5"))), end + 3
            if "/" in value and Decimal(value.split("/")[0]) < Decimal(value.split("/")[1]):  # "1/2 tablet" is a half
                value = _number(str(Decimal(value.split("/")[0]) / Decimal(value.split("/")[1])))
            take(i, end + 1, "number", f"{value}{'' if UNITS[unit] == '%' else ' '}{UNITS[unit]}")
        elif toks[end : end + 2] == ["per", "cent"]:
            take(i, end + 2, "number", f"{value}%")
        else:
            take(i, end, "number", value)
        i += 1
    # Words of a class.
    for kind, table in (("route", ROUTES), ("side", SIDES), ("severity", SEVERITY)):
        for phrase, key in _phrases(table):
            for i in range(len(toks) - len(phrase) + 1):
                if _match(toks, i, phrase) and free(i, i + len(phrase)):
                    take(i, i + len(phrase), kind, key)
    for phrase, key in _phrases(POLARITY):
        for i in range(len(toks) - len(phrase) + 1):
            if _match(toks, i, phrase) and free(i, i + len(phrase)):
                past = any(toks[i] in forms[1] for forms in ACTIONS.values())
                if past and not (i + len(phrase) < len(toks) and i + len(phrase) in names_at):
                    continue  # "decreased to 2.5 mg" is a medication change; "decreased sensation" isn't
                take(i, i + len(phrase), "polarity", key)
    for i, tok in enumerate(toks):
        if free(i, i + 1) and i not in names_at and _polar_word(tok) is not None:
            take(i, i + 1, "polarity", tok)
    clause_of = _clauses(toks)
    drugs_at = {clause_of[start] for start, _, _, drug in labels if drug}
    numbers_at = {clause_of[start] for start, _, kind, _ in kinds if kind == "number"}
    for i, tok in enumerate(toks):
        if not free(i, i + 1):
            continue
        if (tok in ACTIONS["increase"][0] + ACTIONS["increase"][1] + ACTIONS["decrease"][0] + ACTIONS["decrease"][1]
                and i and toks[i - 1] in PERFECT_OR_PASSIVE and clause_of[i] not in drugs_at | numbers_at
                and not any(t in DOSE_WORDS for t, c in zip(toks, clause_of, strict=True) if c == clause_of[i])):
            continue  # "the swelling has decreased": a trend, where no drug or dose is named, not a medication change
        if tok in ("use", "uses") and i - 1 in names_at:
            continue  # "alcohol use" names a habit; "uses albuterol" is taking it
        if tok in ACTIONS["take"][0] and i and any(toks[i - 1] in forms for pair in ACTIONS.values() for forms in pair):
            continue  # "stopped taking", "started using": the first verb says what changed
        for action, (present, past) in ACTIONS.items():
            if tok in present or tok in past:
                take(i, i + 1, "action", action if tok in present else f"{action}, past")
                break
    for kind, phrases in (("doubt", DOUBT), ("history", HISTORY), ("present", PRESENT)):
        for phrase in sorted((_tokens(p) for p in phrases), key=lambda p: -len(p)):
            for i in range(len(toks) - len(phrase) + 1):
                if _match(toks, i, phrase) and free(i, i + len(phrase)):
                    take(i, i + len(phrase), kind, "yes")
    for i, tok in enumerate(toks):
        # A relative who has something ("her mother has"), or has what was named before ("colon cancer in her father"),
        # not one told or given something ("advice given to the mother").
        told = i and toks[i - 1] in ("to", "the") and (i < 2 or toks[i - 2] == "to")
        if tok not in RELATIVES or not free(i, i + 1) or told:
            continue
        if any(t in HAVING for t in toks[i + 1 : i + 3]):
            take(i, i + 1, "who", tok)
        elif toks[max(0, i - 2) : i] in (["in", "his"], ["in", "her"]):
            take(i, i + 1, "who of what was named before", tok)
    for i, tok in enumerate(toks):
        if free(i, i + 1) and tok in ABBREVIATIONS:
            take(i, i + 1, "abbreviation", tok)

    # Each claim tied to the nearest name in its clause.
    found: list[Claim] = []
    for start, end, label, drug in labels:
        if drug:
            found.append(Claim("drug", label, label.lower()))
        found.append(Claim("named", label, label))
    for start, end, kind, value in kinds:
        if kind == "abbreviation":
            found.append(Claim(kind, None, value))
            continue
        # The nearest name in the clause; at the same distance, the one after for what English says before its noun
        # (a medication change, a side, severity, polarity, doubt, the past, a relative), the one before otherwise.
        after_first = kind in ("action", "side", "severity", "polarity", "doubt", "history", "present", "who")
        before_only = kind == "who of what was named before"
        # An age has nothing said of it but its number ("33-year-old woman who started with spots 2 days ago").
        near = [(start - (le - 1) if le <= start else s - (end - 1), (s < end) == after_first, label)
                for s, le, label, _ in labels if clause_of[s] == clause_of[start] and (s < start or not before_only)
                and (label != "age" or kind == "number")]
        label = min(near)[2] if near else None
        if kind == "doubt" and any(_match(toks, end, cause) for cause in CAUSES):
            label = None  # the doubt of a cause (CAUSES): satisfied by either thing it links, added if neither has one
        found.append(Claim("who" if before_only else kind, label, value))
    found += _negations(toks, labels, clause_of)
    return found


def _clauses(toks: list[str]) -> list[int]:
    """Each token's clause: cut at commas, semicolons, full stops, brackets and CLAUSE_WORDS."""
    clause_of, clause = [], 0
    for tok in toks:
        if tok in (",", ";", ".", "(", ")") or tok in CLAUSE_WORDS:
            clause += 1
        clause_of.append(clause)
    return clause_of


def _negations(toks: list[str], labels: list[tuple[int, int, str, bool]], clause_of: list[int]) -> list[Claim]:
    """Each name a negation reaches (the module's header), and each it doesn't, as negated true or false."""
    cues = []
    for phrase in sorted((_tokens(p) for p in NEGATIONS), key=lambda p: -len(p)):
        for i in range(len(toks) - len(phrase) + 1):
            if _match(toks, i, phrase) and not any(start <= i < end for start, end in cues):
                polar = phrase == ["not"] and i + 1 < len(toks) and any(
                    _match(toks, i, _tokens(p)) for p in POLARITY["uncontrolled"])
                if not polar:
                    cues.append((i, i + len(phrase)))
    after = []
    for phrase in sorted((_tokens(p) for p in AFTER_NEGATIONS), key=lambda p: -len(p)):
        for i in range(len(toks) - len(phrase) + 1):
            if _match(toks, i, phrase) and not any(start <= i < end for start, end in after):
                after.append((i, i + len(phrase)))
    found = []
    for start, end, label, _ in labels:
        reached = False
        for cue_start, cue_end in cues:
            if cue_end <= start and not any(toks[j] in NEGATION_ENDS for j in range(cue_end, start)):
                reached = True
        for cue_start, _ in after:
            if cue_start >= end and clause_of[cue_start] == clause_of[start]:
                reached = True
        found.append(Claim("negated", label, "yes" if reached else "no"))
    return found


# ---- Comparing ------------------------------------------------------------------------------------------------------


def negated(english: str, phrase: str) -> list[bool]:
    """For each place the English says the phrase, whether a negation reaches it there (claims())."""
    item = Item(phrase, (" ".join(fold(t) for t in words(phrase)),), {}, None, False, False, None)
    return [claim.value == "yes" for claim in claims(english, Lexicon((item,)))
            if claim.kind == "negated" and claim.label == phrase]


def compare(english: str, cited: list[Entry], script: list[Entry],
            lexicon: Lexicon) -> tuple[list[tuple[str, str, bool]], list[Claim]]:
    """What the English changes from the items of the entries it cites, each (kind, what, said elsewhere in the
    script); and every value it says that they back (VALUES), which a misfiling is told by."""
    items = [item for entry in cited for item in entry.items]
    everywhere = [item for entry in script for item in entry.items]
    said = claims(english, lexicon)
    named = {claim.label for claim in said if claim.kind == "named"}
    changed: list[tuple[str, str, bool]] = []
    backed_claims: list[Claim] = []

    def of(label: str | None, pool: list[Item]) -> list[Item]:
        return [item for item in pool if item.of == label] if label else pool

    def elsewhere(kind: str, value: str) -> bool:
        return any(value in item.values.get(kind, ()) for item in everywhere)

    for claim in said:
        if claim.kind in ("named", "negated", "who", "doubt", "history", "present"):
            continue
        if claim.kind == "drug":
            if not of(claim.label, items):
                changed.append(("drug", claim.value, bool(of(claim.label, everywhere))))
            else:
                backed_claims.append(claim)
            continue
        if claim.kind == "abbreviation":
            spelled = claim.value.removesuffix("s")
            if not any(spelled in name.split() or claim.value in name.split() for item in items for name in item.names):
                changed.append(("abbreviation", claim.value.upper(), any(
                    spelled in name.split() for item in everywhere for name in item.names)))
            continue
        kinds = ("number", "duration") if claim.kind in ("number", "duration") else (claim.kind,)
        # Said of an item the Spanish cited: that item's. Of a vital sign, a result or a drug it didn't: nothing (a
        # value moved to another label is changed). Of anything else, or of nothing: any item's it cited.
        strict = claim.label in VITALS or (claim.label or "").lower() in lexicon.drugs
        own = of(claim.label, items) if claim.label in {item.of for item in items} and claim.kind != "date" else (
            [] if strict and claim.kind != "date" else items)
        backed = {value for item in own for kind in kinds for value in item.values.get(kind, ())}
        if claim.kind == "action":
            backed |= {value.removesuffix(", past") for value in backed}  # a present form for one said in the past
            if claim.value in backed or (claim.value.endswith(", past") and claim.value.removesuffix(", past") in {
                    value for item in own for value in item.values.get("action", ())
                    if not value.endswith(", past")}):
                backed_claims.append(claim)
                continue
        elif claim.value in backed or _added_measurement(claim.value, backed) or (
                claim.kind == "polarity" and _polar_word(claim.value) is not None
                and _polar_word(claim.value) in {_polar_word(value) for value in backed}):
            backed_claims.append(claim)
            continue
        if claim.kind == "number":
            bare = claim.value.split()[0].rstrip("%")
            kind = "unit" if any(value.split()[0].rstrip("%") == bare for value in backed) else "number"
        else:
            kind = claim.kind
        where = f" ({claim.label})" if claim.label else ""
        changed.append((kind, f"{claim.value}{where}", elsewhere(claim.kind, claim.value)))

    for item in items:
        if item.of not in named:
            continue
        own = [claim for claim in said if claim.label in (item.of, None)]
        actions = {claim.value for claim in own if claim.kind == "action"}
        for wanted in item.values.get("action", ()):
            if wanted not in actions and not (not wanted.endswith(", past") and f"{wanted}, past" in actions):
                changed.append(("action", f"{wanted} ({item.of}) left out", False))
        negated = [claim.value == "yes" for claim in said if claim.kind == "negated" and claim.label == item.of]
        if item.negated is not None and negated and item.negated not in negated:
            changed.append(("negation", f"{item.of} {'not ' if item.negated else ''}negated", False))
        doubt = any(claim.kind == "doubt" for claim in own)
        if item.doubt and not doubt:
            changed.append(("certainty", f"{item.of} said with doubt, shown without", False))
        if not item.doubt and any(claim.kind == "doubt" and claim.label == item.of for claim in said):
            changed.append(("certainty", f"{item.of} shown with doubt", False))
        if item.history and any(claim.kind == "present" and claim.label == item.of for claim in said):
            changed.append(("history", f"{item.of} in the past, shown as now", False))
        if not item.history and any(claim.kind == "history" and claim.label == item.of for claim in said):
            changed.append(("history", f"{item.of} shown as in the past", False))
        relatives = {claim.value for claim in said if claim.kind == "who" and claim.label in (item.of, None)}
        if item.who and not relatives:
            changed.append(("experiencer", f"{item.of}: {item.who} left out", False))
        elif relatives and relatives != ({item.who} if item.who else set()):
            changed.append(("relative", f"{item.of}: {', '.join(sorted(relatives))}", False))
    # A finding the statement names that another passage says, shown negated where that one says it is present or
    # the other way round ("Niega dolor torácico" as "denies headache"): moved, and changed.
    for label in named - {item.of for item in items}:
        there = {item.negated for item in of(label, everywhere) if item.negated is not None}
        negated = {claim.value == "yes" for claim in said if claim.kind == "negated" and claim.label == label}
        if len(there) == 1 and negated and not negated & there:
            changed.append(("negation", f"{label} {'not ' if there == {True} else ''}negated", True))
    for claim in said:
        if claim.kind == "doubt" and claim.label is None and not any(item.doubt for item in items):
            changed.append(("certainty", "shown with doubt", False))
        if claim.kind == "who" and claim.label is None and not any(item.who == claim.value for item in items):
            changed.append(("relative", claim.value, False))
        if claim.kind == "history" and claim.label is None and not any(item.history for item in items):
            changed.append(("history", "shown as in the past", False))
    return changed, [claim for claim in backed_claims if claim.kind in VALUES]


def _added_measurement(value: str, backed: set[str]) -> bool:
    """A unit of measurement added to a number said without one ("78" as "78 bpm")."""
    parts = value.replace("%", " %").split()
    return len(parts) == 2 and parts[1] in MEASUREMENT and parts[0] in backed


# ---- Where each entry is in a transcript ----------------------------------------------------------------------------


def _plain(text: str) -> list[tuple[str, int, int]]:
    """Each word of the text in lower case without accents, and where it is."""
    found = []
    for match in re.finditer(r"\w+", text):
        word = "".join(c for c in unicodedata.normalize("NFD", match.group().lower()) if unicodedata.category(c) != "Mn")
        found.append((word, match.start(), match.end()))
    return found


def places(said: list[str], display: str, hypothesis: str) -> list[tuple[int, int] | None]:
    """Where each passage of the display text is in the hypothesis: the same place when they are the same text, and
    otherwise where its words line up with the hypothesis's (None where none do)."""
    if hypothesis == display:
        return [(display.index(s), display.index(s) + len(s)) for s in said]
    shown, heard = _plain(display), _plain(hypothesis)
    matcher = difflib.SequenceMatcher(None, [w for w, _, _ in shown], [w for w, _, _ in heard], autojunk=False)
    mapped: dict[int, int] = {}
    for tag, a0, a1, b0, b1 in matcher.get_opcodes():
        if tag in ("equal", "replace"):
            for k in range(a1 - a0):
                mapped[a0 + k] = min(b0 + k, b1 - 1) if b1 > b0 else None
    found = []
    for passage in said:
        start = display.index(passage)
        inside = [mapped.get(k) for k, (_, s, e) in enumerate(shown) if start <= s and e <= start + len(passage)]
        inside = [k for k in inside if k is not None]
        found.append((heard[min(inside)][1], heard[max(inside)][2]) if inside else None)
    return found
