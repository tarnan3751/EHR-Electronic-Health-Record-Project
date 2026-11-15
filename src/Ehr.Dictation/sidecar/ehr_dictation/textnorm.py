"""Word-level text handling shared by validation and the eval: words with their positions, numbers read the same way
whether written as digits or as words, and plurals read as the singular.

MedASR writes most numbers as digits already. Number words still turn up, in what it leaves unformatted and in the eval's
reference scripts, so both sides of every comparison go through numbers() and come out as the same digits.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

WORD = re.compile(r"[A-Za-z]+(?:['’][A-Za-z]+)*|\d+(?:\.\d+)?")


@dataclass(frozen=True)
class Token:
    """A word or a number, lower-cased, and where it sits in the text (code points, end exclusive)."""

    text: str
    start: int
    end: int


def tokens(text: str) -> list[Token]:
    return [Token(m.group().lower().replace("’", "'"), m.start(), m.end()) for m in WORD.finditer(text)]


def words(text: str) -> list[str]:
    return [token.text for token in tokens(text)]


UNITS = {
    "zero": 0, "one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7, "eight": 8, "nine": 9,
    "ten": 10, "eleven": 11, "twelve": 12, "thirteen": 13, "fourteen": 14, "fifteen": 15, "sixteen": 16,
    "seventeen": 17, "eighteen": 18, "nineteen": 19,
}
TENS = {"twenty": 20, "thirty": 30, "forty": 40, "fifty": 50, "sixty": 60, "seventy": 70, "eighty": 80, "ninety": 90}
ORDINAL_UNITS = {
    "first": 1, "second": 2, "third": 3, "fourth": 4, "fifth": 5, "sixth": 6, "seventh": 7, "eighth": 8, "ninth": 9,
    "tenth": 10, "eleventh": 11, "twelfth": 12, "thirteenth": 13, "fourteenth": 14, "fifteenth": 15,
    "sixteenth": 16, "seventeenth": 17, "eighteenth": 18, "nineteenth": 19,
}
ORDINAL_TENS = {"twentieth": 20, "thirtieth": 30}
ORDINAL_SUFFIX = re.compile(r"^(\d+)(?:st|nd|rd|th)$")


def normalize_number(text: str) -> str:
    """'007' is '7', '2.50' is '2.5' and '10.0' is '10': one spelling per value."""
    if "." in text:
        whole, fraction = text.split(".", 1)
        fraction = fraction.rstrip("0")
        whole = whole.lstrip("0") or "0"
        return f"{whole}.{fraction}" if fraction else whole
    return text.lstrip("0") or "0"


def numbers(text: str) -> list[str]:
    """Every number in the text, in order, as digits: '142/88', 'one hundred forty-two over eighty-eight' and
    'March 3rd' give ['142', '88'], ['142', '88'] and ['3']. Number words combine the usual way ('forty two' is 42,
    'one hundred and five' is 105); a word that can't continue a number starts the next one ('one forty' is 1 and 40)."""
    found: list[str] = []
    value: int | None = None
    group = 0
    last = ""  # what the previous number word was: unit, teen, tens or hundred

    def flush() -> None:
        nonlocal value, group, last
        if value is not None:
            found.append(str(value + group))
        value, group, last = None, 0, ""

    for raw in re.findall(r"[A-Za-z]+|\d+(?:\.\d+)?(?:st|nd|rd|th)?", text):
        word = raw.lower()
        if word[0].isdigit():
            flush()
            ordinal = ORDINAL_SUFFIX.match(word)
            found.append(normalize_number(ordinal.group(1) if ordinal else word))
            continue
        if word in UNITS or word in ORDINAL_UNITS:
            number = UNITS.get(word, ORDINAL_UNITS.get(word, 0))
            kind = "teen" if number >= 10 else "unit"
            if value is not None and not (last == "tens" and kind == "unit") and last != "hundred":
                flush()
            value = value or 0
            group += number
            last = kind
            if word in ORDINAL_UNITS:
                flush()
        elif word in TENS or word in ORDINAL_TENS:
            if value is not None and last not in ("hundred",):
                flush()
            value = value or 0
            group += TENS.get(word, ORDINAL_TENS.get(word, 0))
            last = "tens"
            if word in ORDINAL_TENS:
                flush()
        elif word == "hundred" and value is not None and last in ("unit", "teen"):
            group *= 100
            last = "hundred"
        elif word == "thousand" and value is not None:
            value += (group or 1) * 1000
            group = 0
            last = "hundred"
        elif word == "and" and last == "hundred":
            continue
        else:
            flush()
    flush()
    return found


def stem(word: str) -> str:
    """The word without a plural or possessive ending, so 'pressures' and 'allergies' meet 'pressure' and 'allergy'.
    Nothing else is taken off: a tense or another ending can change what a word says ('rule' and 'ruled', 'resolving'
    and 'resolved')."""
    word = word.lower().removesuffix("'s")
    if word.endswith("ies") and len(word) > 4:
        return word[:-3] + "y"
    if word.endswith("s") and not word.endswith(("ss", "us", "is")) and len(word) > 3:
        return word[:-1]
    return word


STOPWORDS = frozenset(
    """a about above after again against all am an and any are as at be because been before being below between both
    but by can could did do does doing down during each few for from further had has have having he her here hers
    herself him himself his how i if in into is it its itself just me more most my myself nor of off on once only or
    other our ours ourselves out over own same she should so some such than that the their theirs them themselves then
    there these they this those through to too under until up very was we were what when where which while who whom why
    will with would you your yours yourself yourselves also per via mg""".split()
)
