"""How the checks read a transcript dictated in Spanish (the expansion plan's step 17): its words with their positions,
its numbers, the cues that negate a finding, make it uncertain, put it in the past or give it to someone else, the
words for units, how often, sides, routes, severity, dates, medication changes and abbreviations, Spanish drug names
as their English generic names, and the instructions to order something or change the allergy list that the note
never carries (FR-14). translation.py compares a proposed English statement with the Spanish it cites through these.

Every list here is our own, written for this step from how Spanish clinical dictation is commonly phrased. Published
work was consulted, not copied: NUBes (Lima et al., LREC 2020; CC BY-SA 3.0 ES, share-alike) and the IULA Spanish
clinical record corpus (Marimon et al., 2017; its licence statements conflict). medSpaCy has no Spanish ConText rules.
The lists must be read by a Spanish-speaking clinician before any real use: a word missing here is a check that
doesn't happen, and a word wrongly here throws away a right proposal.

Words are compared lower-cased and without accents (their key), so a transcript's "torácico" meets a quote's
"toracico", except where an accent tells two words apart: "mamá" (mother) and "mama" (breast), "papá" and "papa".
Positions are code points in the text, as in the English checks; to_json() turns them into UTF-16 offsets.
"""

from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass

from .textnorm import normalize_number

# ---- Words ----------------------------------------------------------------------------------------------------------

# A number ("37,8", "1.000", "142"), or a word, letters and any digits in it together ("HbA1c", "SatO2", "DM2").
WORD = re.compile(r"\d+(?:[.,]\d+)*(?![^\W_])|[^\W_]*[^\W\d_][^\W_]*(?:['’][^\W_]+)*")


def fold(text: str) -> str:
    """Lower case without accents, one character for one, so positions in the folded text are the text's own."""
    out = []
    for character in text:
        parts = unicodedata.normalize("NFD", character)
        base = parts[0] if len(parts) > 1 and all(unicodedata.category(p) == "Mn" for p in parts[1:]) else character
        lower = base.lower()
        out.append(lower if len(lower) == 1 else base)
    return "".join(out)


@dataclass(frozen=True)
class Token:
    """A word or a number: lower-cased with its accents (text), without them (key), and where it is (code points)."""

    text: str
    key: str
    start: int
    end: int


def tokens(text: str) -> list[Token]:
    return [Token(m.group().lower().replace("’", "'"), fold(m.group()).replace("’", "'"), m.start(), m.end())
            for m in WORD.finditer(text)]


# Words that carry no meaning of their own, for the length check (translation.py): articles, prepositions,
# conjunctions, pronouns and the commonest forms of "to be" and "to have". "no", "sin" and "ni" are not among them, as
# "no" and "without" aren't in the English list.
STOPWORDS = frozenset(
    """a al ante bajo con contra de del desde durante en entre hacia hasta mediante para por segun sobre tras el la los
    las lo un una unos unas y e o u que se su sus le les me te nos mi mis tu tus este esta esto estos estas ese esa eso
    esos esas aquel aquella es son era fue ha han he hemos hay ser estar estan esta muy mas ya tambien como cuando
    donde porque si pero cual quien yo usted ellos ellas el ella""".split()
)

# ---- Numbers --------------------------------------------------------------------------------------------------------

UNITS = {
    "cero": 0, "uno": 1, "un": 1, "una": 1, "dos": 2, "tres": 3, "cuatro": 4, "cinco": 5, "seis": 6, "siete": 7,
    "ocho": 8, "nueve": 9, "diez": 10, "once": 11, "doce": 12, "trece": 13, "catorce": 14, "quince": 15,
    "dieciseis": 16, "diecisiete": 17, "dieciocho": 18, "diecinueve": 19, "veinte": 20, "veintiuno": 21, "veintiun": 21,
    "veintiuna": 21, "veintidos": 22, "veintitres": 23, "veinticuatro": 24, "veinticinco": 25, "veintiseis": 26,
    "veintisiete": 27, "veintiocho": 28, "veintinueve": 29,
}
TENS = {"treinta": 30, "cuarenta": 40, "cincuenta": 50, "sesenta": 60, "setenta": 70, "ochenta": 80, "noventa": 90}
HUNDREDS = {
    "cien": 100, "ciento": 100, "doscientos": 200, "doscientas": 200, "trescientos": 300, "trescientas": 300,
    "cuatrocientos": 400, "cuatrocientas": 400, "quinientos": 500, "quinientas": 500, "seiscientos": 600,
    "seiscientas": 600, "setecientos": 700, "setecientas": 700, "ochocientos": 800, "ochocientas": 800,
    "novecientos": 900, "novecientas": 900,
}
NUMBER_WORDS = frozenset(UNITS) | frozenset(TENS) | frozenset(HUNDREDS) | {"mil"}
# "un", "una" and "uno" are articles too: they are numbers only before a unit, a dose form, "vez" or a time.
ARTICLE_NUMBERS = frozenset({"un", "una", "uno"})
COUNTED = frozenset(
    """vez veces dosis hora horas dia dias semana semanas mes meses ano anos minuto minutos segundo segundos tableta
    tabletas comprimido comprimidos pastilla pastillas capsula capsulas gota gotas ampolla ampollas sobre sobres parche
    parches inhalacion inhalaciones puff puffs disparo disparos""".split()
)
ORDINALS = {
    "primero": 1, "primer": 1, "primera": 1, "segundo": 2, "segunda": 2, "tercero": 3, "tercer": 3, "tercera": 3,
    "cuarto": 4, "quinto": 5, "sexto": 6, "septimo": 7, "octavo": 8, "noveno": 9, "decimo": 10,
}
DECIMAL_WORDS = frozenset({"coma", "punto"})


@dataclass(frozen=True)
class Number:
    """A number said in the text: its words (first and last token, inclusive), what it can be read as, and whether it
    can be read two ways ("1.000": a thousand in Spain, one with three decimals in English)."""

    first: int
    last: int
    readings: frozenset[str]
    ambiguous: bool = False


def digit_readings(raw: str) -> tuple[frozenset[str], bool]:
    """A number written in digits, read as Spanish writes it and as English does. A comma or point followed by one,
    two or four or more digits is a decimal mark ("37,8" is 37.8); followed by exactly three it may be either a decimal
    mark or a thousands separator, and both readings are kept."""
    if "," in raw and "." in raw:
        last = max(raw.rfind(","), raw.rfind("."))
        other = "." if raw[last] == "," else ","
        return frozenset({normalize_number(raw[:last].replace(other, "") + "." + raw[last + 1 :])}), False
    for mark in ",.":
        if mark in raw:
            parts = raw.split(mark)
            if len(parts) > 2:
                return frozenset({normalize_number("".join(parts))}), False
            whole, fraction = parts
            if len(fraction) == 3 and len(whole) <= 3:
                return frozenset({normalize_number(whole + fraction), normalize_number(f"{whole}.{fraction}")}), True
            return frozenset({normalize_number(f"{whole}.{fraction}")}), False
    return frozenset({normalize_number(raw)}), False


