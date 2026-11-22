"""Run A of the expansion plan's step 17: K2 Horizon alone on the Spanish test set's text, before any speech model is
downloaded, to see whether it translates clinical Spanish well enough for the step to go on.

    uv run --project eval python -m ehr_eval.run_a --llama-url http://127.0.0.1:8080 --models-dir <models folder>
        [--only e01 e02] [--machine "..."] [--out eval/results/run-a-es-text.json]

with llama-server's key in LLAMA_API_KEY and the K2 Horizon file models-manifest.json names (the models folder is only
read). Each script's display text is the transcript, as speech recognition that made no mistake would write it, with
the passages said in English marked unclear, as the Spanish pipeline will mark a passage that isn't in Spanish. K2
Horizon sorts it with the Spanish prompt and schema (prompt.load(language="es")), the sidecar's own code, as the
sidecar ships it (reasoning off), and the Spanish checks (translation.py) decide what would be offered. Nothing reaches
the network but llama-server, and nothing is written but the results file, which the CI gate doesn't check.

It prints each stop threshold of the plan (scoring_es.thresholds()) and whether it was crossed: any crossed stops the
step until the owner decides.
"""

from __future__ import annotations

import argparse
import asyncio
import dataclasses
import hashlib
import json
import os
import sys
import time
from datetime import UTC, datetime
from pathlib import Path

os.environ.setdefault("HF_HUB_OFFLINE", "1")

from dictation_scripts_es import SCRIPTS_DIR, Script, load_scripts, runs  # noqa: E402
from ehr_dictation import prompt, provenance, spanish, translation  # noqa: E402
from ehr_dictation.llama import LlamaServer, StructuringFailed, Structurer  # noqa: E402
from ehr_dictation.provenance import CHECKS_CODE_SHA256, PIPELINE_CODE_SHA256  # noqa: E402
from ehr_dictation.settings import Settings  # noqa: E402
from ehr_dictation.transcript import UNCLEAR_MARK, Placed, Segment, Transcript  # noqa: E402

from . import scoring_es  # noqa: E402
from .run import machine  # noqa: E402
from .scripts import EVAL_DIR  # noqa: E402

REPO_ROOT = EVAL_DIR.parent
OUT = EVAL_DIR / "results" / "run-a-es-text.json"
OTHER_LANGUAGE = "other_language"


def text_transcript(script: Script) -> tuple[Transcript, str]:
    """The script's display text as a final transcript, one passage per run of a language, the English ones marked
    unclear; and what K2 Horizon is sent, with [unclear] in their place."""
    text = script.display
    placed: list[Placed] = []
    sent = ""
    at = 0
    for language, words in runs(script.text, display=True):
        start = text.index(words, at)
        end = start + len(words)
        placed.append(Placed(Segment(0, 0, words, 1.0, OTHER_LANGUAGE if language == "en" else None, True), start, end))
        sent += text[at:start] + (UNCLEAR_MARK if language == "en" else words)
        at = end
    sent += text[at:]
    return Transcript(text, tuple(placed), True), sent


def spanish_first(content: str | None) -> tuple[int, int]:
    """How many statements the answer wrote, and how many of them with their keys in the schema's order: "spanish",
    then "field", then "english"."""
    if not content:
        return 0, 0
    orders: list[list[str]] = []

    def pairs(items: list[tuple[str, object]]) -> dict:
        keys = [key for key, _ in items]
        if set(keys) == {"spanish", "field", "english"}:
            orders.append(keys)
        return dict(items)

    try:
        json.loads(content, object_pairs_hook=pairs)
    except json.JSONDecodeError:
        return 0, 0
    return len(orders), sum(1 for keys in orders if keys == ["spanish", "field", "english"])


async def run_script(script: Script, structurer: Structurer) -> scoring_es.SpanishItem:
    transcript, sent = text_transcript(script)
    item = scoring_es.SpanishItem(
        variant=f"{script.id}--text", script=script.id, tags=list(script.tags), hypothesis=transcript.text,
        sorting="done", not_carried_over=[(kind, s, e) for s, e, kind in spanish.directives(transcript.text)],
    )
    started = time.perf_counter()
    try:
        proposal = await structurer.propose(sent)
    except StructuringFailed as error:
        item.timings["structure"] = time.perf_counter() - started
        item.sorting, item.reason, item.detail = "unavailable", error.reason, str(error)
        return item
    item.timings["structure"] = time.perf_counter() - started
    item.tokens = {key: int(proposal.timings[key]) for key in ("prompt_n", "cache_n", "predicted_n")
                   if proposal.timings and isinstance(proposal.timings.get(key), int)}
    item.statements_written, item.statements_in_order = spanish_first(proposal.content)
    checks = translation.check(proposal.fields, proposal.unknown, transcript)
    item.unknown = list(checks.unknown)
    for name, checked in checks.fields.items():
        statements = proposal.fields.get(name, [])
        item.fields[name] = scoring_es.SpanishOutcome(
            proposed=" ".join(s["english"] for s in statements),
            sources=[quote for s in statements for quote in s["spanish"]],
            status="pending" if checked.pending else "empty",
            reasons=checked.reasons,
            value=checked.value if checked.pending else "",
            warnings=[list(w) for w in checked.warnings],
            statements=statements,
        )
    return item


