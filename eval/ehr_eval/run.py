"""Runs the test set through the dictation pipeline and writes the results file CI checks (gate.py).

    uv run --project eval python -m ehr_eval.run --llama-url http://127.0.0.1:8080 [--device mps] [--machine "..."]

with llama-server's key in LLAMA_API_KEY, the models where setup_models.py put them (--models-dir, default
infra/compose/models), and the recordings generate_audio.py made, or a teammate's (--audio, default eval/audio). Each
recording goes through the sidecar's own code: ffmpeg decoding, MedASR transcription cut at pauses, K2 Horizon, and the
checks, timed stage by stage. The two-minute recordings are also fed in one-second chunks, as a live dictation is, to
time the transcript's update after each chunk. Nothing reaches the network but llama-server.

It runs K2 Horizon as the sidecar ships it, reasoning off. --reasoning low256 runs the comparison the owner asked for
instead, reasoning on at "low" capped at 256 tokens (and enable_thinking set, since a llama-server started with
--reasoning off would otherwise keep it off): its results go to eval/results/compare-low256.json, which the gate
doesn't check.

The results go to eval/results/latest.json (--out), stamped with the models' hashes, the prompt, schema and parameters
hash, the pipeline and scoring hashes (scripts.py), the test set's hash and the machine. Record the
accepted baseline on the server machine only (README.md, "Dictation server hardware"): a run on another machine is for
information.
"""

from __future__ import annotations

import argparse
import asyncio
import dataclasses
import json
import os
import platform
import subprocess
import sys
import time
from datetime import UTC, datetime
from pathlib import Path

os.environ.setdefault("HF_HUB_OFFLINE", "1")

from ehr_dictation import audio, prompt, provenance, validation  # noqa: E402
from ehr_dictation.asr import MedAsr  # noqa: E402
from ehr_dictation.llama import LlamaServer, StructuringFailed, Structurer  # noqa: E402
from ehr_dictation.sessions import Sessions  # noqa: E402
from ehr_dictation.settings import Settings  # noqa: E402
from ehr_dictation.transcriber import Progress, advance  # noqa: E402
from ehr_dictation.transcript import assemble  # noqa: E402

from . import scoring  # noqa: E402
from .scripts import EVAL_DIR, gold_sha256, load_all, pipeline_sha256, recordings_sha256, scoring_sha256  # noqa: E402

REPO_ROOT = EVAL_DIR.parent
SCHEMA_VERSION = 1

# The settings a run can use: what ships, and the comparison.
REASONING = {
    "off": prompt.PARAMETERS,
    "low256": prompt.PARAMETERS | {"reasoning_effort": "low", "reasoning_budget_tokens": 256,
                                   "chat_template_kwargs": {"enable_thinking": True}},
}
OUT = {"off": "latest.json", "low256": "compare-low256.json"}


def variant_tags(variant: dict, script_tags: list[str]) -> list[str]:
    noise = f"noise:{variant['noise']}@{variant['snr_db']}dB" if variant.get("noise") else "noise:none"
    return [f"accent:{variant.get('accent', 'unknown')}", noise, f"container:{variant.get('container', 'unknown')}", *script_tags]


async def run_variant(variant: dict, audio_dir: Path, recognizer, structurer: Structurer, scripts) -> scoring.Item:  # noqa: ANN001
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
        return item
    timings["structure"] = time.perf_counter() - started
    item.tokens = {key: int(proposal.timings[key]) for key in ("prompt_n", "cache_n", "predicted_n")
                   if proposal.timings and isinstance(proposal.timings.get(key), int)}

    checks = validation.check(proposal.fields, proposal.unknown, transcript)
    item.unknown = list(checks.unknown)
    item.fields = {
        name: scoring.FieldOutcome(
            proposed=proposal.fields[name]["value"],
            sources=proposal.fields[name]["sources"],
            status="pending" if checked.pending else "empty",
            reasons=checked.reasons,
            value=checked.value if checked.pending else "",
            warnings=[list(span) for span in checked.warnings],
        )
        for name, checked in checks.fields.items()
    }
    return item


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


def machine(label: str | None, device: str) -> dict:
    import torch

    if sys.platform == "darwin":
        cpu = _command(["sysctl", "-n", "machdep.cpu.brand_string"])
        memory = int(_command(["sysctl", "-n", "hw.memsize"]) or 0)
        system = f"macOS {platform.mac_ver()[0]}"
    else:
        cpu = next((line.split(":", 1)[1].strip() for line in _read("/proc/cpuinfo").splitlines()
                    if line.startswith("model name")), platform.processor())
        memory = next((int(line.split()[1]) * 1024 for line in _read("/proc/meminfo").splitlines()
                       if line.startswith("MemTotal")), 0)
        system = f"{platform.system()} {platform.release()}"
    description = f"{cpu}, {round(memory / 2**30)} GB, {system}"
    return {
        "label": label or description,
        "description": description,
        "cpus": os.cpu_count(),
        "python": platform.python_version(),
        "torch": torch.__version__,
        "asr_device": device,
    }


