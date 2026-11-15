"""Shared by the sidecar's tests: synthetic audio (tones, silence and noise, never a voice), the containers browsers
record it in, made with ffmpeg in memory, and a stand-in for MedASR. No test needs the models, a network or a GPU; the
audio tests need ffmpeg on PATH, as the sidecar itself does.
"""

from __future__ import annotations

import subprocess

import numpy as np

from ehr_dictation.asr import Recognition
from ehr_dictation.audio import SAMPLE_RATE


def tone(seconds: float, level_db: float = -20.0, frequency: float = 220.0) -> np.ndarray:
    """A steady tone whose loudness (RMS) is level_db dBFS: stands in for speech."""
    t = np.arange(int(seconds * SAMPLE_RATE)) / SAMPLE_RATE
    return (np.sqrt(2) * 10 ** (level_db / 20) * np.sin(2 * np.pi * frequency * t)).astype(np.float32)


def noise(seconds: float, level_db: float = -70.0, seed: int = 1) -> np.ndarray:
    """White noise at level_db dBFS RMS: a room's background."""
    rng = np.random.default_rng(seed)
    return (rng.standard_normal(int(seconds * SAMPLE_RATE)) * 10 ** (level_db / 20)).astype(np.float32)


def speech_and_pauses(*parts: tuple[str, float], background_db: float = -70.0) -> np.ndarray:
    """("speech", 2.0), ("pause", 1.0), ...: tone where there is speech, over a quiet background throughout."""
    pieces = [tone(seconds) if kind == "speech" else np.zeros(int(seconds * SAMPLE_RATE), np.float32) for kind, seconds in parts]
    samples = np.concatenate(pieces)
    return samples + noise(samples.size / SAMPLE_RATE, background_db)


ENCODINGS = {
    # Chromium's MediaRecorder: WebM with Opus. Firefox's: Ogg with Opus. WebKit's: MP4 with AAC, fragmented so it
    # can be read as it grows. WAV, which some recorders and the eval's own files use.
    "webm": ["-c:a", "libopus", "-b:a", "48k", "-f", "webm"],
    "ogg": ["-c:a", "libopus", "-b:a", "48k", "-f", "ogg"],
    "mp4": ["-c:a", "aac", "-b:a", "64k", "-movflags", "frag_keyframe+empty_moov+default_base_moof", "-f", "mp4"],
    "wav": ["-c:a", "pcm_s16le", "-f", "wav"],
}


def encode(samples: np.ndarray, container: str, rate: int = 48_000) -> bytes:
    """The samples as a browser would record them, through pipes: nothing on disk."""
    return subprocess.run(
        ["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "f32le", "-ar", str(SAMPLE_RATE), "-ac", "1",
         "-i", "pipe:0", "-ar", str(rate), *ENCODINGS[container], "pipe:1"],
        input=samples.astype("<f4").tobytes(),
        capture_output=True,
        check=True,
    ).stdout


class FakeRecognizer:
    """Stands in for MedASR: one word per half second of audio it's given, and a fixed confidence."""

    def __init__(self, word: str = "speech", confidence: float = 0.9) -> None:
        self.word = word
        self.confidence = confidence
        self.calls: list[int] = []

    def recognize(self, samples: np.ndarray) -> Recognition:
        self.calls.append(samples.size)
        count = int(samples.size / (SAMPLE_RATE / 2))
        return Recognition(" ".join([self.word] * count), self.confidence if count else None)
