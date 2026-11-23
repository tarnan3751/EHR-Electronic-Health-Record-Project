"""Run A of the expansion plan's step 17: K2 Horizon alone on the Spanish test set's text, before any speech model is
downloaded, to see whether it translates clinical Spanish well enough for the step to go on.

    uv run --project eval python -m ehr_eval.run_a --llama-url http://127.0.0.1:8080 --models-dir <models folder>
        [--only e01 e02] [--machine "..."] [--out eval/results/run-a-es-text.json]
        [--timeout <seconds>] [--keep-answers] [--holdout]

with llama-server's key in LLAMA_API_KEY and the K2 Horizon file models-manifest.json names (the models folder is only
read). Each script's display text is the transcript, as speech recognition that made no mistake would write it, with
the passages said in English marked unclear, as the Spanish pipeline will mark a passage that isn't in Spanish. K2
Horizon sorts it with the Spanish prompt and schema (prompt.load(language="es")), the sidecar's own code, as the
sidecar ships it (reasoning off), and the Spanish checks (translation.py) decide what would be offered. Nothing reaches
the network but llama-server, and nothing is written but the results file, which the CI gate doesn't check. The five
held-out scripts (eval/scripts-es-holdout) are left out unless --holdout is given, which runs only them.

It prints each stop threshold of the plan (scoring_es.thresholds()) and whether it was crossed: any crossed stops the
step until the owner decides. The results file is written as the run goes, as run.py and run_es.py write theirs: each
script's answer as K2 Horizon gave it, before any check, then checked, and the measures and thresholds only once every
script is in, so a fault in the checks or the scoring leaves every answer so far for --rescore to finish.

Two options are for diagnosing a run, never for the run itself: --timeout gives K2 Horizon longer (or less) than the
sidecar's 90 seconds, so an answer the sidecar would cancel can be seen to its end, and --keep-answers stores each
item's answer as the model wrote it in the results file. Either marks the file "diagnostic", and the printed summary
says so in its first line, so it can't be taken for the run.

    uv run --project eval python -m ehr_eval.run_a --rescore <results file> --out <new results file>

checks and scores a run's stored answers again with the current checks, scoring and scripts, running no model, and
prints, for each script, what the first threshold counted before (as stored) and after, what is misfiled, the fields
discarded and why, and the content F1, before and after ("not scored" before, for a run that stopped before its
measures). Answers stored by field, before the answer was one list, are checked field by field. It refuses a script
whose text has changed since the run.
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
from collections.abc import Callable
from datetime import UTC, datetime
from pathlib import Path

os.environ.setdefault("HF_HUB_OFFLINE", "1")

from dictation_scripts_es import HOLDOUT_DIR, SCRIPTS_DIR, Script, load_scripts, runs  # noqa: E402
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


class KeptAnswers(LlamaServer):
    """llama-server, keeping its last answer as the model wrote it, even one the sidecar refuses (cut off at the token
    limit, or not the schema's shape), for --keep-answers. Nothing is kept of an answer cancelled at the timeout."""

    answer: str | None = None

    async def chat(self, body: dict) -> dict:
        answer = await super().chat(body)
        try:
            self.answer = answer["choices"][0]["message"]["content"]
        except (KeyError, IndexError, TypeError):
            self.answer = None
        return answer


async def run_script(script: Script, structurer: Structurer, kept: KeptAnswers | None = None,
                     raw: Callable[[scoring_es.SpanishItem], None] | None = None) -> scoring_es.SpanishItem:
    """The script's display text sorted and checked; `raw` is given the item first, with the statements as K2 Horizon
    proposed them and no check run yet."""
    transcript, sent = text_transcript(script)
    item = scoring_es.SpanishItem(
        variant=f"{script.id}--text", script=script.id, tags=list(script.tags), hypothesis=transcript.text,
        sorting="done", not_carried_over=[(kind, s, e) for s, e, kind in spanish.directives(transcript.text)],
    )
    if kept:
        kept.answer = None
    started = time.perf_counter()
    try:
        proposal = await structurer.propose(sent)
    except StructuringFailed as error:
        item.timings["structure"] = time.perf_counter() - started
        item.sorting, item.reason, item.detail = "unavailable", error.reason, str(error)
        item.answer = kept.answer if kept else None
        return item
    item.timings["structure"] = time.perf_counter() - started
    item.tokens = {key: int(proposal.timings[key]) for key in ("prompt_n", "cache_n", "predicted_n")
                   if proposal.timings and isinstance(proposal.timings.get(key), int)}
    item.statements_written, item.statements_in_order = spanish_first(proposal.content)
    item.answer = kept.answer if kept else None
    unchecked(item, proposal.fields, proposal.unknown)
    if raw is not None:
        raw(item)
    check(item, proposal.fields, proposal.unknown, transcript)
    return item


def unchecked(item: scoring_es.SpanishItem, fields: dict[str, list[dict]], unknown: list[str]) -> None:
    """The statements K2 Horizon proposed onto the item as it gave them, no check run: what a stored run is checked
    again from (rescore())."""
    item.unknown = list(unknown)
    item.fields = {name: scoring_es.SpanishOutcome(proposed=" ".join(s["english"] for s in statements),
                                                   sources=[quote for s in statements for quote in s["spanish"]],
                                                   statements=statements)
                   for name, statements in fields.items()}


def check(item: scoring_es.SpanishItem, fields: dict[str, list[dict]], unknown: list[str],
          transcript: Transcript) -> None:
    """The proposal's fields through the Spanish checks, onto the item as the sidecar would send them."""
    checks = translation.check(fields, unknown, transcript)
    item.unknown = list(checks.unknown)
    for name, checked in checks.fields.items():
        statements = fields.get(name, [])
        item.fields[name] = scoring_es.SpanishOutcome(
            proposed=" ".join(s["english"] for s in statements),
            sources=[quote for s in statements for quote in s["spanish"]],
            status="pending" if checked.pending else "empty",
            reasons=checked.reasons,
            value=checked.value if checked.pending else "",
            warnings=[list(w) for w in checked.warnings],
            statements=statements,
            shown_statements=[{"english": checked.value[start:end], "sources": [list(span) for span in spans]}
                              for start, end, spans in checked.statements] if checked.pending else [],
        )


def scripts_sha256(directory: Path = SCRIPTS_DIR) -> str:
    digest = hashlib.sha256()
    for path in sorted(directory.glob("*.toml")):
        digest.update(path.name.encode() + b"\0" + path.read_bytes().replace(b"\r\n", b"\n") + b"\0")
    return digest.hexdigest()


def diagnostic(timeout: float, keep_answers: bool) -> list[str]:
    """Why a run is a diagnostic one, not run A: empty for the run itself."""
    reasons = [f"timeout {timeout:g} s, not the sidecar's {Settings.structuring_seconds:g} s"] if (
        timeout != Settings.structuring_seconds) else []
    return reasons + (["the model's answers kept"] if keep_answers else [])


def summary(metrics: dict, diagnosed: list[str] | None = None) -> str:
    lines = [f"DIAGNOSTIC, not run A ({'; '.join(diagnosed)}): its thresholds decide nothing."] if diagnosed else []
    lines += ["", "Run A against the stop thresholds (any crossed stops the step for the owner):"]
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
        f"Changed from the Spanish cited and shown: {metrics['mistranslations']['shown']} (without a warning: "
        f"{metrics['mistranslations']['unflagged']}; moved from another passage: "
        f"{metrics['mistranslations']['moved_from_another_passage']}).",
        f"Misfiled (said in the Spanish cited, not in that field's gold; never a stop): "
        f"{metrics['mistranslations']['misfiled']['shown']}.",
        f"Instructions: {metrics['instructions']['said']} said, {metrics['instructions']['reported_not_carried_over']} "
        f"reported as not carried over, {metrics['instructions']['shown']} shown.",
        f"Structuring: {metrics['timing'].get('structure')} s; tokens written "
        f"{metrics['timing'].get('tokens_predicted')}; statements with their keys in the schema's order "
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
    llama = (KeptAnswers if args.keep_answers else LlamaServer)(args.llama_url, api_key)
    structurer = Structurer(llama, structuring, models.language_file, timeout=args.timeout)
    diagnosed = diagnostic(args.timeout, args.keep_answers)
    scripts_dir = HOLDOUT_DIR if args.holdout else SCRIPTS_DIR
    scripts = {s.id: s for s in load_scripts(scripts_dir)}
    chosen = [s for s in scripts.values() if not args.only or any(s.id == w or s.id.startswith(f"{w}-") for w in args.only)]
    header = {
        "run": "A: K2 Horizon on the Spanish test set's text, no speech recognition (the expansion plan's step 17)",
        "diagnostic": diagnosed,
        "started_at": datetime.now(UTC).isoformat(timespec="seconds"),
        "finished_at": None,
        "machine": machine(args.machine, "none"),
        "llama_server": None,
        "parameters": structuring.parameters,
        "timeout_seconds": args.timeout,
        "provenance": {"language": models.language, "prompt": structuring.prompt_sha256,
                       "schema": structuring.schema_sha256, "structuring": structuring.structuring_sha256,
                       "checks": CHECKS_CODE_SHA256, "pipeline": PIPELINE_CODE_SHA256},
        "dataset": {"scripts_sha256": scripts_sha256(scripts_dir), "scripts": len(chosen), "complete": not args.only,
                    "holdout": args.holdout},
    }

    def save(items: list[scoring_es.SpanishItem], metrics: dict | None = None) -> None:
        """The results file as it stands: every script answered so far, and the measures once there are some."""
        header.update(finished_at=datetime.now(UTC).isoformat(timespec="seconds"), llama_server=structurer.build)
        scored = {} if metrics is None else {"metrics": metrics, "thresholds": [
            {"threshold": name, "crossed": over, "measured": measured}
            for name, over, measured in scoring_es.thresholds(metrics)]}
        results = header | scored | {"items": [dataclasses.asdict(item) for item in items]}
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(results, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")

    items: list[scoring_es.SpanishItem] = []
    for number, script in enumerate(chosen, 1):
        item = await run_script(script, structurer, llama if args.keep_answers else None,
                                raw=lambda answered: save([*items, answered]))
        items.append(item)
        save(items)
        print(f"[{number}/{len(chosen)}] {script.id}: {item.sorting}{f' ({item.reason})' if item.reason else ''}, "
              f"{item.timings.get('structure', 0):.1f} s", flush=True)
    await llama.aclose()
    metrics = scoring_es.score(items, scripts)
    save(items, metrics)
    print(summary(metrics, diagnosed))
    print(f"Wrote {args.out}")
    return 0


# ---- A run's stored answers, checked and scored again ---------------------------------------------------------------


def stored_scripts_dir(stored: dict) -> Path:
    """Where the scripts of a stored run are: the held-out ones, or the test set."""
    return HOLDOUT_DIR if stored.get("dataset", {}).get("holdout") else SCRIPTS_DIR


def stored_item(entry: dict) -> scoring_es.SpanishItem:
    """An item as a results file stored it."""
    fields = {name: scoring_es.SpanishOutcome(**outcome) for name, outcome in entry["fields"].items()}
    return scoring_es.SpanishItem(**{**entry, "fields": fields,
                                     "not_carried_over": [tuple(found) for found in entry["not_carried_over"]]})


def rescore(stored: dict, scripts: dict[str, Script]) -> tuple[dict, list[scoring_es.SpanishItem]]:
    """The stored answers through the current checks, with the scripts' current tags, and the results file again: its
    metrics and thresholds recomputed, its provenance the answers' own but for the checks, and what it was scored with
    before under "rescored"."""
    items = []
    for old in (stored_item(entry) for entry in stored["items"]):
        script = scripts[old.script]
        transcript, _ = text_transcript(script)
        if transcript.text != old.hypothesis:
            raise ValueError(f"{script.id}'s text has changed since the run: run it again instead.")
        item = dataclasses.replace(
            old, tags=list(script.tags), fields={}, unknown=[],
            not_carried_over=[(kind, s, e) for s, e, kind in spanish.directives(transcript.text)])
        if old.sorting == "done":
            check(item, {name: outcome.statements for name, outcome in old.fields.items()}, old.unknown, transcript)
        items.append(item)
    metrics = scoring_es.score(items, scripts)
    results = {
        **stored,
        "provenance": {**stored["provenance"], "checks": CHECKS_CODE_SHA256},
        "dataset": {**stored["dataset"], "scripts_sha256": scripts_sha256(stored_scripts_dir(stored))},
        "metrics": metrics,
        "thresholds": [{"threshold": name, "crossed": over, "measured": measured}
                       for name, over, measured in scoring_es.thresholds(metrics)],
        "items": [dataclasses.asdict(item) for item in items],
        "rescored": {"at": datetime.now(UTC).isoformat(timespec="seconds"),
                     "note": "the stored answers checked and scored again; no model was run",
                     "before": {"checks": stored["provenance"].get("checks"),
                                "scripts_sha256": stored["dataset"].get("scripts_sha256"),
                                "thresholds": stored.get("thresholds")}},
    }
    return results, items


def rescore_table(stored: dict, results: dict, scripts: dict[str, Script]) -> str:
    """For each script: the first threshold's count before and after, what is misfiled, the content F1 before and
    after, and the fields discarded and why, before and after."""
    def unflagged(metrics: dict) -> dict[str, int]:
        counts: dict[str, int] = {}
        for entry in metrics["mistranslations"]["listed"]:
            counts[entry["variant"]] = counts.get(entry["variant"], 0) + (not entry["flagged"])
        return counts

    def f1(item: scoring_es.SpanishItem) -> float | None:
        return scoring_es._fields([item], scripts, lambda script: script.gold)["content"]["f1"]

    def discarded(item: scoring_es.SpanishItem) -> str:
        if item.sorting != "done":
            return f"not sorted ({item.reason})"
        return "; ".join(f"{name}: {', '.join(outcome.reasons)}" for name, outcome in item.fields.items()
                         if outcome.proposed.strip() and outcome.status != "pending") or "none"

    scored = "metrics" in stored  # a run that stopped before its measures has none
    before, after = unflagged(stored["metrics"]) if scored else {}, unflagged(results["metrics"])
    misfiled: dict[str, int] = {}
    for entry in results["metrics"]["mistranslations"]["misfiled"]["listed"]:
        misfiled[entry["variant"]] = misfiled.get(entry["variant"], 0) + 1
    lines = ["item                          first threshold  misfiled  content F1",
             "                              before  after    after     before  after"]
    for old, new in zip((stored_item(e) for e in stored["items"]), (stored_item(e) for e in results["items"])):
        was = before.get(old.variant, 0) if scored else "-"
        lines.append(f"{old.script:30}{was:>6}  {after.get(new.variant, 0):>5}    "
                     f"{misfiled.get(new.variant, 0):>8}     {f1(old) if scored else '-'!s:>6}  {f1(new)!s:>6}")
        lines.append(f"    discarded before: {discarded(old) if scored else 'not scored'}")
        lines.append(f"    discarded after:  {discarded(new)}")
    lines.append("")
    for was, now in zip(stored.get("thresholds") or [None] * len(results["thresholds"]), results["thresholds"]):
        before_text = f"{'CROSSED' if was['crossed'] else 'ok'} ({was['measured']})" if was else "not scored"
        lines.append(f"{now['threshold']}:\n    before {before_text}; after "
                     f"{'CROSSED' if now['crossed'] else 'ok'} ({now['measured']})")
    return "\n".join(lines)


def main_rescore(args: argparse.Namespace) -> int:
    if args.out is None:
        print("Say where the new results go: --out <file>.", file=sys.stderr)
        return 2
    stored = json.loads(args.rescore.read_text(encoding="utf-8"))
    scripts = {s.id: s for s in load_scripts(stored_scripts_dir(stored))}
    results, _ = rescore(stored, scripts)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(results, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(rescore_table(stored, results, scripts))
    print(summary(results["metrics"], results.get("diagnostic")))
    print(f"Wrote {args.out}")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--llama-url", help="llama-server's address, such as http://127.0.0.1:8080")
    parser.add_argument("--models-dir", type=Path, default=REPO_ROOT / "infra" / "compose" / "models",
                        help="where models-manifest.json is; only read")
    parser.add_argument("--machine", help="a name for this machine in the results")
    parser.add_argument("--only", nargs="*", help="script ids to run, for a quick look")
    parser.add_argument("--timeout", type=float, default=Settings.structuring_seconds,
                        help="seconds K2 Horizon has to answer; any but the sidecar's makes the run a diagnostic one")
    parser.add_argument("--keep-answers", action="store_true",
                        help="store each answer as the model wrote it; makes the run a diagnostic one")
    parser.add_argument("--holdout", action="store_true",
                        help="run only the five held-out scripts (eval/scripts-es-holdout), never used to tune the checks")
    parser.add_argument("--rescore", type=Path, help="a results file whose stored answers to check and score again")
    parser.add_argument("--out", type=Path, help=f"where the results go (a run's default: {OUT.relative_to(REPO_ROOT)})")
    args = parser.parse_args(argv)
    if args.rescore:
        return main_rescore(args)
    if not args.llama_url:
        parser.error("--llama-url is required to run the model")
    args.out = args.out or OUT
    return asyncio.run(main_async(args))


if __name__ == "__main__":
    sys.exit(main())
