"""Runs the eval's visits through the pipeline for listening to a visit and writes eval/results/ambient-latest.json,
which the gate checks beside latest.json (ambient_gate.py).

    uv run --project eval python -m ehr_eval.ambient --llama-url http://127.0.0.1:8080 --device mps [--machine "..."]
    uv run --project eval python -m ehr_eval.ambient --contention --llama-url http://127.0.0.1:8080 --device mps
    uv run --project eval python -m ehr_eval.ambient --rescore eval/results/ambient-latest.json
    uv run --project eval python -m ehr_eval.ambient --resume --llama-url http://127.0.0.1:8080 --device mps

with llama-server's key in LLAMA_API_KEY, the models where setup_models.py put them (--models-dir, default
infra/compose/models), the speaker model among them, and the recordings generate_conversations.py made (--audio,
default eval/audio/conversations). Each recording goes through the sidecar's own code (listening.py): decoding,
telling the voices apart, MedASR a turn at a time, the voices named as the recording's turns say, K2 Horizon a window
at a time as the sidecar drafts them (ehr_dictation.listening.drafted()). Each recording's raw answers, everything
--rescore needs, are written to the results file as soon as the recording is done, before anything is scored; once
all are in, the checks, the merging and the measures run (ambient_scoring), and if they fail the file still holds
every answer, for --rescore to finish. --resume skips the recordings a file from the same prompt, code and recordings
already holds. The long recordings are also fed in five-second chunks, to time each chunk's answer. Nothing reaches
the network but llama-server.

--contention adds to the results file a measure of K2 Horizon shared (decision D16): the dictation set's longest clean
recording (--dictation-audio, default eval/audio) sorted once alone and twice at once, then the same while the longest
visit's first window is being drafted, through the sidecar's own turns, under which the window gives way to the first
dictation (listening.contend()); each dictation beside a window is compared with its time without one. It reads the
window from the results, so run it after the eval.

--rescore checks a run's stored window proposals again with the current checks and scores them, without the models,
as rescore.py does for the dictations; it refuses results from another prompt, pipeline or recordings.

The results are stamped with the models' hashes (the speaker model's included), the conversation prompt's structuring
hash, the conversations' pipeline, scoring, recordings and gold hashes (conversations.py) and the machine. As with the
dictations, record an accepted baseline (ambient-baseline.json) on the server machine only.
"""

from __future__ import annotations

import argparse
import asyncio
import dataclasses
import json
import os
import sys
from datetime import UTC, datetime
from pathlib import Path

os.environ.setdefault("HF_HUB_OFFLINE", "1")

from ehr_dictation import conversation, ecapa, prompt, provenance  # noqa: E402
from ehr_dictation.asr import MedAsr  # noqa: E402
from ehr_dictation.llama import LlamaServer, Structurer  # noqa: E402
from ehr_dictation.settings import Settings  # noqa: E402

from . import ambient_scoring, answers, listening  # noqa: E402
from .ambient_scoring import VisitItem  # noqa: E402
from .conversations import (  # noqa: E402
    gold_sha256, load_conversations, pipeline_sha256, recordings_sha256, scoring_sha256,
)
from .run import machine  # noqa: E402
from .scripts import EVAL_DIR, load_all  # noqa: E402

REPO_ROOT = EVAL_DIR.parent
OUT = EVAL_DIR / "results" / "ambient-latest.json"
SCHEMA_VERSION = 1


def conversation_prompt(parameters: dict | None = None) -> prompt.Structuring:
    return prompt.load_conversation(parameters=parameters)


def variant_tags(variant: dict, visit) -> list[str]:  # noqa: ANN001
    return [f"noise:{variant.get('noise') or 'none'}", f"container:{variant.get('container', 'unknown')}",
            f"voices:{visit.speakers}", *visit.tags]


async def run_variant(variant: dict, audio_dir: Path, recognizer, embedder, structurer: Structurer, visits) -> VisitItem:  # noqa: ANN001
    """The recording through the pipeline, its raw answers stored: nothing checked or scored yet."""
    visit = visits[variant["script"]]
    heard = listening.hear((audio_dir / variant["file"]).read_bytes(), visit.speakers, variant["turns"], recognizer, embedder)
    said = heard.conversation
    item = VisitItem(
        variant=variant["id"], script=visit.id, tags=variant_tags(variant, visit), seconds=variant["seconds"],
        hypothesis=said.text, sorting="done",
        segments=[dataclasses.asdict(placed.segment) for placed in said.placed], voices=list(said.voices),
        unsure=list(said.unsure), doubts=heard.doubts, names={str(k): v for k, v in heard.names.items()},
        turns=variant["turns"], timings=heard.timings,
        proposes=listening.proposes(recognizer, embedder, structurer, None),
    )
    if not heard.names:
        item.sorting, item.reason = "unnamed", "voices_not_named"
        return item
    await listening.draft(heard, structurer)
    item.timings, item.sizes = heard.timings, heard.sizes
    item.windows = [
        {"first": w.first, "last": w.last, "start": w.start, "end": w.end, "seconds": round(seconds, 2),
         "proposal": {"fields": p.fields, "unknown": p.unknown} if p else None, "failed": reason,
         "tokens": {k: int(p.timings[k]) for k in ("prompt_n", "cache_n", "predicted_n")
                    if p and p.timings and isinstance(p.timings.get(k), int)}}
        for w, p, seconds, reason in heard.tries
    ]
    if heard.reason:
        item.sorting, item.reason = "unavailable", heard.reason
    return item


