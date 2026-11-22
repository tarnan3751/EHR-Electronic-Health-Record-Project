"""Shared by the sidecar's tests: synthetic audio (tones, silence and noise, never a voice), the containers browsers
record it in, made with ffmpeg in memory, and stand-ins for MedASR, Whisper and llama-server. No test needs the models, a
network or a GPU; the audio tests need ffmpeg on PATH, as the sidecar itself does.
"""

from __future__ import annotations

import json
import subprocess
from pathlib import Path

import httpx2
import numpy as np
import pytest

from ehr_dictation import prompt
from ehr_dictation.asr import Recognition
from ehr_dictation.audio import SAMPLE_RATE
from ehr_dictation.llama import LlamaServer, Structurer
from ehr_dictation.provenance import Models
from ehr_dictation.settings import Settings

MODEL_FILE = "K2-Horizon-7B-Q4_K_M.gguf"


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
    """Stands in for MedASR: one word per half second of audio it's given, and a fixed confidence. Set `hold` to make
    it wait, in its thread, until the event is set: a transcription still running."""

    def __init__(self, word: str = "speech", confidence: float = 0.9) -> None:
        self.word = word
        self.confidence = confidence
        self.calls: list[int] = []
        self.hold = None

    def recognize(self, samples: np.ndarray) -> Recognition:
        if self.hold is not None:
            self.hold.wait(10)
        self.calls.append(samples.size)
        count = int(samples.size / (SAMPLE_RATE / 2))
        return Recognition(" ".join([self.word] * count), self.confidence if count else None)


class FakeWhisper(FakeRecognizer):
    """Stands in for Whisper in a dictation in Spanish: the same Spanish sentence for every passage, with no
    confidence and a verdict of its own, `unclear`, for passages the predicate `marks` picks (by their samples)."""

    def __init__(self, text: str = "Tos seca desde hace 3 días.", unclear: str | None = None, marks=None) -> None:  # noqa: ANN001
        super().__init__()
        self.text = text
        self.unclear = unclear
        self.marks = marks or (lambda samples: True)

    def recognize(self, samples: np.ndarray) -> Recognition:
        if self.hold is not None:
            self.hold.wait(10)
        self.calls.append(samples.size)
        return Recognition(self.text, None, self.unclear if self.marks(samples) else None)


def spanish_answer(*statements: tuple[str, list[str], str], **extra) -> str:
    """K2 Horizon's answer to the Spanish schema: (field, Spanish passages, English) for each statement."""
    return json.dumps({"statements": [{"spanish": spanish, "field": field, "english": english}
                                      for field, spanish, english in statements]} | extra)


def raw_decoder(data: bytes, *, partial: bool = False, **_: object) -> np.ndarray:
    """Stands in for ffmpeg in the session tests: the bytes are float32 samples already."""
    return np.frombuffer(data[: len(data) // 4 * 4], dtype="<f4").astype(np.float32)


def answer(subjective: tuple[str, list[str]] = ("", []), objective=("", []), assessment=("", []), plan=("", []), **extra) -> str:
    fields = {"subjective": subjective, "objective": objective, "assessment": assessment, "plan": plan}
    return json.dumps({name: {"value": v, "sources": s} for name, (v, s) in fields.items()} | extra)


class FakeLlama:
    """A llama-server on httpx2's mock transport: records each request and answers as told."""

    def __init__(self, content: str | None = None, *, model_path: str = f"/models/{MODEL_FILE}", status: int = 200,
                 delay: float = 0.0, props_delay: float = 0.0, finish_reason: str = "stop", unreachable: bool = False,
                 n_ctx: int | None = 4096) -> None:
        self.content = content if content is not None else answer()
        self.model_path = model_path
        self.status = status
        self.delay = delay
        self.props_delay = props_delay
        self.finish_reason = finish_reason
        self.unreachable = unreachable
        self.n_ctx = n_ctx
        self.cancelled = False
        self.requests: list[httpx2.Request] = []

    async def handle(self, request: httpx2.Request) -> httpx2.Response:
        import asyncio

        self.requests.append(request)
        if self.unreachable:
            raise httpx2.ConnectError("Connection refused", request=request)
        if request.url.path == "/props":
            await asyncio.sleep(self.props_delay)
            settings = {"n_ctx": self.n_ctx} if self.n_ctx is not None else {}
            return httpx2.Response(200, json={"model_path": self.model_path, "build_info": "b11669-620d2225",
                                              "default_generation_settings": settings})
        if request.url.path == "/tokenize":
            # One token a word: near enough for the context check.
            return httpx2.Response(200, json={"tokens": list(range(len(json.loads(request.content)["content"].split())))})
        if self.delay:
            try:
                await asyncio.sleep(self.delay)
            except asyncio.CancelledError:
                self.cancelled = True
                raise
        if self.status != 200:
            return httpx2.Response(self.status, json={"error": {"message": "nope"}})
        return httpx2.Response(200, json={
            "choices": [{"finish_reason": self.finish_reason,
                         "message": {"role": "assistant", "content": self.content, "reasoning_content": "thinking"}}],
            "timings": {"prompt_n": 10, "predicted_n": 20},
        })

    def structurer(self, timeout: float = 5.0, language: str = "en") -> Structurer:
        server = LlamaServer("http://llama.test", "llama-key", transport=httpx2.MockTransport(self.handle))
        return Structurer(server, prompt.load(language=language), MODEL_FILE, timeout=timeout)


MODELS = Models(
    speech={"repository": "google/medasr", "commit": "a" * 40, "sha256": "b" * 64},
    language={"repository": "IFM/K2-Horizon-7B-GGUF", "commit": "c" * 40, "file": MODEL_FILE, "sha256": "d" * 64},
)
# With Whisper installed, for dictation in Spanish.
WHISPER = {"repository": "openai/whisper-large-v3-turbo", "commit": "41f01f3fe87f28c78e2fbf8b568835947dd65ed9",
           "sha256": "e" * 64}
MODELS_WITH_WHISPER = Models(MODELS.speech, MODELS.language, WHISPER)


@pytest.fixture
def settings(tmp_path: Path) -> Settings:
    return Settings(api_key="dictation-key", llama_api_key="llama-key", models_dir=tmp_path)
