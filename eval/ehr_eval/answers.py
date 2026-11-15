"""What makes a run's answers (run.py): each recording through the sidecar's own code, ffmpeg decoding, MedASR
transcription cut at pauses and K2 Horizon with the generation parameters of the setting run, timed stage by stage, and
a two-minute recording also fed in one-second chunks, as a live dictation is.

Its hash is part of the pipeline hash (scripts.py), so a change to it means running the eval again. What run.py does
with the answers, the checks and the scoring, is done again by rescore.py, so a change there only means scoring again.
"""

from __future__ import annotations

import argparse
import dataclasses
import time
from pathlib import Path

from ehr_dictation import audio, prompt, provenance, validation
from ehr_dictation.asr import MedAsr
from ehr_dictation.llama import LlamaServer, StructuringFailed, Structurer
from ehr_dictation.sessions import Sessions
from ehr_dictation.settings import Settings
from ehr_dictation.transcriber import Progress, advance
from ehr_dictation.transcript import assemble

from . import scoring

# The settings a run can use: what ships, and the comparison.
REASONING = {
    "off": prompt.PARAMETERS,
    "low256": prompt.PARAMETERS | {"reasoning_effort": "low", "reasoning_budget_tokens": 256,
                                   "chat_template_kwargs": {"enable_thinking": True}},
}


def open_pipeline(args: argparse.Namespace, api_key: str):  # noqa: ANN201
    """The models' provenance, MedASR, llama-server, the prompt with the setting's parameters, and K2 Horizon."""
    models = provenance.load(args.models_dir)
    recognizer = MedAsr(args.models_dir / provenance.MEDASR_DIR, args.device)
    llama = LlamaServer(args.llama_url, api_key)
    structuring = prompt.load(parameters=REASONING[args.reasoning])
    structurer = Structurer(llama, structuring, models.language_file, timeout=Settings.structuring_seconds)
    return models, recognizer, llama, structuring, structurer


def variant_tags(variant: dict, script_tags: list[str]) -> list[str]:
    noise = f"noise:{variant['noise']}@{variant['snr_db']}dB" if variant.get("noise") else "noise:none"
    return [f"accent:{variant.get('accent', 'unknown')}", noise, f"container:{variant.get('container', 'unknown')}", *script_tags]


async def answer(variant: dict, audio_dir: Path, recognizer, structurer: Structurer, scripts):  # noqa: ANN001, ANN201
    """The recording's item, without the checks, with the transcript and K2 Horizon's proposal (None if there is
    none) for them."""
    script = scripts[variant["script"]]
    data = (audio_dir / variant["file"]).read_bytes()
    timings: dict[str, float] = {}

    started = time.perf_counter()
    samples = audio.decode(data)
    timings["decode"] = time.perf_counter() - started

    started = time.perf_counter()
    progress = advance(samples, Progress(), recognizer, final=True)
    transcript = assemble(progress.segments, final=True)
    timings["transcribe"] = time.perf_counter() - started

    item = scoring.Item(
        variant=variant["id"],
        script=script.id,
        tags=variant_tags(variant, script.tags),
        seconds=samples.size / audio.SAMPLE_RATE,
        hypothesis=transcript.text,
        sorting="done",
        not_carried_over=[(kind, start, end) for start, end, kind in validation.directives(transcript.text)],
        segments=[dataclasses.asdict(placed.segment) for placed in transcript.placed],
        unclear_passages=len(transcript.unclear_ranges()),
        timings=timings,
    )
    started = time.perf_counter()
    try:
        proposal = await structurer.propose(transcript.for_structuring())
    except StructuringFailed as error:
        item.sorting, item.reason = "unavailable", error.reason
        timings["structure"] = time.perf_counter() - started
        return item, transcript, None
    timings["structure"] = time.perf_counter() - started
    item.tokens = {key: int(proposal.timings[key]) for key in ("prompt_n", "cache_n", "predicted_n")
                   if proposal.timings and isinstance(proposal.timings.get(key), int)}
    return item, transcript, proposal


async def live_chunks(data: bytes, seconds: float, recognizer, structurer: Structurer, models) -> list[float]:  # noqa: ANN001
    """The time to bring the transcript up to date after each one-second chunk, through the sidecar's sessions."""
    settings = Settings(api_key="eval", llama_api_key="eval", max_session_seconds=3600, max_session_bytes=1 << 30)
    sessions = Sessions(settings, recognizer, structurer, models)
    session = sessions.start("eval")
    size = max(1, int(len(data) / max(seconds, 1.0)))
    times = []
    for seq, offset in enumerate(range(0, len(data), size)):
        started = time.perf_counter()
        await sessions.add_audio(session.id, "eval", seq, data[offset : offset + size])
        times.append(time.perf_counter() - started)
    sessions.cancel(session.id, "eval")
    return times
