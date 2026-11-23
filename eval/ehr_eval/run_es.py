"""Runs the Spanish test set (the expansion plan's step 17) through the dictation pipeline and writes the results file
CI checks beside the English one (gate.py). Started by run.py:

    uv run --project eval python -m ehr_eval.run --set es --llama-url http://127.0.0.1:8080 [--device mps] [--machine "..."]

with llama-server's key in LLAMA_API_KEY, the models where setup_models.py put them (Whisper included: --only whisper),
and the recordings generate_audio.py --set es made (--audio, default eval/audio-es). Each recording dictated in Spanish
goes through the sidecar's own code as a Spanish dictation does (answers_es.py), and then the Spanish checks
(translation.py); the one dictated as English goes the English way and through the English checks (validation.py);
the English script's recording dictated as Spanish goes the Spanish way. Each script's display text is also sorted on
its own, as run A did (run_a.py). Nothing reaches the network but llama-server.

The results go to eval/results/latest-es.json (--out), stamped with the models' hashes (Whisper's as the speech
model), the Spanish prompt, schema and parameters hash, the pipeline and scoring hashes it shares with the English
set, the Spanish test set's own recordings and gold hashes, and the machine. At the end it prints every passage of the
scripts that mix English into Spanish, and of the English script dictated as Spanish, as Whisper wrote it, with the
probability that it was Spanish (scoring_es.py's code_switching), for someone to read for Spanish nobody said.

    uv run --project eval python -m ehr_eval.run --set es --rescore eval/results/latest-es.json --out eval/results/latest-es.json

checks and scores the stored answers again with the current checks, scoring and gold, running no model: what the gate
asks for when only those changed.
"""

from __future__ import annotations

import argparse
import dataclasses
import json
import sys
from datetime import UTC, datetime
from pathlib import Path

from dictation_scripts import load_scripts as load_english
from dictation_scripts_es import load_scripts
from ehr_dictation import validation
from ehr_dictation.transcript import Segment, assemble

from . import scoring_es
from .answers_es import LIVE_SECONDS, answer, live_chunks, open_pipeline
from .run import machine
from .run_a import check, run_script, spanish_first, text_transcript
from .scripts import EVAL_DIR, gold_es_sha256, pipeline_sha256, recordings_es_sha256, scoring_sha256

OUT = EVAL_DIR / "results" / "latest-es.json"
AUDIO = EVAL_DIR / "audio-es"
SCHEMA_VERSION = 1


def check_english(item: scoring_es.SpanishItem, fields: dict[str, dict], unknown: list[str], transcript) -> None:  # noqa: ANN001
    """A recording dictated as English: its proposal through the English checks, onto the item as the sidecar would
    send it."""
    checks = validation.check(fields, unknown, transcript)
    item.unknown = list(checks.unknown)
    for name, checked in checks.fields.items():
        item.fields[name] = scoring_es.SpanishOutcome(
            proposed=fields[name]["value"], sources=fields[name]["sources"],
            status="pending" if checked.pending else "empty", reasons=checked.reasons,
            value=checked.value if checked.pending else "", warnings=[list(span) for span in checked.warnings],
        )


