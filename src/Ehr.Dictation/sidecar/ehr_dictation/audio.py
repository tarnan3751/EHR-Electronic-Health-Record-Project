"""Turns dictated audio, in whatever container the browser recorded, into the 16 kHz mono samples MedASR takes.

The format is never named here: Chromium records WebM, WebKit MP4, Firefox Ogg, and ffmpeg works out which from the
bytes (AGENTS.md: never hard-code a recording format). MediaRecorder's chunks after the first can't be decoded on their
own, so a session keeps all its audio and decodes the whole of it each time. It goes to ffmpeg through a pipe and comes
back through another: nothing is written to disk. ffmpeg's own messages are never logged or passed on, since they
describe the recording.
"""

from __future__ import annotations

import shutil
import subprocess

import numpy as np

SAMPLE_RATE = 16_000


class AudioError(Exception):
    """The bytes aren't audio ffmpeg can decode. The message says nothing about what was in them."""


def ffmpeg() -> str:
    path = shutil.which("ffmpeg")
    if not path:
        raise AudioError("ffmpeg isn't installed, or isn't on PATH.")
    return path


def decode(data: bytes, *, partial: bool = False, timeout: float = 30.0) -> np.ndarray:
    """The audio as float32 samples at 16 kHz, mono. A recording still being made (partial) usually ends mid-frame:
    what decodes is returned, and an opening chunk too short to decode yet gives no samples rather than an error."""
    if not data:
        return np.zeros(0, dtype=np.float32)
    try:
        completed = subprocess.run(
            [
                ffmpeg(), "-hide_banner", "-nostdin", "-loglevel", "error",
                "-i", "pipe:0", "-vn", "-ac", "1", "-ar", str(SAMPLE_RATE), "-f", "f32le", "pipe:1",
            ],
            input=data,
            capture_output=True,
            timeout=timeout,
            check=False,
        )
    except subprocess.TimeoutExpired:
        raise AudioError("ffmpeg took too long to decode the audio.") from None

    output = completed.stdout[: len(completed.stdout) // 4 * 4]
    samples = np.frombuffer(output, dtype="<f4").astype(np.float32)
    if completed.returncode != 0 and samples.size == 0 and not partial:
        raise AudioError(f"ffmpeg couldn't decode the audio (exit code {completed.returncode}).")
    return samples
