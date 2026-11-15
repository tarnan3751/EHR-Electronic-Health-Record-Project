"""Finds the dates in a transcript, however they came out ("March 14th, 2026", "3/14/26", "the fourteenth of March",
"march fourteen twenty twenty six"), so the eval can say whether each date a script says was transcribed right.

A script lists its dates as ISO dates (2026-03-14), or --03-14 when no year is said. A date counts as right when the
transcript has that month and day, and the same year whenever the script says one.
"""

from __future__ import annotations

from .normalize import words

MONTHS = {
    name: number
    for number, names in enumerate(
        [["january", "jan"], ["february", "feb"], ["march"], ["april", "apr"], ["may"], ["june"], ["july"],
         ["august", "aug"], ["september", "sept", "sep"], ["october", "oct"], ["november", "nov"],
         ["december", "dec"]],
        start=1,
    )
    for name in names
}

Date = tuple[int | None, int, int]  # year, month, day


def parse(iso: str) -> Date:
    if iso.startswith("--"):
        month, day = iso[2:].split("-")
        return None, int(month), int(day)
    year, month, day = iso.split("-")
    return int(year), int(month), int(day)


def find(text: str) -> set[Date]:
    tokens = words(text)
    found: set[Date] = set()
    for i, token in enumerate(tokens):
        if token in MONTHS:
            month = MONTHS[token]
            # March 14 [2026]
            if i + 1 < len(tokens) and _day(tokens[i + 1]):
                found.add((_year(tokens, i + 2), month, int(tokens[i + 1])))
            # 14 [of] March [2026]
            before = tokens[i - 2] if i >= 2 and tokens[i - 1] == "of" else tokens[i - 1] if i >= 1 else ""
            if _day(before):
                found.add((_year(tokens, i + 1), month, int(before)))
        # 3 over 14 [over 2026]: how "3/14/2026" normalizes
        if token.isdigit() and i + 2 < len(tokens) and tokens[i + 1] == "over" and tokens[i + 2].isdigit():
            month, day = int(token), int(tokens[i + 2])
            if 1 <= month <= 12 and 1 <= day <= 31:
                year = None
                if i + 4 < len(tokens) and tokens[i + 3] == "over" and tokens[i + 4].isdigit():
                    year = _full_year(int(tokens[i + 4]))
                found.add((year, month, day))
    return found


def said_right(iso: str, text: str) -> bool:
    year, month, day = parse(iso)
    return any(m == month and d == day and (year is None or y == year) for y, m, d in find(text))


def _day(token: str) -> bool:
    return token.isdigit() and 1 <= int(token) <= 31


def _year(tokens: list[str], i: int) -> int | None:
    if i < len(tokens) and tokens[i].isdigit():
        value = int(tokens[i])
        if 1900 <= value <= 2100:
            return value
        # "twenty twenty six" reads as 20 then 26.
        if value in (19, 20) and i + 1 < len(tokens) and tokens[i + 1].isdigit() and int(tokens[i + 1]) < 100:
            return value * 100 + int(tokens[i + 1])
    return None


def _full_year(value: int) -> int:
    return value + 2000 if value < 100 else value