def _word_number(keys: list[str], i: int) -> tuple[int, int] | None:
    """The number said in words from keys[i], and the index of its last word: "ciento cuarenta y dos" is 142,
    "dos mil diecinueve" 2019. None if keys[i] doesn't start one."""
    if keys[i] not in NUMBER_WORDS:
        return None
    if keys[i] in ARTICLE_NUMBERS:
        after = keys[i + 1] if i + 1 < len(keys) else ""
        if after not in COUNTED and after not in UNIT_WORDS and not (after == "y" and keys[i + 2 : i + 3] in (["medio"], ["media"])):
            return None
    total = group = 0
    last = i - 1
    j = i
    while j < len(keys):
        key = keys[j]
        if key in HUNDREDS:
            group += HUNDREDS[key]
        elif key in TENS:
            group += TENS[key]
            if j + 2 < len(keys) and keys[j + 1] == "y" and keys[j + 2] in UNITS and UNITS[keys[j + 2]] < 10:
                group += UNITS[keys[j + 2]]
                j += 2
        elif key in UNITS:
            group += UNITS[key]
        elif key == "mil":
            total += (group or 1) * 1000
            group = 0
        else:
            break
        last = j
        j += 1
        # A unit, teen or twenty-something ends a group unless "mil" follows ("dos mil"), or a bigger word starts the
        # next number ("uno cuarenta" is 1 and 40, as the English reading has it).
        if key in UNITS and (j >= len(keys) or keys[j] != "mil"):
            break
    return (total + group, last) if last >= i else None


def numbers(toks: list[Token]) -> list[Number]:
    """Every number in the words, in order: digits ("37,8", "142"), words ("treinta y siete coma ocho"), a half after
    one ("uno y medio" is 1.5), "medio" or "media" before a unit or dose form (0.5), and an ordinal ("primero de marzo"
    is 1, "el primer mes" 1), as the English reading counts "first"."""
    keys = [t.key for t in toks]
    found: list[Number] = []
    i = 0
    while i < len(toks):
        key = keys[i]
        if key[0].isdigit():
            readings, ambiguous = digit_readings(key)
            first, last, value = i, i, None
            if len(readings) == 1:
                value = next(iter(readings))
        else:
            said = _word_number(keys, i)
            if said is None:
                if key in ("medio", "media") and i + 1 < len(keys) and (keys[i + 1] in UNIT_WORDS or keys[i + 1] in COUNTED):
                    found.append(Number(i, i, frozenset({"0.5"})))
                elif key in ORDINALS:
                    found.append(Number(i, i, frozenset({str(ORDINALS[key])})))
                i += 1
                continue
            value_int, last = said
            first, value, ambiguous = i, str(value_int), False
            readings = frozenset({value})
        # A decimal said in words: "treinta y siete coma ocho", "37 coma 5".
        if value is not None and last + 2 < len(keys) and keys[last + 1] in DECIMAL_WORDS:
            fraction = keys[last + 2]
            fraction_said = (fraction, last + 2) if fraction.isdigit() else None
            if fraction_said is None:
                words = _word_number(keys, last + 2)
                fraction_said = (str(words[0]), words[1]) if words else None
            if fraction_said is not None:
                readings = frozenset({normalize_number(f"{value}.{fraction_said[0]}")})
                last = fraction_said[1]
        elif value is not None and last + 2 < len(keys) and keys[last + 1] == "y" and keys[last + 2] in ("medio", "media"):
            readings = frozenset({normalize_number(str(float(value) + 0.5))})
            last += 2
        found.append(Number(first, last, readings, ambiguous))
        i = last + 1
    return found


def readings(toks: list[Token]) -> set[str]:
    return {reading for number in numbers(toks) for reading in number.readings}


# ---- Units, how often, sides, routes, severity ------------------------------------------------------------------------

# Unit words after a number, by the canonical name translation.py compares with the English unit.
UNIT_NAMES = {
    "mg": "mg", "mgs": "mg", "miligramo": "mg", "miligramos": "mg",
    "mcg": "mcg", "μg": "mcg", "µg": "mcg", "ug": "mcg", "microgramo": "mcg", "microgramos": "mcg", "gamma": "mcg",
    "gammas": "mcg",
    "g": "g", "gr": "g", "gramo": "g", "gramos": "g",
    "kg": "kg", "kilo": "kg", "kilos": "kg", "kilogramo": "kg", "kilogramos": "kg",
    "ml": "ml", "mililitro": "ml", "mililitros": "ml", "cc": "ml",
    "l": "l", "litro": "l", "litros": "l",
    "ui": "unit", "u": "unit", "unidad": "unit", "unidades": "unit",
    "grado": "degree", "grados": "degree",
    "mmhg": "mmhg", "lpm": "bpm", "rpm": "rpm",
}
UNIT_WORDS = frozenset(UNIT_NAMES)
# Said as several words: "milímetros de mercurio", "latidos por minuto", "por ciento".
UNIT_PHRASES = [
    (("milimetros", "de", "mercurio"), "mmhg"), (("latidos", "por", "minuto"), "bpm"),
    (("respiraciones", "por", "minuto"), "rpm"), (("por", "ciento"), "percent"), (("porciento",), "percent"),
]


def number_units(text: str, toks: list[Token]) -> set[tuple[str, str]]:
    """Each number said with its unit, as (reading, unit): "50 microgramos" is ("50", "mcg"), "92 %" ("92", "percent")."""
    return {(reading, unit) for number, unit in measured(text, toks) for reading in number.readings}


def measured(text: str, toks: list[Token]) -> list[tuple[Number, str]]:
    """Each number said with a unit, and the unit."""
    keys = [t.key for t in toks]
    pairs: list[tuple[Number, str]] = []
    for number in numbers(toks):
        after = number.last + 1
        unit = None
        if after < len(keys) and keys[after] in UNIT_NAMES:
            unit = UNIT_NAMES[keys[after]]
            if unit == "mg" and after + 1 < len(keys) and keys[after + 1] in ("dl", "decilitro"):
                unit = "mg/dl"
        for words, name in UNIT_PHRASES:
            if tuple(keys[after : after + len(words)]) == words:
                unit = name
        end = toks[number.last].end
        if unit is None and re.match(r"\s*%", text[end:]):
            unit = "percent"
        if unit is None and re.match(r"\s*°", text[end:]):
            unit = "degree"
        if unit is not None:
            pairs.append((number, unit))
    return pairs


_SP_NUMBER = r"(\d+|" + "|".join(sorted(UNITS, key=len, reverse=True)) + "|" + "|".join(TENS) + ")"


def _count(word: str) -> int | None:
    if word.isdigit():
        return int(word)
    return UNITS.get(word) or TENS.get(word)


def _per_day(hours: int) -> str:
    return f"{24 // hours}/d" if hours and 24 % hours == 0 else f"q{hours}h"


