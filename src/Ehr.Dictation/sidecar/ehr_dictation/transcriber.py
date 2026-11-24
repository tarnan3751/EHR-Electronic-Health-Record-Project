"""Brings a session's transcript up to date with the audio it has so far, transcribing only what changed.

Committed passages are never transcribed again. Each call cuts the uncommitted audio at the pauses segmenter.py finds,
transcribes and commits each finished passage, and, while the clinician is still speaking, transcribes the rest as a
tentative tail the next call replaces. At the end (final), everything is committed. Each passage is judged by quality.py
as it is transcribed; silence makes no passage. The eval calls advance() on a whole recording at once, final, which is
what stopping a session does with whatever audio was never committed.

A recorded visit (the expansion plan's step 16) is transcribed once it has stopped, a turn at a time (passages()), each
turn cut at its own pauses and judged against the whole recording's background, as advance() judges a dictation's.
"""

from __future__ import annotations

from dataclasses import dataclass, field

import numpy as np

from . import quality, segmenter
from .asr import Recognizer
from .transcript import Segment, format


@dataclass(frozen=True)
class Progress:
    segments: list[Segment] = field(default_factory=list)
    committed: int = 0  # samples
    tail: Segment | None = None

    @property
    def all_segments(self) -> list[Segment]:
        return self.segments + ([self.tail] if self.tail else [])


def advance(samples: np.ndarray, progress: Progress, recognizer: Recognizer, *, final: bool) -> Progress:
    frame_levels = segmenter.levels(samples)
    floor = segmenter.noise_floor(frame_levels)
    segments = list(progress.segments)
    committed = min(progress.committed, samples.size)

    for cut in segmenter.cut_points(frame_levels, committed // segmenter.HOP, frame_levels.size, final=final):
        end = cut * segmenter.HOP
        if (segment := _passage(samples, frame_levels, floor, committed, end, recognizer, final=True)) is not None:
            segments.append(segment)
        committed = end

    tail = None
    if final:
        if (segment := _passage(samples, frame_levels, floor, committed, samples.size, recognizer, final=True)) is not None:
            segments.append(segment)
        committed = samples.size
    elif samples.size - committed >= segmenter.SAMPLE_RATE // 2:
        tail = _passage(samples, frame_levels, floor, committed, samples.size, recognizer, final=False)
    return Progress(segments, committed, tail)


def passages(samples: np.ndarray, frame_levels: np.ndarray, floor: float, start: int, end: int,
             recognizer: Recognizer) -> list[Segment]:
    """The final passages of samples[start:end], one speaker's turn: cut at its pauses, transcribed and judged, with
    frame_levels and floor measured over the whole recording (segmenter.levels() and noise_floor())."""
    cuts = segmenter.cut_points(frame_levels, start // segmenter.HOP, min(-(-end // segmenter.HOP), frame_levels.size),
                                final=True)
    edges = [start, *(cut * segmenter.HOP for cut in cuts), end]
    found = [_passage(samples, frame_levels, floor, low, high, recognizer, final=True) for low, high in zip(edges, edges[1:])]
    return [segment for segment in found if segment is not None]


def _passage(
    samples: np.ndarray, frame_levels: np.ndarray, floor: float, start: int, end: int, recognizer: Recognizer, *, final: bool
) -> Segment | None:
    if end <= start:
        return None
    passage_levels = frame_levels[start // segmenter.HOP : max(start // segmenter.HOP + 1, end // segmenter.HOP)]
    if quality.is_silence(passage_levels):
        return None
    recognition = recognizer.recognize(samples[start:end])
    has_text = bool(format(recognition.text))
    unclear = quality.assess(passage_levels, floor, recognition.confidence, has_text)
    if not has_text and unclear is None:
        return None
    return Segment(start, end, recognition.text, recognition.confidence, unclear, final)
