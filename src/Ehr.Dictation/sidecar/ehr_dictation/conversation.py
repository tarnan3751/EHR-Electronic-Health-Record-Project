"""A recorded visit's transcript (the expansion plan's step 16): MedASR's passages, each with the voice diarize.py gave
it and whether that is sure, and once the clinician has said who each voice is, who said it; and the parts of it K2
Horizon drafts the note from, a window at a time.

The text is the passages joined as a dictation's are (transcript.py), with a line break wherever who is speaking
changes, so the review shows a turn a line and no negation or other cue reaches from one speaker's words into the next
(validation.py ends a cue's reach at a line break). to_json() adds each segment's speakerGroup (its voice) and speaker:
clinician, patient or other as the clinician named its voice, or unsure where diarize.py couldn't tell or the clinician
named the voice "unsure", and null for every segment until the voices are named. A dictation's transcript has neither.
numbered() numbers the voices that said something 1 to k, in the order they first spoke, with none missing, so a voice
diarize.py told apart that MedASR heard nothing from leaves no gap in the voices the clinician names.

windows() cuts the visit into parts of about WINDOW_WORDS words at the line breaks, a turn longer than that at its
passages, for K2 Horizon to draft one at a time (decision D16), so each fits the context a request has and the time it
is given. Each part's text is a line a turn, led by who said it ("Clinician:", "Patient:", "Other:", "Unsure:"), as
conversation-prompt.md describes, with each unclear passage as [unclear].
"""

from __future__ import annotations

import dataclasses
from collections.abc import Mapping, Sequence
from dataclasses import dataclass

from .audio import SAMPLE_RATE
from .textnorm import tokens
from .transcript import UNCLEAR_MARK, Placed, Segment, Transcript, assemble as assemble_passages

ROLES = ("clinician", "patient", "other")
# What the clinician may name a voice: a role, or "unsure", when they can't tell whose it is (its passages are then
# Unsure).
NAMES = (*ROLES, "unsure")
LABELS = {"clinician": "Clinician", "patient": "Patient", "other": "Other", "unsure": "Unsure"}
WINDOW_WORDS = 450


@dataclass(frozen=True)
class Conversation(Transcript):
    voices: tuple[int | None, ...] = ()  # each passage's voice, or None where none was told
    unsure: tuple[bool, ...] = ()  # whether each passage's voice couldn't be told with confidence
    roles: tuple[tuple[int, str], ...] = ()  # each voice's role, once the clinician has named them

    def speaker(self, index: int) -> str | None:
        """Who said the passage: its voice's role, or unsure; None until the voices are named."""
        if not self.roles:
            return None
        if self.unsure[index] or self.voices[index] is None:
            return "unsure"
        return dict(self.roles)[self.voices[index]]

    def named(self, roles: Mapping[int, str]) -> Conversation:
        return dataclasses.replace(self, roles=tuple(sorted(roles.items())))

    def numbered(self) -> Conversation:
        order = {voice: number for number, voice in enumerate(sorted({v for v in self.voices if v is not None}), start=1)}
        return dataclasses.replace(self, voices=tuple(order.get(voice) for voice in self.voices))

    def speakers(self) -> list[dict]:
        """Each voice that said something, in order, with how long its passages last in seconds."""
        seconds: dict[int, float] = {}
        for placed, voice in zip(self.placed, self.voices, strict=True):
            if voice is not None:
                seconds[voice] = seconds.get(voice, 0.0) + (placed.segment.end_sample - placed.segment.start_sample) / SAMPLE_RATE
        return [{"group": voice, "seconds": round(seconds[voice], 1)} for voice in sorted(seconds)]

    def passages(self) -> tuple[tuple[int, int, str], ...]:
        """Each passage's place in the text (code points) and who said it, for the checks (validation.Scope)."""
        return tuple((placed.start, placed.end, self.speaker(i) or "unsure") for i, placed in enumerate(self.placed))

    def to_json(self) -> dict:
        out = super().to_json()
        for i, segment in enumerate(out["segments"]):
            segment["speakerGroup"] = self.voices[i]
            segment["speaker"] = self.speaker(i)
        return out


def assemble(passages: Sequence[tuple[Segment, int | None, bool]]) -> Conversation:
    """The final transcript of a visit's passages, in order, each with its voice and whether that is unsure: a line a
    run of one speaker's passages."""
    text = ""
    placed: list[Placed] = []
    voices: list[int | None] = []
    unsure: list[bool] = []
    run: list[tuple[Segment, int | None, bool]] = []

    def flush() -> None:
        nonlocal text
        if not run:
            return
        part = assemble_passages([segment for segment, _, _ in run], final=True)
        kept = iter(run)
        offset = len(text) + (1 if text and part.text else 0)
        for at in part.placed:
            segment, voice, doubt = next((item for item in kept if item[0] is at.segment))
            placed.append(Placed(segment, at.start + offset, at.end + offset))
            voices.append(voice)
            unsure.append(doubt)
        if part.text:
            text = f"{text}\n{part.text}" if text else part.text
        run.clear()

    for passage in passages:
        if run and (run[-1][1], run[-1][2]) != (passage[1], passage[2]):
            flush()
        run.append(passage)
    flush()
    return Conversation(text, tuple(placed), True, tuple(voices), tuple(unsure))


@dataclass(frozen=True)
class Window:
    """A part of the visit to draft: its passages (first to last, exclusive), where it is in the text, and its text for
    K2 Horizon."""

    first: int
    last: int
    start: int
    end: int
    text: str


def windows(conversation: Conversation, words: int = WINDOW_WORDS) -> list[Window]:
    """The named visit in parts of about `words` words, cut where who is speaking changes (see the header)."""
    lines: list[list[int]] = []
    for i in range(len(conversation.placed)):
        if lines and conversation.speaker(lines[-1][-1]) == conversation.speaker(i):
            lines[-1].append(i)
        else:
            lines.append([i])
    # A turn longer than a window is cut at its passages.
    pieces: list[list[int]] = []
    for line in lines:
        piece: list[int] = []
        for i in line:
            if piece and _words(conversation, [*piece, i]) > words:
                pieces.append(piece)
                piece = []
            piece.append(i)
        pieces.append(piece)

    out: list[Window] = []
    taken: list[list[int]] = []
    for piece in pieces:
        if taken and sum(_words(conversation, p) for p in taken) + _words(conversation, piece) > words:
            out.append(_window(conversation, taken))
            taken = []
        taken.append(piece)
    if taken:
        out.append(_window(conversation, taken))
    return out


def _words(conversation: Conversation, passages: list[int]) -> int:
    return sum(len(tokens(conversation.placed[i].segment.text)) for i in passages)


def _window(conversation: Conversation, pieces: list[list[int]]) -> Window:
    lines = []
    for piece in pieces:
        said = " ".join(
            UNCLEAR_MARK if conversation.placed[i].segment.unclear else conversation.text[conversation.placed[i].start : conversation.placed[i].end]
            for i in piece
        )
        lines.append(f"{LABELS[conversation.speaker(piece[0]) or 'unsure']}: {' '.join(said.split())}")
    first, last = pieces[0][0], pieces[-1][-1] + 1
    return Window(first, last, conversation.placed[first].start, conversation.placed[last - 1].end, "\n".join(lines))