def results(items: list[VisitItem], run: dict, manifest: dict, models: provenance.Models, structuring: prompt.Structuring,
            complete: bool) -> dict:
    """A results file of raw answers, not scored yet (scored())."""
    return {
        "schema_version": SCHEMA_VERSION,
        "baseline": False,
        "run": run,
        "provenance": {
            "speech": models.speech,
            "language": models.language,
            "speaker": models.speaker,
            "prompt": structuring.prompt_sha256,
            "schema": structuring.schema_sha256,
            "structuring": structuring.structuring_sha256,
            "pipeline": pipeline_sha256(),
            "scoring": scoring_sha256(),
        },
        "dataset": {
            "recordings_sha256": recordings_sha256(),
            "gold_sha256": gold_sha256(),
            "scripts": len({item.script for item in items}),
            "recordings": len(items),
            "complete": complete,
            "audio": manifest.get("generator", {}),
        },
        "metrics": None,
        "items": [dataclasses.asdict(item) for item in items],
    }


def scored(results_file: dict, visits: dict | None = None) -> dict:
    """The file's stored answers checked and scored (ambient_scoring.restored(), score())."""
    visits = visits or {visit.id: visit for visit in load_conversations()}
    items = ambient_scoring.restored(results_file, visits)
    return results_file | {"metrics": ambient_scoring.score(items, visits), "items": [dataclasses.asdict(item) for item in items]}


