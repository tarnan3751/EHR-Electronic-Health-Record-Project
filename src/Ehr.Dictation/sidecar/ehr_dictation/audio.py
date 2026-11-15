"""Turns dictated audio, in whatever container the browser recorded, into the 16 kHz mono samples MedASR takes.

The format is never named here: Chromium records WebM, WebKit MP4, Firefox Ogg, and ffmpeg works out which from the
bytes (AGENTS.md: never hard-code a recording format). MediaRecorder's chunks after the first can't be decoded on their
own, so a session keeps all its audio and decodes the whole of it each time. It goes to ffmpeg through a pipe and comes
back through another: nothing is written to disk. ffmpeg's own messages are never logged or passed on, since they
describe the recording.

A small file can hold hours of near-silence, so the decoded length is capped (max_seconds): a recording longer than a
session may be decodes to just past the limit, never to gigabytes. A decode also has a time limit (DECODE_SECONDS), well
inside the on-prem app's 20 seconds for a call; running out of it (DecodeTimeout) means the server was busy, not that
the bytes weren't audio.
"""

from __future__ import annotations

import shutil
import subprocess

import numpy as np

SAMPLE_RATE = 16_000
DECODE_SECONDS = 10.0


class AudioError(Exception):
    """The bytes aren't audio ffmpeg can decode. The message says nothing about what was in them."""


class DecodeTimeout(AudioError):
    """ffmpeg didn't finish in time: the server was busy, which says nothing about the audio."""


def ffmpeg() -> str:
    path = shutil.which("ffmpeg")
    if not path:
        raise AudioError("ffmpeg isn't installed, or isn't on PATH.")
    return path


def decode(
    data: bytes, *, partial: bool = False, max_seconds: float | None = None, timeout: float = DECODE_SECONDS
) -> np.ndarray:
    """The audio as float32 samples at 16 kHz, mono, at most max_seconds of it. A recording still being made (partial)
    usually ends mid-frame: what decodes is returned, and an opening chunk too short to decode yet gives no samples
    rather than an error."""
    if not data:
        return np.zeros(0, dtype=np.float32)
    limit = ["-t", f"{max_seconds:.3f}"] if max_seconds is not None else []
    try:
        completed = subprocess.run(
            [
                ffmpeg(), "-hide_banner", "-nostdin", "-loglevel", "error",
                "-i", "pipe:0", "-vn", "-ac", "1", "-ar", str(SAMPLE_RATE), *limit, "-f", "f32le", "pipe:1",
            ],
            input=data,
            capture_output=True,
            timeout=max(timeout, 0.1),
            check=False,
        )
    except subprocess.TimeoutExpired:
        raise DecodeTimeout("ffmpeg took too long to decode the audio.") from None

    output = completed.stdout
    whole = len(output) // 4 * 4
    # A view of ffmpeg's output, not a copy: float32 little-endian is what every server and laptop here uses natively.
    samples = np.frombuffer(output if whole == len(output) else memoryview(output)[:whole], dtype="<f4")
    if completed.returncode != 0 and samples.size == 0 and not partial:
        raise AudioError(f"ffmpeg couldn't decode the audio (exit code {completed.returncode}).")
    return samples.astype(np.float32, copy=False)
