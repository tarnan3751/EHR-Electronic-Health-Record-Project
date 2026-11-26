"""What makes a Spanish run's answers (run_es.py, the expansion plan's step 17): each recording through the sidecar's
own code as a dictation in Spanish goes, ffmpeg decoding, Whisper transcription cut at pauses and K2 Horizon with the
Spanish prompt and schema, timed stage by stage; a recording dictated as English the English way instead (MedASR and
the English prompt); every clean recording dictated in Spanish also fed in one-second chunks through the sidecar's
sessions, as a live Spanish dictation is, and then stopped, so its final pass runs as a stop runs it; and each script's
display text alone (run_a.py), to measure the translation without speech recognition.

Each passage Whisper transcribes keeps the probabilities its verdict read (whisper_asr.Whisper.hear()): that it was
Spanish, that it held no speech, and its words' mean log-probability. With them the results can show every passage of
the scripts that mix English into Spanish beside how likely it was Spanish (Spanish is forced on every passage, so
English heard as Spanish may come out written in Spanish nobody said), and every passage left out by the no-speech
rule alone, which Whisper's own decoding would only apply with a low log-probability too.

The live run times the sidecar's one transcription lock (Sessions' _transcribing, shared by English and Spanish
dictations), replaced here by a lock that times each hold: its longest hold while the chunks come in, and the hold of
the final pass after stop. An English dictation on the same server skips its live updates, and its stop waits, for as
long as a Spanish pass holds it.

Its hash is part of the pipeline hash (scripts.py), shared with the English set: a change to it means running both
evals again. What run_es.py does with the answers, the checks and the scoring, may be redone without the models.
"""

from __future__ import annotations

import argparse
import asyncio
import dataclasses
import time
from dataclasses import dataclass, field
from pathlib import Path

from ehr_dictation import audio, prompt, provenance, spanish, validation
from ehr_dictation.asr import MedAsr, Recognition, Recognizer
from ehr_dictation.llama import LlamaServer, Proposal, StructuringFailed, Structurer
from ehr_dictation.sessions import Sessions, Spanish
from ehr_dictation.settings import Settings
from ehr_dictation.transcriber import Progress, advance
from ehr_dictation.transcript import Segment, Transcript, assemble
from ehr_dictation.whisper_asr import Heard, Whisper

from .scoring_es import SpanishItem

FINAL_PASS_SECONDS = 600.0  # how long the live run waits for the final pass after stop


@dataclass
class Pipeline:
    models: provenance.Models
    whisper: Recognizer
    medasr: Recognizer
    llama: LlamaServer
    spanish: Structurer
    english: Structurer


def open_pipeline(args: argparse.Namespace, api_key: str) -> Pipeline:
    """The models' provenance (Whisper's included, which the run needs), Whisper, MedASR, llama-server, and K2 Horizon
    asked as the sidecar asks it for each language."""
    models = provenance.load(args.models_dir)
    if models.spanish_speech is None:
        raise SystemExit("models-manifest.json records no Whisper: run src/Ehr.Dictation/setup_models.py --only whisper.")
    llama = LlamaServer(args.llama_url, api_key)
    return Pipeline(
        models,
        Whisper.load(args.models_dir / provenance.WHISPER_DIR, args.device),
        MedAsr(args.models_dir / provenance.MEDASR_DIR, args.device),
        llama,
        Structurer(llama, prompt.load(language="es"), models.language_file, timeout=Settings.structuring_seconds),
        Structurer(llama, prompt.load(), models.language_file, timeout=Settings.structuring_seconds),
    )


class Listening:
    """Whisper, keeping each passage it hears with the probabilities its verdict read. A stand-in without hear() gives
    none."""

    def __init__(self, whisper: Recognizer) -> None:
        self.whisper = whisper
        self.heard: list[tuple[int, Heard]] = []

    def recognize(self, samples):  # noqa: ANN001, ANN201
        hear = getattr(self.whisper, "hear", None)
        heard = hear(samples) if hear else Heard(self.whisper.recognize(samples))
        self.heard.append((samples.size, heard))
        return heard.recognition

    def probabilities(self, segments: list[Segment]) -> list[dict]:
        """For each passage of the transcript, in order, the probabilities its verdict read: that it was Spanish, that
        it held no speech, and its words' mean log-probability (each None where none was given)."""
        found, at = [], 0
        for segment in segments:
            while at < len(self.heard) and (self.heard[at][0], self.heard[at][1].recognition.text) != (
                    segment.end_sample - segment.start_sample, segment.raw):
                at += 1
            heard = self.heard[at][1] if at < len(self.heard) else Heard(Recognition("", None))
            found.append({"spanish": heard.spanish, "no_speech": heard.no_speech,
                          "mean_log_probability": heard.mean_log_probability})
            at += 1
        return found


