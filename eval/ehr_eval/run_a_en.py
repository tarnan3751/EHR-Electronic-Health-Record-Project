"""Run A's English counterpart (the expansion plan's step 17): K2 Horizon alone on the English test set's text, as run A
puts it to the Spanish set's (run_a.py), so the two sets can be set side by side against run A's stop thresholds.

    uv run --project eval python -m ehr_eval.run_a_en --llama-url http://127.0.0.1:8080 --models-dir <models folder>
        [--only d01 d02] [--machine "..."] [--out eval/results/run-a-en-text.json]

with llama-server's key in LLAMA_API_KEY. Each English script's display text (eval/scripts) is the transcript, one
passage, as speech recognition that made no mistake would write it (goldreport.transcript()). K2 Horizon sorts it with
the English prompt and schema as the sidecar ships them (reasoning off, the sidecar's 90 seconds), and the English
checks (validation.py) decide what would be offered; the instructions they find are weighed for proposals as on a
recording. It is scored by scoring.py, as the English recordings are, and set against the Spanish set's stop thresholds
line by line where a measure exists in both (thresholds()): the first, a value changed from the Spanish its statement
cites, has no English counterpart, since an English value is the clinician's own words, checked word for word, and the
nearest English measures stand beside it. Nothing reaches the network but llama-server, and nothing is written but the
results file, which the CI gate doesn't check. It is written as the run goes, each answer as K2 Horizon gave it before
any check, then checked, and the measures once every script is in.

    uv run --project eval python -m ehr_eval.run_a_en --rescore <results file> --out <new results file>

checks and scores a run's stored answers again with the current checks and scoring, running no model. It refuses a
script whose text has changed since the run, and a results file that isn't a whole run of the set (run_a.incomplete():
a script with no item, one held twice, or one neither sorted nor failed). A script that timed out is a failure in every
measure, never left out. A run made with --only says in its summary's first line that it is incomplete.

    uv run --project eval python -m ehr_eval.run_a_en --compare <English results> <Spanish results>

prints the two sets' stop thresholds side by side from each file's measures: the English from this run's results (or
eval/results/latest.json, the recordings, once scored again with this scorer: it then has discarded and misfiled), the
Spanish from a run A results file (or a Spanish one's "text" measures).
"""

from __future__ import annotations

import argparse
import asyncio
import dataclasses
import json
import os
import sys
import time
from collections.abc import Callable
from datetime import UTC, datetime
from pathlib import Path

os.environ.setdefault("HF_HUB_OFFLINE", "1")

from ehr_dictation import prompt, provenance, validation  # noqa: E402
from ehr_dictation.llama import LlamaServer, StructuringFailed, Structurer  # noqa: E402
from ehr_dictation.provenance import CHECKS_CODE_SHA256, PIPELINE_CODE_SHA256  # noqa: E402
from ehr_dictation.settings import Settings  # noqa: E402
from ehr_dictation.transcript import Transcript  # noqa: E402

from . import goldreport, scoring, scoring_es  # noqa: E402
from .run import machine  # noqa: E402
from .run_a import incomplete, scripts_sha256  # noqa: E402
from .scripts import EVAL_DIR, SCRIPTS_DIR, Script, load_all  # noqa: E402

OUT = EVAL_DIR / "results" / "run-a-en-text.json"


def item_for(script: Script) -> tuple[scoring.Item, Transcript]:
    """The script's display text as a final transcript, and its item before any answer: the instructions the English
    checks find in it, and those they would propose (scoring.weigh_proposals())."""
    transcript = goldreport.transcript(script.display)
    item = scoring.Item(
        variant=f"{script.id}--text", script=script.id, tags=list(script.tags), seconds=0.0,
        hypothesis=transcript.text, sorting="done",
        not_carried_over=[(kind, start, end) for start, end, kind in validation.directives(transcript.text)],
        segments=[dataclasses.asdict(placed.segment) for placed in transcript.placed])
    scoring.weigh_proposals(item, transcript)
    return item, transcript