# How often, as times per period: in order, each one's text taken so a shorter phrase inside it ("al día" in "dos veces
# al día") isn't read again. "cada tercer día" is every other day in Mexico and every third day literally: both, and
# translation.py warns.
FREQUENCIES = [
    (re.compile(rf"\b{_SP_NUMBER} veces (?:al|por|cada|a el) dia\b"), lambda m: {f"{_count(m.group(1))}/d"}),
    (re.compile(r"\buna vez (?:al|por|cada) dia\b|\buna vez diaria\b"), lambda m: {"1/d"}),
    (re.compile(rf"\b{_SP_NUMBER} veces (?:a la|por|cada) semana\b"), lambda m: {f"{_count(m.group(1))}/w"}),
    (re.compile(r"\buna vez (?:a la|por|cada) semana\b"), lambda m: {"1/w"}),
    (re.compile(r"\buna vez (?:al|por|cada) mes\b"), lambda m: {"1/m"}),
    (re.compile(rf"\bcada {_SP_NUMBER} horas?\b"), lambda m: {_per_day(_count(m.group(1)) or 0)}),
    (re.compile(r"\bcada tercer dia\b"), lambda m: {"1/2d", "1/3d"}),
    (re.compile(r"\b(?:en )?dias alternos\b|\bcada otro dia\b|\bun dia si y otro no\b|\binterdiari[oa]\b"), lambda m: {"1/2d"}),
    (re.compile(rf"\bcada {_SP_NUMBER} dias\b"), lambda m: {f"1/{_count(m.group(1))}d"}),
    (re.compile(rf"\bcada {_SP_NUMBER} semanas\b"), lambda m: {f"1/{_count(m.group(1))}w"}),
    (re.compile(rf"\bcada {_SP_NUMBER} meses\b"), lambda m: {f"1/{_count(m.group(1))}m"}),
    (re.compile(r"\b(?:diari[oa]s?|diariamente|cada dia|todos los dias|al dia|por dia)\b"), lambda m: {"1/d"}),
    (re.compile(r"\b(?:semanal(?:es|mente)?|cada semana|a la semana|por semana)\b"), lambda m: {"1/w"}),
    (re.compile(r"\b(?:mensual(?:es|mente)?|cada mes|al mes|por mes)\b"), lambda m: {"1/m"}),
    (re.compile(r"\b(?:si es necesario|si lo necesita|si precisa|en caso de|a demanda|segun necesidad|por razon "
                r"necesaria|prn)\b"), lambda m: {"prn"}),
    (re.compile(r"\b(?:al acostarse|antes de dormir|por la noche|en la noche|cada noche|por las noches|en las noches)\b"),
     lambda m: {"hs"}),
    (re.compile(r"\b(?:por la manana|en la manana|cada manana|por las mananas)\b"), lambda m: {"am"}),
    (re.compile(r"\bantes de (?:las |cada )?comidas?\b|\bantes de comer\b"), lambda m: {"ac"}),
    (re.compile(r"\bdespues de (?:las |cada )?comidas?\b|\bdespues de comer\b"), lambda m: {"pc"}),
    (re.compile(r"\bcon (?:las |la )?comidas?\b|\bcon alimentos\b"), lambda m: {"with-meals"}),
    (re.compile(r"\ben ayunas\b"), lambda m: {"fasting"}),
]
AMBIGUOUS_FREQUENCY = re.compile(r"\bcada tercer dia\b")


def frequencies(text: str) -> set[str]:
    """How often the text says something is taken or done, as times per period ("2/d", "1/w", "3/d" for every eight
    hours), and when ("prn", "hs", "ac")."""
    folded = fold(text)
    taken: list[tuple[int, int]] = []
    found: set[str] = set()
    for pattern, read in FREQUENCIES:
        for match in pattern.finditer(folded):
            if any(match.start() < end and start < match.end() for start, end in taken):
                continue
            taken.append(match.span())
            found |= read(match)
    return found


SIDES = {"izquierdo": "left", "izquierda": "left", "izquierdos": "left", "izquierdas": "left", "izq": "left",
         "derecho": "right", "derecha": "right", "derechos": "right", "derechas": "right", "der": "right",
         "bilateral": "bilateral", "bilaterales": "bilateral", "ambos": "bilateral", "ambas": "bilateral"}

ROUTES = [
    ("oral", re.compile(r"\b(?:oral(?:es)?|via oral|por boca|vo)\b")),
    ("iv", re.compile(r"\b(?:intravenos[oa]s?|intravenosamente|endovenos[oa]s?|iv|ev)\b")),
    ("im", re.compile(r"\b(?:intramuscular(?:es)?|im)\b")),
    ("sc", re.compile(r"\b(?:subcutane[oa]s?|subcutaneamente|sc|sbc)\b")),
    ("inhaled", re.compile(r"\b(?:inhalad[oa]s?|inhalacion(?:es)?|inhalador(?:es)?|nebulizad[oa]s?|nebulizacion(?:es)?)\b")),
    ("topical", re.compile(r"\b(?:topic[oa]s?|topicamente)\b")),
    ("sublingual", re.compile(r"\bsublingual(?:es)?\b")),
    ("rectal", re.compile(r"\brectal(?:es)?\b")),
    ("vaginal", re.compile(r"\bvaginal(?:es)?\b")),
    ("ophthalmic", re.compile(r"\b(?:oftalmic[oa]s?|colirio)\b")),
    ("otic", re.compile(r"\botic[oa]s?\b")),
    ("nasal", re.compile(r"\b(?:nasal(?:es)?|intranasal(?:es)?)\b")),
]


def routes(text: str) -> set[str]:
    folded = fold(text)
    return {name for name, pattern in ROUTES if pattern.search(folded)}


# What each severity word allows in English: "intenso" is intense or severe.
SEVERITY = {
    "leve": {"mild"}, "leves": {"mild"}, "moderado": {"moderate"}, "moderada": {"moderate"}, "moderados": {"moderate"},
    "moderadas": {"moderate"}, "grave": {"severe"}, "graves": {"severe"}, "severo": {"severe"}, "severa": {"severe"},
    "severos": {"severe"}, "severas": {"severe"}, "intenso": {"intense", "severe"}, "intensa": {"intense", "severe"},
    "intensos": {"intense", "severe"}, "intensas": {"intense", "severe"}, "critico": {"critical"}, "critica": {"critical"},
}

# ---- Dates ----------------------------------------------------------------------------------------------------------

MONTHS = {"enero": "january", "febrero": "february", "marzo": "march", "abril": "april", "mayo": "may", "junio": "june",
          "julio": "july", "agosto": "august", "septiembre": "september", "setiembre": "september",
          "octubre": "october", "noviembre": "november", "diciembre": "december"}
WEEKDAYS = {"lunes": "monday", "martes": "tuesday", "miercoles": "wednesday", "jueves": "thursday",
            "viernes": "friday", "sabado": "saturday", "domingo": "sunday"}