def scripts_sha256(directory: Path = SCRIPTS_DIR) -> str:
    digest = hashlib.sha256()
    for path in sorted(directory.glob("*.toml")):
        digest.update(path.name.encode() + b"\0" + path.read_bytes().replace(b"\r\n", b"\n") + b"\0")
    return digest.hexdigest()


def summary(metrics: dict) -> str:
    lines = ["", "Run A against the stop thresholds (any crossed stops the step for the owner):"]
    crossed = False
    for name, over, measured in scoring_es.thresholds(metrics):
        crossed |= over
        lines.append(f"  {'CROSSED' if over else 'ok     '}  {name}: {measured}")
    fields = metrics["fields"]
    lines += [
        "",
        f"English content F1 {fields['content']['f1']} (subjective {fields['by_field']['subjective']['content']['f1']}, "
        f"objective {fields['by_field']['objective']['content']['f1']}, assessment "
        f"{fields['by_field']['assessment']['content']['f1']}, plan {fields['by_field']['plan']['content']['f1']}); "
        f"detection F1 {fields['detection']['f1']}.",
        f"Checks: {metrics['validation']['passed']} of {metrics['validation']['proposed']} proposed fields passed; "
        f"reasons {metrics['validation']['reasons']}.",
        f"Mistranslations shown: {metrics['mistranslations']['shown']} (without a warning: "
        f"{metrics['mistranslations']['unflagged']}).",
        f"Instructions: {metrics['instructions']['said']} said, {metrics['instructions']['reported_not_carried_over']} "
        f"reported as not carried over, {metrics['instructions']['shown']} shown.",
        f"Structuring: {metrics['timing'].get('structure')} s; tokens written "
        f"{metrics['timing'].get('tokens_predicted')}; statements with the Spanish first "
        f"{metrics['timing']['statements_spanish_first']}.",
        "",
        "STOP: report to the owner before anything else." if crossed else "No threshold crossed.",
    ]
    return "\n".join(lines)


async def main_async(args: argparse.Namespace) -> int:
    api_key = os.environ.get("LLAMA_API_KEY", "")
    if not api_key:
        print("Set LLAMA_API_KEY to llama-server's key.", file=sys.stderr)
        return 2
    models = provenance.load(args.models_dir, verify=False)
    structuring = prompt.load(language="es")
    llama = LlamaServer(args.llama_url, api_key)
    structurer = Structurer(llama, structuring, models.language_file, timeout=Settings.structuring_seconds)
    scripts = {s.id: s for s in load_scripts()}
    chosen = [s for s in scripts.values() if not args.only or any(s.id == w or s.id.startswith(f"{w}-") for w in args.only)]
    started_at = datetime.now(UTC).isoformat(timespec="seconds")
    items = []
    for number, script in enumerate(chosen, 1):
        item = await run_script(script, structurer)
        items.append(item)
        print(f"[{number}/{len(chosen)}] {script.id}: {item.sorting}{f' ({item.reason})' if item.reason else ''}, "
              f"{item.timings.get('structure', 0):.1f} s", flush=True)
    await llama.aclose()
    metrics = scoring_es.score(items, scripts)
    results = {
        "run": "A: K2 Horizon on the Spanish test set's text, no speech recognition (the expansion plan's step 17)",
        "started_at": started_at,
        "finished_at": datetime.now(UTC).isoformat(timespec="seconds"),
        "machine": machine(args.machine, "none"),
        "llama_server": structurer.build,
        "parameters": structuring.parameters,
        "provenance": {"language": models.language, "prompt": structuring.prompt_sha256,
                       "schema": structuring.schema_sha256, "structuring": structuring.structuring_sha256,
                       "checks": CHECKS_CODE_SHA256, "pipeline": PIPELINE_CODE_SHA256},
        "dataset": {"scripts_sha256": scripts_sha256(), "scripts": len(chosen), "complete": not args.only},
        "metrics": metrics,
        "thresholds": [{"threshold": name, "crossed": over, "measured": measured}
                       for name, over, measured in scoring_es.thresholds(metrics)],
        "items": [dataclasses.asdict(item) for item in items],
    }
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(results, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(summary(metrics))
    print(f"Wrote {args.out}")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--llama-url", required=True, help="llama-server's address, such as http://127.0.0.1:8080")
    parser.add_argument("--models-dir", type=Path, default=REPO_ROOT / "infra" / "compose" / "models",
                        help="where models-manifest.json is; only read")
    parser.add_argument("--machine", help="a name for this machine in the results")
    parser.add_argument("--only", nargs="*", help="script ids to run, for a quick look")
    parser.add_argument("--out", type=Path, default=OUT)
    return asyncio.run(main_async(parser.parse_args(argv)))


if __name__ == "__main__":
    sys.exit(main())
