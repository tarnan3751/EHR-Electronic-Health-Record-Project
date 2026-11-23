"""What makes a Spanish run's answers (run_es.py, the expansion plan's step 17): each recording through the sidecar's
own code as a dictation in Spanish goes, ffmpeg decoding, Whisper transcription cut at pauses and K2 Horizon with the
Spanish prompt and schema, timed stage by stage; a recording dictated as English the English way instead (MedASR and
the English prompt); the recordings of a minute and a half or more also fed in one-second chunks through the sidecar's
sessions, as a live Spanish dictation is; and each script's display text alone (run_a.py), to measure the
translation without speech recognition. Each passage Whisper transcribes keeps the probability that it was Spanish,
as its language check read it (whisper_asr.Whisper.hear()), so the results can show every passage of the scripts that
mix English into Spanish beside it: Spanish is forced on every passage, so English heard as Spanish may come out
written in Spanish nobody said.

Its hash is part of the pipeline hash (scripts.py), shared with the English set: a change to it means running both
evals again. What run_es.py does with the answers, the checks and the scoring, may be redone without the models.
"""

from __future__ import annotations

import argparse
import dataclasses
import time
from dataclasses import dataclass
from pathlib import Path

from ehr_dictation import audio, prompt, provenance, spanish, validation
from ehr_dictation.asr import MedAsr, Recognizer
from ehr_dictation.llama import LlamaServer, Proposal, StructuringFailed, Structurer
from ehr_dictation.sessions import Sessions, Spanish
from ehr_dictation.settings import Settings
from ehr_dictation.transcriber import Progress, advance
from ehr_dictation.transcript import Segment, Transcript, assemble
from ehr_dictation.whisper_asr import Heard, Whisper

from .scoring_es import SpanishItem

LIVE_SECONDS = 90.0  # a recording this long or longer is also fed in one-second chunks


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
    """Whisper, keeping each passage it hears with the probability that it was Spanish. A stand-in without hear()
    gives none."""

    def __init__(self, whisper: Recognizer) -> None:
        self.whisper = whisper
        self.heard: list[tuple[int, Heard]] = []

    def recognize(self, samples):  # noqa: ANN001, ANN201
        hear = getattr(self.whisper, "hear", None)
        heard = hear(samples) if hear else Heard(self.whisper.recognize(samples))
        self.heard.append((samples.size, heard))
        return heard.recognition

    def spanish(self, segments: list[Segment]) -> list[float | None]:
        """For each passage of the transcript, in order, the probability that it was Spanish."""
        found, at = [], 0
        for segment in segments:
            while at < len(self.heard) and (self.heard[at][0], self.heard[at][1].recognition.text) != (
                    segment.end_sample - segment.start_sample, segment.raw):
                at += 1
            found.append(self.heard[at][1].spanish if at < len(self.heard) else None)
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
        heard_as_spanish=[] if english else listening.spanish([placed.segment for placed in transcript.placed]),
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


async def live_chunks(data: bytes, seconds: float, pipeline: Pipeline) -> list[float]:
    """The time to answer each one-second chunk of a dictation in Spanish, through the sidecar's sessions."""
    settings = Settings(api_key="eval", llama_api_key="eval", max_spanish_seconds=3600, max_session_bytes=1 << 30)
    sessions = Sessions(settings, pipeline.medasr, pipeline.english, pipeline.models,
                        spanish=Spanish(pipeline.whisper, pipeline.spanish))
    session = sessions.start("eval", "es")
    size = max(1, int(len(data) / max(seconds, 1.0)))
    times = []
    for seq, offset in enumerate(range(0, len(data), size)):
        started = time.perf_counter()
        await sessions.add_audio(session.id, "eval", seq, data[offset : offset + size])
        times.append(time.perf_counter() - started)
    sessions.cancel(session.id, "eval")
    return times