# "mañana" is tomorrow, unless it is the morning: after one of these.
MORNING_AFTER = frozenset("la esta por de en cada una toda".split())
PERIOD_NAMES = {"semana": "week", "mes": "month", "ano": "year", "noche": "night", "manana": "morning"}
# "Hace un mes", "desde hace tres semanas": a time up to now, "a month ago" or "for the past 3 weeks".
SINCE_PERIODS = {"semana": "week", "semanas": "week", "mes": "month", "meses": "month", "ano": "year", "anos": "year"}
SOME = frozenset("unos unas varios varias algunos algunas".split())


def date_terms(toks: list[Token]) -> set[str]:
    """The dates the words name, as the English checks name them (validation._date_terms): months, weekdays, today,
    yesterday, tomorrow, "last week", "next month", "this morning", "ago" for "hace", and "past week" (or weeks) for
    "hace una semana" and "hace dos semanas", as "for the past week" says it. "La semana pasada" is "last week" only."""
    keys = [t.key for t in toks]
    said = {number.first: number.last for number in numbers(toks)}
    found: set[str] = set()
    for i, key in enumerate(keys):
        before = keys[i - 1] if i else ""
        after = keys[i + 1] if i + 1 < len(keys) else ""
        if key in MONTHS:
            found.add(MONTHS[key])
        elif key in WEEKDAYS:
            found.add(WEEKDAYS[key])
            if after in ("pasado", "pasada"):
                found.add(f"last {WEEKDAYS[key]}")
            if before in ("proximo", "proxima") or (after == "que" and keys[i + 2 : i + 3] == ["viene"]):
                found.add(f"next {WEEKDAYS[key]}")
        elif key == "hoy":
            found.add("today")
        elif key == "ayer":
            found.add("yesterday")
        elif key == "anoche":
            found |= {"last night", "yesterday"}
        elif key == "manana" and before not in MORNING_AFTER:
            found.add("tomorrow")
        elif key == "hace":
            found.add("ago")
            j = said[i + 1] + 1 if i + 1 in said else i + 2 if after in SOME else i + 1
            if j < len(keys) and keys[j] in SINCE_PERIODS:
                found |= {f"past {SINCE_PERIODS[keys[j]]}", f"past {SINCE_PERIODS[keys[j]]}s"}
        if key in PERIOD_NAMES:
            period = PERIOD_NAMES[key]
            if after in ("pasado", "pasada") or before in ("ultimo", "ultima", "pasado", "pasada"):
                found.add(f"last {period}")
            if before in ("proximo", "proxima", "siguiente") or (after == "que" and keys[i + 2 : i + 3] == ["viene"]):
                found.add(f"next {period}")
            if before in ("este", "esta"):
                found.add("tonight" if period == "night" else f"this {period}")
    return found


# ---- Abbreviations --------------------------------------------------------------------------------------------------

# A Spanish abbreviation or term, folded, and the English abbreviations it may be written as. An English abbreviation
# in a proposal must be said as itself or come from one of these (translation.py).
ABBREVIATIONS = {
    "ta": {"BP"}, "presion arterial": {"BP"}, "tension arterial": {"BP"}, "fc": {"HR"}, "frecuencia cardiaca": {"HR"},
    "fr": {"RR"}, "frecuencia respiratoria": {"RR"}, "sato2": {"SPO2", "O2"}, "sat o2": {"SPO2", "O2"},
    "saturacion": {"SPO2", "O2"}, "hta": {"HTN"}, "hipertension": {"HTN"}, "dm2": {"T2DM", "DM2", "DM"},
    "dm": {"DM"}, "diabetes tipo 2": {"T2DM", "DM2"}, "epoc": {"COPD"}, "iam": {"MI", "AMI"}, "infarto": {"MI"},
    "icc": {"CHF", "HF"}, "insuficiencia cardiaca": {"HF", "CHF", "HFREF", "HFPEF"}, "erc": {"CKD"},
    "enfermedad renal cronica": {"CKD"}, "imc": {"BMI"}, "ecg": {"ECG", "EKG"}, "ekg": {"ECG", "EKG"},
    "electrocardiograma": {"ECG", "EKG"}, "fevi": {"LVEF", "EF"}, "fraccion de eyeccion": {"LVEF", "EF"},
    "bh": {"CBC"}, "biometria hematica": {"CBC"}, "hemograma": {"CBC"}, "hba1c": {"HBA1C", "A1C"},
    "a1c": {"A1C", "HBA1C"}, "hemoglobina glucosilada": {"HBA1C", "A1C"}, "hemoglobina glicosilada": {"HBA1C", "A1C"},
    "pcr": {"CRP", "PCR"}, "proteina c reactiva": {"CRP"}, "tsh": {"TSH"}, "inr": {"INR"}, "tac": {"CT"},
    "tomografia": {"CT"}, "rm": {"MRI"}, "rmn": {"MRI"}, "resonancia": {"MRI"}, "covid": {"COVID"}, "vih": {"HIV"},
    "itu": {"UTI"}, "infeccion urinaria": {"UTI"}, "infeccion de vias urinarias": {"UTI"}, "ldl": {"LDL"},
    "hdl": {"HDL"}, "tfg": {"GFR", "EGFR"}, "filtrado glomerular": {"GFR", "EGFR"}, "ast": {"AST"}, "alt": {"ALT"},
    "acv": {"CVA"}, "evc": {"CVA"}, "tep": {"PE"}, "embolia pulmonar": {"PE"}, "tvp": {"DVT"},
    "trombosis venosa profunda": {"DVT"}, "fa": {"AF", "AFIB"}, "fibrilacion auricular": {"AF", "AFIB"},
    "phq": {"PHQ"}, "cbc": {"CBC"}, "aine": {"NSAID"}, "aines": {"NSAID", "NSAIDS"},
    "antiinflamatorio": {"NSAID"}, "antiinflamatorios": {"NSAID", "NSAIDS"}, "ira": {"AKI"}, "lesion renal aguda": {"AKI"}, "ivu": {"UTI"},
}


def abbreviations(text: str) -> set[str]:
    """The English abbreviations the Spanish text backs: its own abbreviations as said, and those ABBREVIATIONS gives
    for its abbreviations and terms."""
    folded = " " + " ".join(t.key for t in tokens(text)) + " "
    found = {t.key.upper() for t in tokens(text) if t.key.isalnum()}
    for term, english in ABBREVIATIONS.items():
        if f" {term.strip()} " in folded:
            found |= english
    return found


# ---- Drug names -----------------------------------------------------------------------------------------------------