def variant_tags(variant: dict, script_tags: list[str]) -> list[str]:
    noise = f"noise:{variant['noise']}@{variant['snr_db']}dB" if variant.get("noise") else "noise:none"
    tags = [f"accent:{variant.get('accent', 'unknown')}", noise, f"container:{variant.get('container', 'unknown')}"]
    return tags + ([f"run-as:{variant['run_as']}"] if variant.get("run_as") else []) + list(script_tags)


async def answer(variant: dict, audio_dir: Path, pipeline: Pipeline,
                 tags: list[str]) -> tuple[SpanishItem, Transcript, Proposal | None]:
    """The recording's item, without the checks, with its transcript and K2 Horizon's proposal (None if there is none)
    for them. Dictated as Spanish unless the variant is run as English."""
    english = variant.get("run_as") == "en"
    data = (audio_dir / variant["file"]).read_bytes()
    timings: dict[str, float] = {}

    started = time.perf_counter()
    samples = audio.decode(data)
    timings["decode"] = time.perf_counter() - started

    started = time.perf_counter()
    listening = Listening(pipeline.whisper)
    progress = advance(samples, Progress(), pipeline.medasr if english else listening, final=True)
    transcript = assemble(progress.segments, final=True)
    timings["transcribe"] = time.perf_counter() - started

    found = validation.directives(transcript.text) if english else spanish.directives(transcript.text)
    item = SpanishItem(
        variant=variant["id"], script=variant["script"], tags=variant_tags(variant, tags), hypothesis=transcript.text,
        sorting="done", not_carried_over=[(kind, start, end) for start, end, kind in found], timings=timings,
        seconds=samples.size / audio.SAMPLE_RATE, run_as=variant.get("run_as"),
        segments=[dataclasses.asdict(placed.segment) for placed in transcript.placed],
        heard=[] if english else listening.probabilities([placed.segment for placed in transcript.placed]),
    )
    started = time.perf_counter()
    try:
        proposal = await (pipeline.english if english else pipeline.spanish).propose(transcript.for_structuring())
    except StructuringFailed as error:
        timings["structure"] = time.perf_counter() - started
        item.sorting, item.reason, item.detail = "unavailable", error.reason, str(error)
        return item, transcript, None
    timings["structure"] = time.perf_counter() - started
    item.tokens = {key: int(proposal.timings[key]) for key in ("prompt_n", "cache_n", "predicted_n")
                   if proposal.timings and isinstance(proposal.timings.get(key), int)}
    return item, transcript, proposal


class TimedLock:
    """The sessions' one transcription lock, timing each hold: what an English dictation on the same server waits
    behind while a Spanish pass holds it."""

    def __init__(self) -> None:
        self._lock = asyncio.Semaphore(1)
        self._since = 0.0
        self.holds: list[float] = []

    async def acquire(self) -> bool:
        await self._lock.acquire()
        self._since = time.perf_counter()
        return True

    def release(self) -> None:
        self.holds.append(time.perf_counter() - self._since)
        self._lock.release()

    async def __aenter__(self) -> TimedLock:
        await self.acquire()
        return self

    async def __aexit__(self, *exc: object) -> None:
        self.release()


@dataclass
class Live:
    chunk_seconds: list[float] = field(default_factory=list)  # the time to answer each one-second chunk
    longest_live_hold: float | None = None  # the transcription lock's longest hold while the chunks came in
    final_hold: float | None = None  # its hold for the final pass after stop


async def live_chunks(data: bytes, seconds: float, pipeline: Pipeline) -> Live:
    """A dictation in Spanish through the sidecar's sessions, one second at a time, then stopped: the time to answer
    each chunk, and how long its passes held the transcription lock, live and for the final pass."""
    settings = Settings(api_key="eval", llama_api_key="eval", max_spanish_seconds=3600, max_session_bytes=1 << 30)
    sessions = Sessions(settings, pipeline.medasr, pipeline.english, pipeline.models,
                        spanish=Spanish(pipeline.whisper, pipeline.spanish))
    lock = sessions._transcribing = TimedLock()  # noqa: SLF001 (the eval's instrument in the sidecar's own lock's place)
    session = sessions.start("eval", language="es")
    size = max(1, int(len(data) / max(seconds, 1.0)))
    live = Live()
    for seq, offset in enumerate(range(0, len(data), size)):
        started = time.perf_counter()
        await sessions.add_audio(session.id, "eval", seq, data[offset : offset + size])
        live.chunk_seconds.append(time.perf_counter() - started)
    live.longest_live_hold = max(lock.holds, default=None)
    held = len(lock.holds)
    await sessions.stop(session.id, "eval")
    waited = time.perf_counter()
    while len(lock.holds) == held and time.perf_counter() - waited < FINAL_PASS_SECONDS:
        await asyncio.sleep(0.05)
    live.final_hold = lock.holds[held] if len(lock.holds) > held else None
    sessions.cancel(session.id, "eval")  # K2 Horizon's sorting isn't timed here
    return live