def _write(out: Path, data: dict) -> None:
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(data, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")


async def evaluate(args: argparse.Namespace) -> int:
    """The run (see the header)."""
    api_key = os.environ.get("LLAMA_API_KEY", "")
    manifest_path = args.audio / "manifest.json"
    if not api_key or not manifest_path.is_file():
        print("Set LLAMA_API_KEY, and generate the visits' audio first (eval/README.md).", file=sys.stderr)
        return 2
    models = provenance.load(args.models_dir)
    if models.speaker is None:
        print("The speaker model isn't installed: uv run src/Ehr.Dictation/setup_models.py --only speaker", file=sys.stderr)
        return 2
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    visits = {visit.id: visit for visit in load_conversations()}
    variants = [v for v in manifest["variants"] if not args.only or v["script"].split("-")[0] in args.only or v["script"] in args.only]
    recognizer = MedAsr(args.models_dir / provenance.MEDASR_DIR, args.device)
    embedder = ecapa.Ecapa(args.models_dir / provenance.SPEAKER_DIR / provenance.SPEAKER_WEIGHTS, models.speaker["sha256"],
                           args.device)
    llama = LlamaServer(args.llama_url, api_key)
    structuring = conversation_prompt()
    structurer = Structurer(llama, structuring, models.language_file, timeout=Settings.structuring_seconds)
    out = args.out or OUT
    items: list[VisitItem] = []
    run = {"started_at": datetime.now(UTC).isoformat(timespec="seconds"), "machine": machine(args.machine, args.device),
           "parameters": structuring.parameters}
    if args.resume and out.is_file():
        stored = json.loads(out.read_text(encoding="utf-8"))
        problem = _stale(stored)
        if problem:
            print(f"--resume can't use {out}: {problem}", file=sys.stderr)
            return 2
        items = [VisitItem(**item) for item in stored["items"]]
        run = stored["run"]
    done = {item.variant for item in items}

    for number, variant in enumerate(variants, 1):
        if variant["id"] in done:
            continue
        item = await run_variant(variant, args.audio, recognizer, embedder, structurer, visits)
        if variant["seconds"] >= listening.LONG_SECONDS and variant.get("noise") is None and not args.no_live:
            data = (args.audio / variant["file"]).read_bytes()
            item.chunk_seconds = await listening.chunk_seconds(data, variant["seconds"], recognizer, embedder, structurer, models)
        items.append(item)
        windows = ", ".join(f"{w['seconds']:.0f}{'!' if w['failed'] else ''}" for w in item.windows)
        print(f"[{number}/{len(variants)}] {variant['id']}: {item.sorting}"
              f" ({', '.join(f'{k} {v:.1f}s' for k, v in item.timings.items())}; windows {windows} s)", flush=True)
        run |= {"finished_at": datetime.now(UTC).isoformat(timespec="seconds"), "llama_server": structurer.build}
        _write(out, results(items, run, manifest, models, structuring, not args.only and len(items) == len(variants)))
    await llama.aclose()
    raw = results(items, run, manifest, models, structuring, not args.only and len(items) == len(variants))
    _write(out, raw)
    _write(out, scored(raw))
    print(f"Wrote {out}")
    return 0


def _stale(results_file: dict) -> str | None:
    """Why a results file's answers can't be used with this code and these recordings, or None."""
    if results_file["provenance"]["structuring"] != conversation_prompt(results_file["run"]["parameters"]).structuring_sha256:
        return "it came from another prompt, schema or parameters: run the eval again instead"
    if results_file["dataset"].get("recordings_sha256") != recordings_sha256():
        return "it came from other recordings: generate the audio and run the eval again instead"
    if results_file["provenance"].get("pipeline") != pipeline_sha256():
        return "it came from other code making the transcript or the answers: run the eval again"
    return None


async def contention(args: argparse.Namespace) -> int:
    """K2 Horizon shared: the longest clean dictation sorted alone, then while a visit's window drafts (see the header)."""
    api_key = os.environ.get("LLAMA_API_KEY", "")
    out = args.out or OUT
    if not api_key or not out.is_file():
        print("Set LLAMA_API_KEY, and run the visits' eval first.", file=sys.stderr)
        return 2
    stored = json.loads(out.read_text(encoding="utf-8"))
    longest = max((i for i in stored["items"] if i["sorting"] == "done"), key=lambda i: i["seconds"])
    item = VisitItem(**{k: v for k, v in longest.items() if k != "fields"})
    window = conversation.windows(ambient_scoring.rebuild(item), conversation.WINDOW_TOKENS, item.sizes)[0]
    dictations = json.loads((args.dictation_audio / "manifest.json").read_text(encoding="utf-8"))["variants"]
    dictation = max((v for v in dictations if v.get("noise") is None), key=lambda v: v["seconds"])

    models = provenance.load(args.models_dir)
    recognizer = MedAsr(args.models_dir / provenance.MEDASR_DIR, args.device)
    llama = LlamaServer(args.llama_url, api_key)
    dictating = Structurer(llama, prompt.load(), models.language_file, timeout=Settings.structuring_seconds)
    drafting = dictating.using(conversation_prompt())
    scripts = load_all()

    _, transcript, _ = await answers.answer(dictation, args.dictation_audio, recognizer, dictating, scripts)
    said = transcript.for_structuring()
    cases = {}
    for name, window_text, count in (("one_alone", None, 1), ("two_alone", None, 2), ("one_beside_a_window", window.text, 1),
                                     ("two_beside_a_window", window.text, 2)):
        cases[name] = await listening.contend(window_text, said, count, drafting, dictating, recognizer, models)
    await llama.aclose()
    stored["contention"] = {"dictation": dictation["id"], "window": longest["variant"],
                            "window_seconds_alone": longest["windows"][0]["seconds"]} | cases
    out.write_text(json.dumps(stored, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Wrote the contention probe into {out}: {stored['contention']}")
    return 0


def rescore(results_file: dict) -> dict:
    """A run's stored answers checked and scored again with the current code (see the header): a finished run's, or one
    whose scoring failed."""
    problem = _stale(results_file)
    if problem:
        raise ValueError(f"These results can't be scored again: {problem}.")
    out = scored(results_file)
    out["provenance"] = results_file["provenance"] | {"scoring": scoring_sha256()}
    out["dataset"] = results_file["dataset"] | {"gold_sha256": gold_sha256()}
    out["rescored_from"] = {"scoring": results_file["provenance"].get("scoring"),
                            "gold_sha256": results_file["dataset"].get("gold_sha256"), "structuring_outputs_reused": True}
    return out


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--llama-url", help="llama-server's address, such as http://127.0.0.1:8080")
    parser.add_argument("--models-dir", type=Path, default=REPO_ROOT / "infra" / "compose" / "models")
    parser.add_argument("--audio", type=Path, default=EVAL_DIR / "audio" / "conversations")
    parser.add_argument("--dictation-audio", type=Path, default=EVAL_DIR / "audio")
    parser.add_argument("--device", default="cpu", help="where MedASR and the speaker model run: cpu, mps or cuda")
    parser.add_argument("--machine", help="a name for this machine in the results (default: its CPU, memory and OS)")
    parser.add_argument("--out", type=Path, help="default: eval/results/ambient-latest.json")
    parser.add_argument("--only", nargs="*", help="visit ids or numbers (c03), for a quick look; the gate wants all")
    parser.add_argument("--no-live", action="store_true", help="skip the five-second-chunk timing")
    parser.add_argument("--contention", action="store_true", help="add the contention probe to the results")
    parser.add_argument("--rescore", type=Path, help="score this results file again, without the models")
    parser.add_argument("--resume", action="store_true", help="skip the recordings the results file already holds")
    args = parser.parse_args(argv)
    if args.rescore:
        rescored = rescore(json.loads(args.rescore.read_text(encoding="utf-8")))
        (args.out or args.rescore).write_text(json.dumps(rescored, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"Wrote {args.out or args.rescore}")
        return 0
    if not args.llama_url:
        parser.error("--llama-url is needed to run the models")
    return asyncio.run(contention(args) if args.contention else evaluate(args))


if __name__ == "__main__":
    sys.exit(main())