# Spanish names whose English generic name the endings below don't give.
DRUG_EXCEPTIONS = {
    "paracetamol": {"acetaminophen", "paracetamol"}, "acetaminofen": {"acetaminophen"},
    "salbutamol": {"albuterol", "salbutamol"}, "adrenalina": {"epinephrine", "adrenaline"},
    "noradrenalina": {"norepinephrine", "noradrenaline"}, "aspirina": {"aspirin"}, "glibenclamida": {"glyburide"},
    "petidina": {"meperidine"}, "metamizol": {"metamizole", "dipyrone"}, "dipirona": {"dipyrone", "metamizole"},
    "levotiroxina": {"levothyroxine"}, "ciclosporina": {"cyclosporine"}, "insulina": {"insulin"},
    "fentanilo": {"fentanyl"}, "fenitoina": {"phenytoin"}, "hidroclorotiazida": {"hydrochlorothiazide"},
    "clortalidona": {"chlorthalidone"}, "espironolactona": {"spironolactone"}, "tiamina": {"thiamine"},
    "hidrocortisona": {"hydrocortisone"}, "hidroxicloroquina": {"hydroxychloroquine"}, "acetilsalicilico": {"aspirin"},
    "clorfenamina": {"chlorpheniramine"}, "ceftriaxona": {"ceftriaxone"}, "amoxicilina": {"amoxicillin"},
    "nitroglicerina": {"nitroglycerin"}, "teofilina": {"theophylline"}, "levetiracetam": {"levetiracetam"},
    "lamotrigina": {"lamotrigine"}, "clonazepam": {"clonazepam"}, "warfarina": {"warfarin"},
    "atorvastatina": {"atorvastatin"},
}
# Endings, longest first: the Spanish ending and the English ones it may be.
DRUG_ENDINGS = [
    ("floxacino", ("floxacin",)), ("gliflozina", ("gliflozin",)), ("cilina", ("cillin",)), ("micina", ("mycin",)),
    ("statina", ("statin",)), ("glutida", ("glutide",)), ("tidina", ("tidine",)), ("dipino", ("dipine",)),
    ("prazol", ("prazole",)), ("azol", ("azole",)), ("asa", ("ase",)), ("osa", ("ose",)), ("ina", ("in", "ine")),
    ("ino", ("in", "ine")), ("ona", ("one",)), ("eno", ("en", "ene")), ("ilo", ("yl", "il")), ("ato", ("ate",)),
    ("ida", ("ide",)), ("ido", ("ide",)), ("aco", ("ac",)), ("ico", ("ic",)), ("io", ("ium",)), ("ol", ("ol", "ole")),
    ("an", ("an",)),
]
# Words that read as a drug's name and aren't one, as validation.NOT_DRUGS for English: "colesterol" ends in a
# bronchodilator's stem, "ultrasonido" in a corticosteroid's, and "allí", "actos", "retina", "valió" are brand names
# (Alli, Actos, Retin-A, Valium) once their accents go.
NOT_DRUGS = frozenset({"colesterol", "ultrasonido", "alli", "actos", "retina", "valio", "soma", "cardeno", "bonina"})


def drug_candidates(word: str) -> set[str]:
    """The English generic names a Spanish word may be: itself (lisinopril, losartán), an exception (paracetamol is
    acetaminophen), or its ending changed (metformina, metformin; ibuprofeno, ibuprofen; furosemida, furosemide), with
    a leading "es" before a consonant dropped (espironolactona, spironolactone) and a leading "f" also tried as "ph"."""
    key = fold(word)
    found = {key} | DRUG_EXCEPTIONS.get(key, set())
    stems = {key}
    if re.match(r"es[bcdfgklmnpqrstvz]", key):
        stems.add(key[1:])
    for stem in list(stems):
        for ending, englishes in DRUG_ENDINGS:
            if stem.endswith(ending) and len(stem) > len(ending) + 2:
                found |= {stem[: -len(ending)] + english for english in englishes}
                break
    found |= {"ph" + name[1:] for name in found if name.startswith("f")}
    found |= {name.replace("hidro", "hydro").replace("clor", "chlor").replace("tiaz", "thiaz") for name in found}
    return found


# ---- Qualifiers -----------------------------------------------------------------------------------------------------

def _phrases(text: str) -> list[tuple[str, ...]]:
    return sorted((tuple(phrase.split()) for phrase in text.split(";") if phrase.strip()), key=len, reverse=True)


# Cues before what they qualify; and after it ("COVID negativo", "neumonía probable"). Each kind with its reason
# code's name in translation.py. Matched on keys (no accents), longest first.
NEGATION_BEFORE = _phrases(
    """no; ni; niega; nego; niegan; negaba; sin; nunca; jamas; ningun; ninguno; ninguna; tampoco; negativo para;
    negativa para; negativos para; ausencia de; libre de; no presenta; no refiere; no hay; no se observa; no se observan;
    no se palpa; no se palpan; se descarta; se descartan; descarta"""
)
NEGATION_AFTER = _phrases("negativo; negativa; negativos; negativas; ausente; ausentes; descartado; descartada; descartados; descartadas")
CERTAINTY_BEFORE = _phrases(
    """posible; posibles; posiblemente; probable; probables; probablemente; sospecha de; se sospecha; sospechoso de;
    sospechosa de; sospecho; a descartar; descartar; dudoso; dudosa; en estudio; impresiona de; presunto; presunta;
    no se puede descartar; no se descarta; riesgo de"""
)
CERTAINTY_AFTER = _phrases("probable; posible; a descartar; en estudio; dudoso; dudosa")
HISTORY_BEFORE = _phrases(
    """antecedente de; antecedentes de; antecedentes personales de; historia de; historial de; con antecedente de;
    operado de; operada de; postoperado de; postoperada de; intervenido de; intervenida de"""
)
HISTORY_AFTER = _phrases("previo; previa; previos; previas; antiguo; antigua")
# Not "historia de la enfermedad actual", the heading.
HISTORY_HEADING = ("historia", "de", "la", "enfermedad", "actual")

