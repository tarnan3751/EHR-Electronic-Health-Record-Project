"""How a transcript and its reference are made comparable before counting word errors: lower case, no punctuation,
spoken punctuation and fillers dropped, hyphenated words split, "142/88" and "142 over 88" alike, and numbers as digits
whether written or spoken ("fifty-four" and "54" are the same word). Both sides go through the same steps, using the
sidecar's own number reading (ehr_dictation.textnorm), so the score counts what MedASR got wrong and not how it chose to
write it.
"""

from __future__ import annotations

import re

from ehr_dictation.textnorm import ORDINAL_TENS, ORDINAL_UNITS, TENS, UNITS, numbers

FILLERS = frozenset("um uh er ah hmm mm".split())
SPOKEN_PUNCTUATION = re.compile(r"\{[^{}]*\}")
NUMBER_WORDS = frozenset(UNITS) | frozenset(TENS) | frozenset(ORDINAL_UNITS) | frozenset(ORDINAL_TENS) | {"hundred", "thousand"}
TOKEN = re.compile(r"[a-z]+(?:'[a-z]+)?|\d+(?:\.\d+)?(?:st|nd|rd|th)?")


def words(text: str) -> list[str]:
    text = SPOKEN_PUNCTUATION.sub(" ", text.lower().replace("’", "'"))
    text = re.sub(r"(\d)\s*/\s*(\d)", r"\1 over \2", text)
    text = text.replace("-", " ")
    raw = [t for t in TOKEN.findall(text) if t not in FILLERS]

    out: list[str] = []
    run: list[str] = []

    def flush() -> None:
        if run:
            # "and" joins number words only after hundred or thousand ("one hundred and five"); one left at the end of a
            # run is a word again.
            trailing = run[-1] == "and"
            out.extend(numbers(" ".join(run[:-1] if trailing else run)))
            if trailing:
                out.append("and")
            run.clear()

    for token in raw:
        if token in NUMBER_WORDS or (token == "and" and run and run[-1] in ("hundred", "thousand")):
            run.append(token)
            continue
        flush()
        out.extend(numbers(token) if token[0].isdigit() else [token])
    flush()
    return out


def normalize(text: str) -> str:
    return " ".join(words(text))
