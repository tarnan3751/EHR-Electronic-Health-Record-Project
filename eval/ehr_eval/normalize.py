"""How a transcript and its reference are made comparable before counting word errors: lower case, no punctuation,
spoken punctuation and fillers dropped, hyphenated words split, "142/88" and "142 over 88" alike, numbers as digits
whether written or spoken ("fifty-four" and "54" are the same word), and a contraction as the words it stands for
("I've" and "I have", "don't" and "do not", "it's", "it is" and "it has" are the same words). Both sides go through the
same steps, using the sidecar's own number reading (ehr_dictation.textnorm), so the score counts what MedASR got wrong
and not how it chose to write it: it writes out most of what people say short, which counted as two errors a word.
"""

from __future__ import annotations

import re

from ehr_dictation.textnorm import ORDINAL_TENS, ORDINAL_UNITS, TENS, UNITS, numbers

FILLERS = frozenset("um uh er ah hmm mm".split())
SPOKEN_PUNCTUATION = re.compile(r"\{[^{}]*\}")
NUMBER_WORDS = frozenset(UNITS) | frozenset(TENS) | frozenset(ORDINAL_UNITS) | frozenset(ORDINAL_TENS) | {"hundred", "thousand"}
TOKEN = re.compile(r"[a-z]+(?:'[a-z]+)?|\d+(?:\.\d+)?(?:st|nd|rd|th)?")
# A contraction's words written out; "'s" and "'d" are two words each ("is" or "has", "would" or "had"), so after the
# subjects they follow, those are written one way, "is" and "would", wherever they come from.
SHORT = {"'m": "am", "'re": "are", "'ve": "have", "'ll": "will"}
WRITTEN_OUT = {"can't": ["can", "not"], "cannot": ["can", "not"], "won't": ["will", "not"], "shan't": ["shall", "not"],
               "let's": ["let", "us"]}
BEFORE_IS = frozenset("it that there here he she what who where how".split())
BEFORE_WOULD = frozenset("i you we they he she it that there who".split())


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
    return _written_out(out)


def _written_out(tokens: list[str]) -> list[str]:
    """Each contraction as the words it stands for, "is" for "has" and "would" for "had" after a subject (see above)."""
    out: list[str] = []
    for token in tokens:
        head, _, tail = token.partition("'")
        if token in WRITTEN_OUT:
            out += WRITTEN_OUT[token]
        elif token.endswith("n't"):
            out += [token[:-3], "not"]
        elif tail and f"'{tail}" in SHORT:
            out += [head, SHORT[f"'{tail}"]]
        elif tail == "s" and head in BEFORE_IS:
            out += [head, "is"]
        elif tail == "d" and head in BEFORE_WOULD:
            out += [head, "would"]
        else:
            out.append(token)
    for i in range(1, len(out)):
        if out[i] == "has" and out[i - 1] in BEFORE_IS:
            out[i] = "is"
        elif out[i] == "had" and out[i - 1] in BEFORE_WOULD:
            out[i] = "would"
    return out


def normalize(text: str) -> str:
    return " ".join(words(text))