# Relatives, matched on the words with their accents ("mamá" is mother, "mama" is breast), and who they may be in
# English (translation.py names English relatives the same way). A plural may be either: "hermanos" are brothers or
# siblings.
RELATIVES = {
    "madre": {"mother"}, "mamá": {"mother"}, "padre": {"father"}, "papá": {"father"}, "padres": {"parent"},
    "hermano": {"brother"}, "hermanos": {"brother", "sibling"}, "hermana": {"sister"}, "hermanas": {"sister", "sibling"},
    "hijo": {"son"}, "hijos": {"son", "child"}, "hija": {"daughter"}, "hijas": {"daughter", "child"},
    "abuelo": {"grandfather"}, "abuelos": {"grandfather", "grandparent"}, "abuela": {"grandmother"},
    "tío": {"uncle"}, "tíos": {"uncle", "aunt"}, "tía": {"aunt"}, "tías": {"aunt"}, "primo": {"cousin"},
    "primos": {"cousin"}, "prima": {"cousin"}, "primas": {"cousin"}, "esposo": {"husband", "spouse"},
    "marido": {"husband", "spouse"}, "esposa": {"wife", "spouse"}, "pareja": {"partner", "spouse"},
    "sobrino": {"nephew"}, "sobrinos": {"nephew"}, "sobrina": {"niece"}, "sobrinas": {"niece"}, "nieto": {"grandson"},
    "nietos": {"grandson", "grandchild"}, "nieta": {"granddaughter"}, "materno": {"maternal"}, "materna": {"maternal"},
    "paterno": {"paternal"}, "paterna": {"paternal"},
}
EXPERIENCER_BEFORE = _phrases(
    "antecedentes familiares de; antecedentes familiares; antecedente familiar de; historia familiar de; familiares; familiar"
)
# A relative after one of these (and maybe "su", "el", "la") is someone the patient is with or speaks to: "acompañado
# de su madre", "se informa al padre" ("al" is "a el"). Unless a relative clause says what they have: "su madre, que
# tiene diabetes".
NOT_EXPERIENCER_AFTER = frozenset("con por a al de para junto".split())
POSSESSIVES = frozenset("su sus el la los las mi".split())
WHO_HAS = re.compile(r"\s*,?\s+(?:que|quien)\s+(?=(?:tiene|tuvo|tenia|padece|padecio|es|era|fue)\b)")
# Someone telling, not having: "la madre refiere tos nocturna" is the patient's cough.
REPORTING = frozenset(
    """dice dijo refiere refirio comenta comento cuenta conto informa informo reporta reporto menciona menciono explica
    explico nota noto cree piensa trajo trae llamo llama preocupa preocupada preocupado""".split()
)
# Where a cue's reach ends, besides punctuation: a contrast, a new subject ("él", told from the article "el" by its
# accent), or the next step of the plan.
TERMINATORS = frozenset("pero aunque embargo excepto salvo mientras quien cual paciente ella ellos ellas".split())
NEXT_CLAUSE = frozenset(
    """iniciar inicia continuar continua suspender suspende aumentar aumenta disminuir disminuye mantener mantiene
    seguir sigue control cita derivar derivo interconsulta exploracion examen impresion plan revisar regresar volver
    recomiendo recomendar indico""".split()
)
SCOPE = 6  # words after a cue it can reach, within its clause
POST_SCOPE = 4  # words before a cue that follows what it qualifies


def _matches(keys: list[str], phrases: list[tuple[str, ...]]) -> list[tuple[int, int]]:
    """Where the phrases occur in the keys, as (first, last) word indices, longest first, not overlapping."""
    taken: set[int] = set()
    found = []
    for phrase in phrases:
        for i in range(len(keys) - len(phrase) + 1):
            if tuple(keys[i : i + len(phrase)]) == phrase and not taken & set(range(i, i + len(phrase))):
                taken |= set(range(i, i + len(phrase)))
                found.append((i, i + len(phrase) - 1))
    return sorted(found)


def _stops(text: str, toks: list[Token], i: int, edge: int, k: int, ends: frozenset[str]) -> bool:
    between = text[edge : toks[i].start]
    return (toks[i].key in ends or toks[i].text == "él" or any(mark in between for mark in ".,;:?!\n")
            or (k and toks[i].key in NEXT_CLAUSE and toks[i - 1].key != "a"))


def _reach(text: str, toks: list[Token], before: list[tuple[int, int]], after: list[tuple[int, int]],
           ends: frozenset[str] = TERMINATORS) -> tuple[set[int], set[int]]:
    """The words the cues reach, and the cues' own words: SCOPE words on from a cue before what it qualifies, POST_SCOPE
    back from one after it, each stopping at its clause."""
    reached: set[int] = set()
    cues: set[int] = set()
    for first, last in before:
        cues |= set(range(first, last + 1))
        edge = toks[last].end
        for k, i in enumerate(range(last + 1, min(len(toks), last + 1 + SCOPE))):
            if _stops(text, toks, i, edge, k, ends):
                break
            reached.add(i)
            edge = toks[i].end
    for first, last in after:
        cues |= set(range(first, last + 1))
        edge = toks[first].start
        for i in range(first - 1, max(-1, first - 1 - POST_SCOPE), -1):
            if toks[i].key in ends or any(mark in text[toks[i].end : edge] for mark in ".,;:?!\n"):
                break
            reached.add(i)
            edge = toks[i].start
    return reached - cues, cues


def _is_content(token: Token) -> bool:
    return token.key.isalpha() and len(token.key) >= 2 and token.key not in STOPWORDS


def qualified(text: str, toks: list[Token]) -> dict[str, set[int]]:
    """For each kind of qualifier (negation, certainty, history, experiencer), the content words its cues reach."""
    keys = [t.key for t in toks]
    certainty_before = _matches(keys, CERTAINTY_BEFORE)
    taken = {i for first, last in certainty_before for i in range(first, last + 1)}
    corrections = {i for first, last in correction_cues(text, toks) for i in range(first, last + 1)}

    def free(spans: list[tuple[int, int]]) -> list[tuple[int, int]]:
        return [(f, l) for f, l in spans if not taken & set(range(f, l + 1)) and not corrections & set(range(f, l + 1))]

    history = [(f, l) for f, l in _matches(keys, HISTORY_BEFORE)
               if tuple(keys[f : f + len(HISTORY_HEADING)]) != HISTORY_HEADING]
    relatives, relatives_after = [], []
    for i, token in enumerate(toks):
        if token.text not in RELATIVES:
            continue
        relative = WHO_HAS.match(fold(text), token.end)
        j = i - 1
        if j >= 0 and toks[j].key in POSSESSIVES:
            j -= 1
        # "Cáncer de mama en su madre": the relative after what they have.
        if j >= 0 and toks[j].key == "en":
            relatives_after.append((j, i))
            continue
        if not relative and j >= 0 and toks[j].key in NOT_EXPERIENCER_AFTER:
            continue
        relatives.append((i, i))
    out: dict[str, set[int]] = {}
    for kind, before, after, ends in (
        ("negation", free(_matches(keys, NEGATION_BEFORE)), free(_matches(keys, NEGATION_AFTER)), TERMINATORS),
        ("certainty", certainty_before, _matches(keys, CERTAINTY_AFTER), TERMINATORS),
        ("history", history, _matches(keys, HISTORY_AFTER), TERMINATORS),
        ("experiencer", sorted(_matches(keys, EXPERIENCER_BEFORE) + relatives), relatives_after, TERMINATORS | REPORTING),
    ):
        reached, _ = _reach(text, toks, before, after, ends)
        out[kind] = {i for i in reached if _is_content(toks[i])}
    return out


def relatives(toks: list[Token]) -> list[set[str]]:
    """Each relative the words name, as who they may be in English."""
    return [RELATIVES[t.text] for t in toks if t.text in RELATIVES]


# ---- Corrections ----------------------------------------------------------------------------------------------------

CORRECTIONS = _phrases(
    """no perdon; perdon; perdona; disculpa; disculpe; quiero decir; quise decir; o sea; corrijo; correccion;
    mejor dicho; rectifico; me equivoque; borra eso; borre eso; tacha eso; olvida eso; en realidad; mas bien; no espera;
    no digo"""
)
# Words one of which, between a value's number, date or drug and another of the same kind just after it, may be a
# correction the checks didn't hear: the possible_correction warning (translation.py).
GAP_WORDS = frozenset("no espera perdon digo decir corrijo correccion mejor rectifico error cambia cambio".split())