async def main_async(args: argparse.Namespace, api_key: str) -> int:
    manifest_path = args.audio / "manifest.json"
    if not manifest_path.is_file():
        print(f"No {manifest_path}: generate the audio first (eval/README.md: generate_audio.py --set es).", file=sys.stderr)
        return 2
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    scripts = {s.id: s for s in load_scripts()}
    english = {s.id: s for s in load_english()}
    wanted = lambda script: not args.only or any(script == w or script.startswith(f"{w}-") for w in args.only)  # noqa: E731
    variants = [v for v in manifest["variants"] if wanted(v["script"])]
    pipeline = open_pipeline(args, api_key)
    started_at = datetime.now(UTC).isoformat(timespec="seconds")

    items: list[scoring_es.SpanishItem] = []
    for number, variant in enumerate(variants, 1):
        script = scripts.get(variant["script"]) or english[variant["script"]]
        item, transcript, proposal = await answer(variant, args.audio, pipeline, list(script.tags))
        if proposal is not None and variant.get("run_as") == "en":
            check_english(item, proposal.fields, proposal.unknown, transcript)
        elif proposal is not None:
            item.statements_written, item.statements_in_order = spanish_first(proposal.content)
            check(item, proposal.fields, proposal.unknown, transcript)
        if item.seconds >= LIVE_SECONDS and not variant.get("noise") and not variant.get("run_as") and not args.no_live:
            data = (args.audio / variant["file"]).read_bytes()
            item.chunk_seconds = await live_chunks(data, item.seconds, pipeline)
        items.append(item)
        print(f"[{number}/{len(variants)}] {variant['id']}: {item.sorting}{f' ({item.reason})' if item.reason else ''}"
              f" ({', '.join(f'{k} {v:.1f}s' for k, v in item.timings.items())})", flush=True)
    chosen = [s for s in scripts.values() if wanted(s.id)]
    for number, script in enumerate(chosen, 1):
        item = await run_script(script, pipeline.spanish)
        items.append(item)
        print(f"[text {number}/{len(chosen)}] {script.id}: {item.sorting}, {item.timings.get('structure', 0):.1f} s",
              flush=True)
    await pipeline.llama.aclose()

    structuring = pipeline.spanish.structuring
    results = {
        "schema_version": SCHEMA_VERSION,
        "baseline": False,
        "run": {
            "started_at": started_at,
            "finished_at": datetime.now(UTC).isoformat(timespec="seconds"),
            "machine": machine(args.machine, args.device),
            "llama_server": pipeline.spanish.build,
            "parameters": structuring.parameters,
        },
        "provenance": {
            "speech": pipeline.models.spanish_speech,
            "english_speech": pipeline.models.speech,
            "language": pipeline.models.language,
            "prompt": structuring.prompt_sha256,
            "schema": structuring.schema_sha256,
            "structuring": structuring.structuring_sha256,
            "pipeline": pipeline_sha256(),
            "scoring": scoring_sha256(),
        },
        "dataset": {
            "recordings_sha256": recordings_es_sha256(),
            "gold_sha256": gold_es_sha256(),
            "scripts": len(chosen),
            "recordings": len(variants),
            "complete": not args.only,
            "audio": manifest.get("generator", {}),
        },
        "metrics": scoring_es.score_audio(items, scripts, english),
        "items": [dataclasses.asdict(item) for item in items],
    }
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(results, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(code_switching(results["metrics"]))
    print(f"Wrote {args.out}")
    return 0


def code_switching(metrics: dict) -> str:
    """Every passage of the code-switching recordings as Whisper wrote it, with the probability that it was Spanish and
    what it was marked."""
    lines = ["", "Passages of the scripts that mix English into Spanish, and of the English script dictated as Spanish, "
                 "as Whisper wrote them (P(Spanish), mark, text): read them for Spanish nobody said."]
    for entry in metrics["code_switching"]["items"]:
        lines.append(f"  {entry['variant']}")
        for passage in entry["passages"]:
            spanish = "  -  " if passage["spanish"] is None else f"{passage['spanish']:.3f}"
            lines.append(f"    {spanish}  {passage['unclear'] or '':14}  {passage['text']}")
    return "\n".join(lines)


def stored_item(entry: dict) -> scoring_es.SpanishItem:
    fields = {name: scoring_es.SpanishOutcome(**outcome) for name, outcome in entry["fields"].items()}
    return scoring_es.SpanishItem(**{**entry, "fields": fields,
                                     "not_carried_over": [tuple(found) for found in entry["not_carried_over"]]})


def rescore(stored: dict) -> dict:
    """The stored answers through the current checks and scoring, against the current gold and tags: each recording's
    transcript rebuilt from its passages as heard, each display text from its script, nothing run but the checks."""
    scripts = {s.id: s for s in load_scripts()}
    english = {s.id: s for s in load_english()}
    items = []
    for entry in stored["items"]:
        old = stored_item(entry)
        script = scripts.get(old.script) or english[old.script]
        if old.variant.endswith("--text"):
            transcript, _ = text_transcript(script)
        else:
            transcript = assemble([Segment(**segment) for segment in old.segments], final=True)
        if transcript.text != old.hypothesis:
            raise SystemExit(f"{old.variant}'s transcript can't be rebuilt as it was: run the Spanish eval again.")
        item = dataclasses.replace(old, fields={}, unknown=[], tags=[t for t in old.tags if ":" in t] + list(script.tags))
        if old.sorting == "done" and old.run_as == "en":
            check_english(item, {name: {"value": f.proposed, "sources": f.sources} for name, f in old.fields.items()},
                          old.unknown, transcript)
        elif old.sorting == "done":
            check(item, {name: f.statements for name, f in old.fields.items()}, old.unknown, transcript)
        items.append(item)
    out = {**stored, "provenance": stored["provenance"] | {"scoring": scoring_sha256()},
           "dataset": stored["dataset"] | {"gold_sha256": gold_es_sha256()},
           "metrics": scoring_es.score_audio(items, scripts, english),
           "items": [dataclasses.asdict(item) for item in items]}
    out["rescored"] = {"at": datetime.now(UTC).isoformat(timespec="seconds"),
                       "scoring": stored["provenance"].get("scoring")}
    return out


def main_rescore(path: Path, out: Path) -> int:
    results = rescore(json.loads(path.read_text(encoding="utf-8")))
    out.write_text(json.dumps(results, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Wrote {out}")
    return 0