def check(item: scoring.Item, fields: dict[str, dict], unknown: list[str], transcript: Transcript) -> None:
    """The proposal's fields through the English checks, onto the item as the sidecar would send them (run.py's)."""
    checks = validation.check(fields, unknown, transcript)
    item.unknown = list(checks.unknown)
    item.fields = {
        name: scoring.FieldOutcome(
            proposed=fields[name]["value"], sources=fields[name]["sources"],
            status="pending" if checked.pending else "empty", reasons=checked.reasons,
            value=checked.value if checked.pending else "", warnings=[list(span) for span in checked.warnings])
        for name, checked in checks.fields.items()
    }


async def run_script(script: Script, structurer: Structurer,
                     raw: Callable[[scoring.Item], None] | None = None) -> scoring.Item:
    """The script's display text sorted and checked; `raw` is given the item first, with K2 Horizon's answer as it
    gave it and no check run yet."""
    item, transcript = item_for(script)
    started = time.perf_counter()
    try:
        proposal = await structurer.propose(transcript.for_structuring())
    except StructuringFailed as error:
        item.timings["structure"] = time.perf_counter() - started
        item.sorting, item.reason = "unavailable", error.reason
        return item
    item.timings["structure"] = time.perf_counter() - started
    item.tokens = {key: int(proposal.timings[key]) for key in ("prompt_n", "cache_n", "predicted_n")
                   if proposal.timings and isinstance(proposal.timings.get(key), int)}
    item.unknown = list(proposal.unknown)
    item.fields = {name: scoring.FieldOutcome(proposed=field["value"], sources=field["sources"])
                   for name, field in proposal.fields.items()}
    if raw is not None:
        raw(item)
    check(item, proposal.fields, proposal.unknown, transcript)
    return item


# ---- The Spanish set's stop thresholds, measured on the English set ------------------------------------------------


def thresholds(metrics: dict) -> list[tuple[str, bool | None, str]]:
    """Run A's stop thresholds (scoring_es.thresholds()), line for line, on the English set's measures: each crossed or
    not, or None where it can't apply, with the nearest English measure beside it. The lines are the Spanish set's."""
    order_allergy, discarded, sorting = metrics["order_allergy"], metrics["discarded"], metrics["sorting"]
    content_f1 = metrics["fields"]["content"]["f1"]
    return [
        ("a value changed from what was said and shown without a warning", None,
         "not applicable: an English value is the clinician's own words, checked word for word; nearest: "
         f"{order_allergy['retracted_shown_unflagged']} retracted value(s) and "
         f"{order_allergy['drug_not_in_gold_shown_unflagged']} drug(s) no gold value carries shown without a warning, "
         f"{metrics['unsupported_content']['shown']} of offered words not in the transcript"),
        ("an instruction to order something or change the allergy list carried into a field",
         order_allergy["shown"] > 0, f"{order_allergy['shown']} shown"),
        (f"content F1 below {scoring_es.F1_FLOOR} (against the rule-following gold)",
         content_f1 is None or content_f1 < scoring_es.F1_FLOOR, f"F1 {content_f1}"),
        ("more than a third of the fields with gold discarded by the checks, or left empty by a script not sorted",
         discarded["share"] is not None and discarded["share"] > scoring_es.DISCARDED_CEILING,
         f"{discarded['discarded']} of {discarded['fields_with_gold']} ({discarded['share']})"),
        ("a script not sorted: timed out, cut off at the token limit, or the wrong shape",
         bool(sorting["unavailable"]), f"unavailable {sorting['unavailable'] or 'none'}"),
    ]


def _mark(crossed: bool | None) -> str:
    return "n/a" if crossed is None else "CROSSED" if crossed else "ok"


def _misfiled(misfiled: dict) -> str:
    return (f"{misfiled['total']} value(s), {misfiled['unflagged_total']} without a warning, in "
            f"{misfiled['misfiled_statements']} of {misfiled['statements']} statements ({misfiled['share']})")


def summary(metrics: dict, gap: str | None = None) -> str:
    lines = [f"INCOMPLETE, not a whole run of the English set ({gap}): its thresholds decide nothing."] if gap else []
    lines += ["", "The English set's text against run A's stop thresholds (n/a: no English counterpart):"]
    for name, crossed, measured in thresholds(metrics):
        lines.append(f"  {_mark(crossed):7}  {name}: {measured}")
    fields, validated = metrics["fields"], metrics["validation"]
    lines += [
        "",
        f"Content F1 {fields['content']['f1']} against the rule-following gold "
        f"({metrics['fields_clinical']['content']['f1']} against the clinical gold); detection F1 "
        f"{fields['detection']['f1']}.",
        f"Checks: {validated['passed']} of {validated['proposed']} proposed fields passed; reasons {validated['reasons']}.",
        f"Misfiled (a value its field's rule-following gold doesn't have; never a stop): {_misfiled(metrics['misfiled'])}.",
    ]
    return "\n".join(lines)