def correction_cues(text: str, toks: list[Token]) -> list[tuple[int, int]]:
    """Where the clinician corrects themselves, as (first, last) word indices. "digo" counts after a comma ("10 mg,
    digo, 20 mg") or "no"; "no, perdón" and "no, espera" are one cue with the comma."""
    keys = [t.key for t in toks]
    found = _matches(keys, CORRECTIONS)
    taken = {i for f, l in found for i in range(f, l + 1)}
    for i, key in enumerate(keys):
        if key == "digo" and i not in taken and i and text[toks[i - 1].end : toks[i].start].strip() == ",":
            found.append((i, i))
    return sorted(found)


# ---- Instructions (FR-14) -------------------------------------------------------------------------------------------

def _forms(*groups: str) -> str:
    return "|".join(sorted({form for group in groups for form in group.split()}, key=len, reverse=True))


# Present, imperative, subjunctive and future forms (and the enclitic imperatives: "pídale", "mándale", folded), and
# the infinitive, which a plan uses as an imperative ("Solicitar hemograma."). Past forms are documentation: "pidió",
# "ordenó", "recetó", "solicitó", "pedí", "receté".
ORDER_VERBS = _forms(
    "pido pide pida pidan pedimos pedire pediremos pidele pidale pidanle pedir",
    "ordeno ordena ordene ordenen ordenamos ordenare ordenaremos ordenese ordenar",
    "solicito solicita solicite soliciten solicitamos solicitare solicitaremos solicitese solicitar",
    "receto recete receten recetamos recetare recetaremos recetese recetar",
    "prescribo prescribe prescriba prescriban prescribimos prescribire prescribiremos prescribir",
)
THIRD_PERSON = frozenset("pide piden ordena ordenan solicita solicitan receta recetan prescribe prescriben manda mandan "
                         "envia envian".split())
INFINITIVES = "pedir|ordenar|solicitar|recetar|prescribir|mandar|enviar|renovar|resurtir"
SEND_ONLY = _forms(
    "mando manda mande manden mandamos mandare mandaremos mandale mandele mandenle mandar",
    "envio envia envie envien enviamos enviare enviaremos enviale enviele enviar",
)
SEND_VERBS = SEND_ONLY + "|" + _forms("llamo llama llame llamamos llamale llamar")
ALLERGY_ADD = _forms(
    "agrega agregue agreguen agrego agregamos agregar agregala agregalo agreguela agreguelo",
    "anade anada anadan anado anadimos anadir anadela anadelo",
    "pon ponga pongan pongo ponemos poner ponla ponlo",
    "anota anote anoten anoto anotamos anotar anotala anotalo",
    "registra registre registren registro registramos registrar registrala registralo",
    "documenta documente documento documentamos documentar",
    "incluye incluya incluyo incluimos incluir",
    "marca marque marco marcamos marcar",
)
ALLERGY_REMOVE = _forms(
    "quita quite quiten quito quitamos quitar quitala quitalo",
    "elimina elimine eliminen elimino eliminamos eliminar eliminala eliminalo",
    "borra borre borren borro borramos borrar borrala borralo",
    "retira retire retiren retiro retiramos retirar",
    "saca saque saquen saco sacamos sacar sacala sacalo",
)
ALLERGY_CHANGE = _forms(
    "actualiza actualice actualizo actualizamos actualizar",
    "cambia cambie cambio cambiamos cambiar",
    "corrige corrija corrijo corregimos corregir",
    "modifica modifique modifico modificamos modificar",
)
# After the allergy is named ("alergia a la sulfa, agrégala"): only the imperative and the infinitive, so a past
# ("cambió") or a first person ("documento") isn't taken for one.
ALLERGY_AFTER = _forms(
    "agrega agregue agregar agregala agregalo agreguela agreguelo anade anada anadir anadela anadelo anota anote anotar",
    "registra registre registrar quita quite quitar quitala quitalo elimina elimine eliminar eliminala eliminalo",
    "borra borre borrar borrala borralo actualiza actualice actualizar",
)
_LIST = r"(?:(?:sus|su|la|las|tu|tus|esta)\s+)?(?:(?:lista|listado|registro)\s+de\s+)?"
_SENTENCE = r"(?:(?![.\n;]).)"

