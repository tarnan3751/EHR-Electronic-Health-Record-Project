"""Which passages are too quiet, too noisy or too uncertain to trust, so the clinician can dictate them again (FR-10).

A passage is judged by its speech, the frames more than 6 dB above the recording's background: their median loudness,
how far that is above the background (a signal-to-noise estimate), and MedASR's own confidence in what it heard. The
thresholds are starting values, tuned against the eval's noisy recordings (eval/README.md). A marked passage keeps its
text, marked, but is left out of what K2 Horizon sorts.
"""

from __future__ import annotations

import numpy as np

TOO_QUIET = "too_quiet"
TOO_NOISY = "too_noisy"
LOW_CONFIDENCE = "low_confidence"

QUIET_DBFS = -42.0
NOISY_SNR_DB = 10.0
CONFIDENCE = 0.55
SPEECH_MARGIN_DB = 6.0
MIN_SPEECH_FRAMES = 10  # 0.1 s
SOUND_MARGIN_DB = 10.0
MIN_SOUND_FRAMES = 50  # 0.5 s


def assess(frame_levels: np.ndarray, floor: float, confidence: float | None, has_text: bool) -> str | None:
    """Why the passage can't be trusted, or None. A passage MedASR heard nothing in is marked only if it holds half a
    second or more of sound well above the background, which is speech it couldn't make out; a breath, a click or
    silence is None, and the caller drops it."""
    if not has_text and np.count_nonzero(frame_levels > floor + SOUND_MARGIN_DB) < MIN_SOUND_FRAMES:
        return None
    speech = frame_levels[frame_levels > floor + SPEECH_MARGIN_DB]
    if speech.size >= MIN_SPEECH_FRAMES:
        level = float(np.median(speech))
    elif frame_levels.size:
        level = float(np.percentile(frame_levels, 90))
    else:
        level = floor
    if level < QUIET_DBFS:
        return TOO_QUIET
    if level - floor < NOISY_SNR_DB:
        return TOO_NOISY
    if not has_text or (confidence is not None and confidence < CONFIDENCE):
        return LOW_CONFIDENCE
    return None


# Quieter than this throughout, a passage is silence and isn't worth transcribing. Speech buried in noise is louder, and
# is transcribed and then judged by assess().
SILENCE_DBFS = -60.0


def is_silence(frame_levels: np.ndarray) -> bool:
    return frame_levels.size == 0 or float(frame_levels.max()) < SILENCE_DBFS