def compare(english: dict, spanish: dict) -> str:
    """The two sets' stop thresholds, line by line, from each set's measures, then what is reported beside them."""
    rows = [(name, f"{_mark(en_crossed)}: {en_measured}", f"{_mark(es_crossed)}: {es_measured}")
            for (_, en_crossed, en_measured), (name, es_crossed, es_measured)
            in zip(thresholds(english), scoring_es.thresholds(spanish), strict=True)]
    rows.append(("reported, never a stop: misfiled", _misfiled(english["misfiled"]),
                 _misfiled(spanish["mistranslations"]["misfiled"])))
    lines = ["stop threshold (run A's)  |  English  |  Spanish"]
    for name, en, es in rows:
        lines += ["", name, f"    English: {en}", f"    Spanish: {es}"]
    return "\n".join(lines)


# ---- A run, its stored answers scored again, and the two sets side by side -----------------------------------------


async def main_async(args: argparse.Namespace) -> int:
    api_key = os.environ.get("LLAMA_API_KEY", "")
    if not api_key:
        print("Set LLAMA_API_KEY to llama-server's key.", file=sys.stderr)
        return 2
    models = provenance.load(args.models_dir, verify=False)
    structuring = prompt.load()
    llama = LlamaServer(args.llama_url, api_key)
    structurer = Structurer(llama, structuring, models.language_file, timeout=Settings.structuring_seconds)
    scripts = load_all()
    chosen = [s for s in scripts.values() if not args.only or any(s.id == w or s.id.startswith(f"{w}-") for w in args.only)]
    header = {
        "run": "A in English: K2 Horizon on the English test set's text, no speech recognition (the expansion plan's "
               "step 17)",
        "set": "en", "started_at": datetime.now(UTC).isoformat(timespec="seconds"), "finished_at": None,
        "machine": machine(args.machine, "none"), "llama_server": None, "parameters": structuring.parameters,
        "timeout_seconds": Settings.structuring_seconds,
        "provenance": {"language": models.language, "prompt": structuring.prompt_sha256,
                       "schema": structuring.schema_sha256, "structuring": structuring.structuring_sha256,
                       "checks": CHECKS_CODE_SHA256, "pipeline": PIPELINE_CODE_SHA256},
        "dataset": {"scripts_sha256": scripts_sha256(SCRIPTS_DIR), "scripts": len(chosen), "complete": not args.only},
    }

    def save(items: list[scoring.Item], metrics: dict | None = None) -> None:
        """The results file as it stands: every script answered so far, and the measures once there are some."""
        header.update(finished_at=datetime.now(UTC).isoformat(timespec="seconds"), llama_server=structurer.build)
        write(args.out, header | {"items": [dataclasses.asdict(item) for item in items]}, metrics)

    items: list[scoring.Item] = []
    for number, script in enumerate(chosen, 1):
        item = await run_script(script, structurer, raw=lambda answered: save([*items, answered]))
        items.append(item)
        save(items)
        print(f"[{number}/{len(chosen)}] {script.id}: {item.sorting}{f' ({item.reason})' if item.reason else ''}, "
              f"{item.timings.get('structure', 0):.1f} s", flush=True)
    await llama.aclose()
    metrics = scoring.score(items, scripts)
    save(items, metrics)
    print(summary(metrics, incomplete([dataclasses.asdict(item) for item in items], list(scripts))))
    print(f"Wrote {args.out}")
    return 0


def write(out: Path, results: dict, metrics: dict | None) -> None:
    scored = {} if metrics is None else {"metrics": metrics, "thresholds": [
        {"threshold": name, "crossed": crossed, "measured": measured} for name, crossed, measured in thresholds(metrics)]}
    items = results.pop("items")
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(results | scored | {"items": items}, indent=1, ensure_ascii=False) + "\n",
                   encoding="utf-8")


