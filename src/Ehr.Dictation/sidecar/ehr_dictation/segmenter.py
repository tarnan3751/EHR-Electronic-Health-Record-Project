"""Where to cut a growing recording so live transcripts stay cheap: at pauses.

The audio a session has so far is measured in 10 ms frames (loudness in dBFS over 25 ms). Everything before the last
committed cut is final and never transcribed again. After it, each pause of at least 0.4 s that ends at least 3 s
before the end of the audio is a safe place to commit, since the speaker has moved on, so passages run from pause to
pause. Without a pause, a stretch longer than 20 s is cut at its quietest 100 ms, so no piece MedASR has to read again
grows without limit. At the end of a dictation the last 3 s are committed too.
"""

from __future__ import annotations

import numpy as np

from .audio import SAMPLE_RATE

HOP = SAMPLE_RATE // 100  # 10 ms
WINDOW = SAMPLE_RATE * 25 // 1000  # 25 ms
FRAMES_PER_SECOND = SAMPLE_RATE // HOP

MIN_PAUSE = 40  # frames: 0.4 s
KEEP = 300  # frames: the last 3 s stay tentative while recording
MIN_SEGMENT = 100  # frames: 1 s
MAX_SEGMENT = 2000  # frames: 20 s
CUT_WINDOW = 10  # frames: a forced cut lands in the quietest 100 ms...
CUT_SEARCH = 500  # ...of the last 5 s of the stretch


def levels(samples: np.ndarray) -> np.ndarray:
    """Loudness of each 10 ms frame, in dBFS (silence bottoms out at -100)."""
    if samples.size < WINDOW:
        return np.zeros(0)
    squares = np.concatenate(([0.0], np.cumsum(samples.astype(np.float64) ** 2)))
    starts = np.arange(0, samples.size - WINDOW + 1, HOP)
    energy = (squares[starts + WINDOW] - squares[starts]) / WINDOW
    return 20 * np.log10(np.maximum(np.sqrt(np.maximum(energy, 0)), 1e-5))


def noise_floor(frame_levels: np.ndarray) -> float:
    """The background: the 10th percentile of the frames, never below -90 dBFS."""
    if frame_levels.size == 0:
        return -90.0
    return max(-90.0, float(np.percentile(frame_levels, 10)))


def pause_threshold(frame_levels: np.ndarray) -> float:
    """A frame quieter than this is part of a pause: within the bottom 30% of the range from the background to the
    speech, which is the median of the frames at least 10 dB above the background."""
    floor = noise_floor(frame_levels)
    above = frame_levels[frame_levels > floor + 10]
    speech = float(np.median(above)) if above.size else floor
    return floor + max(3.0, 0.3 * (speech - floor))


def cut_points(frame_levels: np.ndarray, start: int, end: int, *, final: bool) -> list[int]:
    """Frame indices to cut at, after `start`, in order. While recording the last KEEP frames are left alone."""
    quiet = frame_levels < pause_threshold(frame_levels)
    limit = end if final else end - KEEP
    cuts: list[int] = []
    while limit - start > MIN_SEGMENT:
        window_end = min(limit, start + MAX_SEGMENT)
        cut = _first_pause_middle(quiet, start + MIN_SEGMENT, window_end)
        if cut is None:
            if limit - start <= MAX_SEGMENT:
                break
            cut = _quietest(frame_levels, window_end - CUT_SEARCH, window_end)
        cuts.append(cut)
        start = cut
    return cuts


def _first_pause_middle(quiet: np.ndarray, low: int, high: int) -> int | None:
    stretch = quiet[low:high]
    if stretch.size == 0:
        return None
    edges = np.flatnonzero(np.diff(np.concatenate(([False], stretch, [False])).astype(np.int8)))
    for begin, stop in zip(edges[0::2], edges[1::2], strict=True):
        # A pause still going at the end of the window may not be over.
        if stop == stretch.size:
            break
        if stop - begin >= MIN_PAUSE:
            return low + int(begin + stop) // 2
    return None


def _quietest(frame_levels: np.ndarray, low: int, high: int) -> int:
    stretch = frame_levels[low:high]
    averaged = np.convolve(stretch, np.ones(CUT_WINDOW) / CUT_WINDOW, mode="valid")
    return low + int(np.argmin(averaged)) + CUT_WINDOW // 2
