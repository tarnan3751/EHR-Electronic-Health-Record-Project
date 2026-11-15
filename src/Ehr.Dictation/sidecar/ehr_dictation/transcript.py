"""The transcript a clinician sees: MedASR's passages, formatted and joined, each with where it sits in the text.

MedASR writes spoken punctuation as {period}, {comma}, {colon}, {new paragraph} and so on, and a spoken heading as
[HEADING]; format() turns those into punctuation, line breaks and "Heading:" lines. Anything else in braces is kept as
the words it says. Positions inside this module are code points; to_json() gives UTF-16 offsets, which is how C# and
JavaScript index strings, so the review screen can highlight what a field came from (FR-08) without converting.

A passage marked unclear (quality.py) keeps its text here, but for_structuring() replaces it with [unclear], so
K2 Horizon never sorts words that couldn't be trusted.
"""

from __future__ import annotations

import re
from collections.abc import Sequence
from dataclasses import dataclass

from .audio import SAMPLE_RATE

UNCLEAR_MARK = "[unclear]"

COMMANDS = {
    "period": ".", "full stop": ".", "comma": ",", "colon": ":", "semicolon": ";", "question mark": "?",
    "exclamation point": "!", "exclamation mark": "!", "hyphen": "-", "dash": "-",
    "open parenthesis": "(", "open paren": "(", "close parenthesis": ")", "close paren": ")",
    "new line": "\n", "next line": "\n", "new paragraph": "\n\n", "next paragraph": "\n\n",
}
PIECE = re.compile(r"\{([^{}]*)\}|\[([^\[\]]*)\]|<[^<>]*>|([^\s{}\[\]<>]+)")
ATTACHES = ".,:;?!)"


def format(raw: str) -> str:  # noqa: A001 (the module's one job)
    """MedASR's text as a person would write it."""
    out = ""
    skip_colon = False
    for match in PIECE.finditer(raw):
        command, heading, word = match.group(1), match.group(2), match.group(3)
        if command is not None:
            spoken = " ".join(command.lower().split())
            mark = COMMANDS.get(spoken)
            if mark is None:
                out, skip_colon = (_word(out, spoken) if spoken else out), False
            elif mark == ":" and skip_colon:
                skip_colon = False
            else:
                out, skip_colon = _mark(out, mark), False
        elif heading is not None:
            title = " ".join(heading.split()).capitalize()
            if title:
                out = out.rstrip(" ")
                if out and not out.endswith("\n"):
                    out += "\n"
                out += title + ":"
                skip_colon = True
        elif word is not None:
            out, skip_colon = _word(out, word), False
        # Anything in angle brackets is a model token, such as </s>.
    return out.strip()


def _word(out: str, word: str) -> str:
    return out + word if not out or out[-1] in " \n(-" else f"{out} {word}"


def _mark(out: str, mark: str) -> str:
    if mark.startswith("\n"):
        out = out.rstrip(" \n")
        return out + mark if out else out
    if mark == "(":
        return out + ("(" if not out or out[-1] in " \n" else " (")
    return out.rstrip(" ") + mark


@dataclass(frozen=True)
class Segment:
    """One passage: its samples in the recording, MedASR's text, and whether it can be trusted."""

    start_sample: int
    end_sample: int
    raw: str
    confidence: float | None
    unclear: str | None
    final: bool

    @property
    def text(self) -> str:
        return format(self.raw) or (UNCLEAR_MARK if self.unclear else "")


@dataclass(frozen=True)
class Placed:
    segment: Segment
    start: int
    end: int


@dataclass(frozen=True)
class Transcript:
    text: str
    placed: tuple[Placed, ...]
    final: bool

    def unclear_ranges(self) -> list[tuple[int, int]]:
        return [(p.start, p.end) for p in self.placed if p.segment.unclear]

    def for_structuring(self) -> str:
        text = ""
        for placed in self.placed:
            text = _join(text, UNCLEAR_MARK if placed.segment.unclear else placed.segment.text)
        return text

    def to_json(self) -> dict:
        return {
            "text": self.text,
            "final": self.final,
            "segments": [
                {
                    "start": utf16_offset(self.text, p.start),
                    "end": utf16_offset(self.text, p.end),
                    "startMs": p.segment.start_sample * 1000 // SAMPLE_RATE,
                    "endMs": p.segment.end_sample * 1000 // SAMPLE_RATE,
                    "final": p.segment.final,
                    "unclear": p.segment.unclear,
                }
                for p in self.placed
            ],
        }


def assemble(segments: Sequence[Segment], *, final: bool) -> Transcript:
    text = ""
    placed: list[Placed] = []
    for segment in segments:
        piece = segment.text
        if not piece:
            continue
        joined = _join(text, piece)
        placed.append(Placed(segment, len(joined) - len(piece), len(joined)))
        text = joined
    return Transcript(text, tuple(placed), final)


def _join(text: str, piece: str) -> str:
    if not text:
        return piece
    if piece[0] in ATTACHES or piece[0] == "\n" or text.endswith("\n"):
        return text + piece
    return f"{text} {piece}"


def utf16_offset(text: str, index: int) -> int:
    """The code-point index as a count of UTF-16 code units, which is what C# and JavaScript use."""
    return index + sum(1 for character in text[:index] if ord(character) > 0xFFFF)