def _command(arguments: list[str]) -> str:
    try:
        return subprocess.run(arguments, capture_output=True, text=True, check=False).stdout.strip()
    except OSError:
        return ""


def _read(path: str) -> str:
    try:
        return Path(path).read_text(encoding="utf-8")
    except OSError:
        return ""


async def main_async(args: argparse.Namespace) -> int:
    api_key = os.environ.get("LLAMA_API_KEY", "")
    if not api_key:
        print("Set LLAMA_API_KEY to llama-server's key.", file=sys.stderr)
        return 2
    manifest_path = args.audio / "manifest.json"
    if not manifest_path.is_file():
        print(f"No {manifest_path}: generate the audio first (eval/README.md).", file=sys.stderr)
        return 2
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    scripts = load_all()
    variants = [v for v in manifest["variants"] if not args.only or v["script"] in args.only]

    models = provenance.load(args.models_dir)
    recognizer = MedAsr(args.models_dir / provenance.MEDASR_DIR, args.device)
    llama = LlamaServer(args.llama_url, api_key)
    structuring = prompt.load(parameters=REASONING[args.reasoning])
    structurer = Structurer(llama, structuring, models.language_file, timeout=Settings.structuring_seconds)
    out = args.out or EVAL_DIR / "results" / OUT[args.reasoning]
    started_at = datetime.now(UTC).isoformat(timespec="seconds")

    items: list[scoring.Item] = []
    for number, variant in enumerate(variants, 1):
        item = await run_variant(variant, args.audio, recognizer, structurer, scripts)
        if item.seconds >= scoring.TWO_MINUTES and variant.get("noise") is None and not args.no_live:
            data = (args.audio / variant["file"]).read_bytes()
            item.chunk_seconds = await live_chunks(data, item.seconds, recognizer, structurer, models)
        items.append(item)
        print(f"[{number}/{len(variants)}] {variant['id']}: {item.sorting}"
              f" ({', '.join(f'{k} {v:.1f}s' for k, v in item.timings.items())})", flush=True)
    await llama.aclose()

    results = {
        "schema_version": SCHEMA_VERSION,
        "baseline": False,
        "run": {
            "started_at": started_at,
            "finished_at": datetime.now(UTC).isoformat(timespec="seconds"),
            "machine": machine(args.machine, args.device),
            "llama_server": structurer.build,
            "reasoning": args.reasoning,
            "parameters": structuring.parameters,
        },
        "provenance": {
            "speech": models.speech,
            "language": models.language,
            "prompt": structuring.prompt_sha256,
            "schema": structuring.schema_sha256,
            "structuring": structuring.structuring_sha256,
            "pipeline": pipeline_sha256(),
            "scoring": scoring_sha256(),
        },
        "dataset": {
            "recordings_sha256": recordings_sha256(),
            "gold_sha256": gold_sha256(),
            "scripts": len({v["script"] for v in variants}),
            "recordings": len(variants),
            "complete": not args.only,
            "audio": manifest.get("generator", {}),
        },
        "metrics": scoring.score(items, scripts),
        "items": [dataclasses.asdict(item) for item in items],
    }
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(results, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Wrote {out}")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--llama-url", required=True, help="llama-server's address, such as http://127.0.0.1:8080")
    parser.add_argument("--models-dir", type=Path, default=REPO_ROOT / "infra" / "compose" / "models")
    parser.add_argument("--audio", type=Path, default=EVAL_DIR / "audio", help="the recordings and their manifest.json")
    parser.add_argument("--device", default="cpu", help="where MedASR runs: cpu, mps (Apple Silicon) or cuda")
    parser.add_argument("--machine", help="a name for this machine in the results (default: its CPU, memory and OS)")
    parser.add_argument("--reasoning", choices=sorted(REASONING), default="off",
                        help="off, as shipped (default), or low256, the comparison")
    parser.add_argument("--out", type=Path, help="default: eval/results/latest.json, or compare-low256.json")
    parser.add_argument("--only", nargs="*", help="script IDs to run, for a quick look; the gate wants all of them")
    parser.add_argument("--no-live", action="store_true", help="skip the one-second-chunk timing")
    return asyncio.run(main_async(parser.parse_args(argv)))


if __name__ == "__main__":
    sys.exit(main())