def rescore(stored: dict, scripts: dict[str, Script]) -> dict:
    """The stored answers through the current English checks and scoring, refusing a script whose text has changed;
    the results file again, with what it was checked with before under "rescored"."""
    items = []
    for entry in stored["items"]:
        old = scoring.Item(**{key: value for key, value in entry.items() if key != "fields"})
        fields = {name: scoring.FieldOutcome(**outcome) for name, outcome in entry["fields"].items()}
        item, transcript = item_for(scripts[old.script])
        if transcript.text != old.hypothesis:
            raise ValueError(f"{old.script}'s text has changed since the run: run it again instead.")
        item = dataclasses.replace(item, sorting=old.sorting, reason=old.reason, timings=old.timings, tokens=old.tokens)
        if old.sorting == "done" and fields:
            check(item, {name: {"value": f.proposed, "sources": f.sources} for name, f in fields.items()}, old.unknown,
                  transcript)
        items.append(item)
    return stored | {
        "provenance": stored["provenance"] | {"checks": CHECKS_CODE_SHA256},
        "dataset": stored["dataset"] | {"scripts_sha256": scripts_sha256(SCRIPTS_DIR)},
        "items": [dataclasses.asdict(item) for item in items],
        "metrics": scoring.score(items, scripts),
        "rescored": {"at": datetime.now(UTC).isoformat(timespec="seconds"),
                     "note": "the stored answers checked and scored again; no model was run",
                     "before": {"checks": stored["provenance"].get("checks")}},
    }


def main_rescore(path: Path, out: Path) -> int:
    stored = json.loads(path.read_text(encoding="utf-8"))
    scripts = load_all()
    if gap := incomplete(stored["items"], list(scripts)):
        print(f"{path} isn't a whole run of the English set's {len(scripts)} scripts ({gap}): a script that timed out "
              "is a failure in every measure, never left out. Run it again whole.", file=sys.stderr)
        return 2
    results = rescore(stored, scripts)
    metrics = results.pop("metrics")
    write(out, results, metrics)
    print(summary(metrics))
    print(f"Wrote {out}")
    return 0


def main_compare(english_path: Path, spanish_path: Path) -> int:
    english = json.loads(english_path.read_text(encoding="utf-8")).get("metrics", {})
    spanish = json.loads(spanish_path.read_text(encoding="utf-8")).get("metrics", {})
    spanish = spanish.get("text", spanish)  # a Spanish recordings' file: its display texts, run A's measures
    if "misfiled" not in english or "discarded" not in english:
        print(f"{english_path}'s measures lack misfiled or discarded: score it again with this scorer first "
              "(run_a_en --rescore, or for the recordings rescore.py).", file=sys.stderr)
        return 2
    if "misfiled" not in spanish.get("mistranslations", {}) or "share" not in spanish["mistranslations"]["misfiled"]:
        print(f"{spanish_path}'s measures are from an earlier scorer: score it again first (run_a --rescore).",
              file=sys.stderr)
        return 2
    print(compare(english, spanish))
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--llama-url", help="llama-server's address, such as http://127.0.0.1:8080")
    parser.add_argument("--models-dir", type=Path, default=EVAL_DIR.parent / "infra" / "compose" / "models",
                        help="where models-manifest.json is; only read")
    parser.add_argument("--machine", help="a name for this machine in the results")
    parser.add_argument("--only", nargs="*", help="script ids to run, for a quick look")
    parser.add_argument("--rescore", type=Path, help="a results file whose stored answers to check and score again")
    parser.add_argument("--compare", nargs=2, type=Path, metavar=("ENGLISH", "SPANISH"),
                        help="print two results files' stop thresholds side by side: an English one, then a Spanish one")
    parser.add_argument("--out", type=Path, help=f"where the results go (a run's default: {OUT.relative_to(EVAL_DIR.parent)})")
    args = parser.parse_args(argv)
    if args.compare:
        return main_compare(*args.compare)
    if args.rescore:
        if args.out is None:
            print("Say where the new results go: --out <file>.", file=sys.stderr)
            return 2
        return main_rescore(args.rescore, args.out)
    if not args.llama_url:
        parser.error("--llama-url is required to run the model")
    args.out = args.out or OUT
    return asyncio.run(main_async(args))


if __name__ == "__main__":
    sys.exit(main())