ORDERS = [
    re.compile(rf"\b(?:{ORDER_VERBS})\b"),
    re.compile(r"\b(?:le|les|se)\s+receta(?:n)?\b"),
    re.compile(rf"\b(?:voy|vamos|va|van)\s+a\s+(?:{INFINITIVES})\b"),
    re.compile(rf"\bhay\s+que\s+(?:{INFINITIVES})\b"),
    re.compile(r"\bse\s+(?:pide|piden|ordena|ordenan|solicita|solicitan|prescribe|prescriben)\b"),
    # Sending to the pharmacy, or a prescription or order sent or made: any form of the verb with its object.
    re.compile(rf"\b(?:{SEND_VERBS})\b{_SENTENCE}{{0,60}}?\bfarmacia\b"),
    re.compile(rf"\b(?:{SEND_ONLY})\b{_SENTENCE}{{0,30}}?\b(?:receta|recetas|orden|ordenes|a hacer|al laboratorio|"
               r"estudios?|analisis)\b"),
    re.compile(r"\b(?:renovar|renuevo|renueva|renueve|renovamos|resurtir|resurto|resurta|resurtimos|surtir|surta|"
               r"rellenar|rellene|repetir|repito|repita)\s+(?:(?:la|las|su|sus|esa|esta)\s+)?(?:receta|recetas|"
               r"prescripcion|prescripciones)\b"),
    re.compile(r"\b(?:hacer|hago|haga|hacemos|poner|pongo|ponga|ponemos|meter|meto|meta|metemos|generar|genero|genere|"
               r"generamos|dejar|dejo|deje|dejamos)\s+(?:(?:una|la|las|unas|otra|nueva)\s+)?(?:orden|ordenes|receta|"
               r"recetas|prescripcion)\b"),
    # Code-switching: "un refill", "mándale el refill".
    re.compile(r"\brefills?\b"),
    # The model told to make an orders section: "agrega una sección de órdenes".
    re.compile(r"\b(?:agrega|agregue|agregar|anade|anada|anadir|crea|cree|crear|haz|haga|hacer|incluye|incluya|incluir|"
               rf"pon|ponga|poner|abre|abra|abrir)\b{_SENTENCE}{{0,30}}?\b(?:seccion|campo|apartado|pestana)\s+de\s+"
               r"(?:ordenes|pedidos|orden)\b"),
]
ALLERGY_CHANGES = [
    re.compile(rf"\b(?:{ALLERGY_ADD})\b{_SENTENCE}{{0,50}}?\b(?:a|en|como|entre)\s+{_LIST}(?:alergias?|alergic[oa]s?)\b"),
    re.compile(rf"\b(?:{ALLERGY_REMOVE})\b{_SENTENCE}{{0,50}}?\bde\s+{_LIST}alergias?\b"),
    re.compile(rf"\b(?:{ALLERGY_CHANGE})\s+{_LIST}alergias?\b"),
    re.compile(r"\b(?:agreguese|anadase|anotese|registrese|quitese|eliminese|borrese|retirese|incluyase|marquese)\b"
               rf"{_SENTENCE}{{0,50}}?\balergi"),
    re.compile(r"\bse\s+(?:agrega|agregan|anade|anaden|anota|anotan|registra|registran|quita|quitan|elimina|eliminan|"
               rf"borra|borran|incluye|incluyen)\b{_SENTENCE}{{0,50}}?\balergi"),
    re.compile(r"\b(?:debe|deben|hay que)\s+(?:agregarse|anadirse|anotarse|registrarse|quitarse|eliminarse|agregar|"
               rf"anadir|anotar|registrar|quitar|eliminar)\b{_SENTENCE}{{0,50}}?\balergi"),
    re.compile(rf"\bque\s+se\s+(?:agregue|anada|anote|registre|quite|elimine)\b{_SENTENCE}{{0,50}}?\balergi"),
    re.compile(r"\b(?:dar de baja|inactivar|inactiva|inactive|desactivar|desactiva|desactive)\s+(?:(?:la|esa|esta)\s+)?"
               r"alergia"),
    re.compile(rf"\balergi\w*\b{_SENTENCE}{{0,40}}?\b(?:{ALLERGY_AFTER})\b"),
]
# Who else may ask: "la paciente pide una incapacidad" is not the clinician ordering.
OTHER_SUBJECTS = frozenset(
    """paciente ella madre padre mama papa familia familiar esposo esposa hijo hija hermano hermana cardiologo
    cardiologa medico medica doctor doctora especialista enfermera enfermero""".split()
)
# An instruction not to: "no pidas", "no hace falta pedir", "sin necesidad de solicitar". Unless another negation comes
# just before it, which makes it one again: "no hay razón para no pedir".
NEGATED_INSTRUCTION = re.compile(
    r"\b(?:no|nunca|ya no|todavia no|aun no|no hace falta|no es necesario|no hay que|sin necesidad de|no se debe|"
    r"no debe|no se necesita|no vamos a|no voy a)\s+(?:(?:le|les|lo|la|los|las|se|me|te|nos|a|que|hay)\s+){0,3}$"
)
ANY_NEGATION = frozenset("no nunca nada ningun ninguno ninguna sin razon".split())
UNDOES_REACH = 3  # words before an instruction not to within which another negation makes it an instruction again
# A sentence about something sent already is documentation: "las recetas se enviaron a la farmacia la semana pasada".
PAST = re.compile(r"\b(?:ayer|la semana pasada|el mes pasado|el ano pasado|hace|ya|previamente|anteriormente)\b|"
                  r"\bse\s+(?:envio|enviaron|mando|mandaron)\b|\bfue\s+(?:enviad|mandad)\w*")
SENTENCE_MARK = re.compile(r"[!?\n;]|\.(?!\d)|(?<!\d)\.")
CLAUSE_MARK = re.compile(r"[!?;\n:]|[.,](?!\d)|(?<!\d)[.,]")
LEAD_IN = frozenset("por favor de tambien ademas ahora luego entonces le les se lo la yo nosotros y voy vamos a hay que "
                    "usted ya".split())
LEAD_IN_REACH = 5
DIRECTIVE_REACH = 14
# Words that start another clause, which ends an instruction.
CLAUSE_STARTS = frozenset("pero aunque porque mientras entonces luego despues ademas".split())
# After "y" or a comma, a new subject or another step of the plan starts another clause; anything else ("pido una
# creatinina y un potasio") is more of what is being ordered.
NEW_CLAUSE = NEXT_CLAUSE | frozenset("yo ella el paciente recomiendo explico se le".split())
# After the allergy list, the pharmacy or a prescription these carry the instruction on; anything else ends it.
CARRIES_ON = frozenset("a para de con como del al que".split())
TARGET = re.compile(r"(?:alergias?|alergic[oa]s?|farmacia|receta|recetas|orden|ordenes)$")


def _sentence_bounds(folded: str, at: int) -> tuple[int, int]:
    start = max((m.end() for m in SENTENCE_MARK.finditer(folded, 0, at)), default=0)
    found = SENTENCE_MARK.search(folded, at)
    return start, found.start() if found else len(folded)


def _directive_end(folded: str, toks: list[Token], end: int) -> int:
    _, stop = _sentence_bounds(folded, end)
    end += len(re.match(r"\w*", folded[end:]).group())
    following = [t for t in toks if t.start >= end and t.end <= stop]
    if TARGET.search(folded[:end]) and not (following and following[0].key in CARRIES_ON):
        return end
    joined = False
    for token in following[:DIRECTIVE_REACH]:
        if token.key in CLAUSE_STARTS or (token.key in NEW_CLAUSE and (joined or "," in folded[end : token.start])):
            break
        joined = token.key == "y"
        if not joined:
            end = token.end
    return end


def directives(text: str) -> list[tuple[int, int, str]]:
    """Each instruction in Spanish to place an order ("order") or change the allergy list ("allergy_change"), from its
    lead-in to the end of its clause, overlapping ones merged, an allergy change winning (as validation.directives()
    does in English)."""
    folded = fold(text)
    toks = tokens(text)
    matches: list[tuple[int, int, str]] = []
    for kind, patterns in (("order", ORDERS), ("allergy_change", ALLERGY_CHANGES)):
        for pattern in patterns:
            for match in pattern.finditer(folded):
                matches.append((match.start(), match.end(), kind))
    spans = []
    for start, end, kind in sorted(matches):
        clause = max((m.end() for m in CLAUSE_MARK.finditer(folded, 0, start)), default=0)
        before = folded[clause:start]
        negated = NEGATED_INSTRUCTION.search(before)
        if negated and not any(t.key in ANY_NEGATION for t in tokens(before[: negated.start()])[-UNDOES_REACH:]):
            continue
        verb = folded[start:end].split()[0]
        if verb in THIRD_PERSON and any(t.key in OTHER_SUBJECTS for t in tokens(before)):
            continue
        sentence_start, sentence_end = _sentence_bounds(folded, start)
        if "farmacia" in folded[start:end] and PAST.search(folded[sentence_start:sentence_end]):
            continue
        span_start = start
        for token in reversed([t for t in toks if t.end <= start][-LEAD_IN_REACH:]):
            if token.start < clause or token.key not in LEAD_IN:
                break
            span_start = token.start
        spans.append((span_start, _directive_end(folded, toks, end), kind))
    merged: list[tuple[int, int, str]] = []
    for start, end, kind in sorted(spans):
        if merged and start < merged[-1][1]:
            last = merged[-1]
            merged[-1] = (last[0], max(last[1], end), "allergy_change" if "allergy_change" in (kind, last[2]) else kind)
        else:
            merged.append((start, end, kind))
    return merged
