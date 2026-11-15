"""Checks a run's stored proposals again with the current validation rules, and scores them, without running the models.

    uv run --project eval python -m ehr_eval.rescore eval/results/latest.json [--out eval/results/latest.json]

A run keeps, for each recording, MedASR's passages as heard and K2 Horizon's proposals as answered (run.py). This
rebuilds each transcript from those passages exactly, runs validation.py over the proposals, and writes the results
again, stamped with the current code and measurement hashes and everything else as the run recorded it: the model
outputs are the same ones, and only what is checked and scored has changed. It is how a change to the rules is
measured on the same answers (for example strict against relaxed), and it refuses a run made with another prompt,
schema or parameters, whose answers the current code would never have been given.
"""

from __future__ import annotations

import argparse
import dataclasses
import json
import sys
from pathlib import Path

from ehr_dictation import prompt, validation
from ehr_dictation.provenance import CODE_SHA256
from ehr_dictation.transcript import Segment, assemble

from . import scoring
from .scripts import load_all, measurement_sha256


def rescore(results: dict) -> dict:
    structuring = prompt.load(parameters=results["run"]["parameters"])
    if results["provenance"]["structuring"] != structuring.structuring_sha256:
        raise ValueError("These results came from another prompt, schema or parameters: run the eval again instead.")
    items = []
    for stored in results["items"]:
        item = scoring.Item(**{key: value for key, value in stored.items() if key != "fields"})
        item.not_carried_over = [tuple(entry) for entry in item.not_carried_over]
        item.fields = {name: scoring.FieldOutcome(**outcome) for name, outcome in stored["fields"].items()}
        if item.sorting == "done" and item.fields:
            transcript = assemble([Segment(**segment) for segment in item.segments], final=True)
            proposed = {name: {"value": outcome.proposed, "sources": outcome.sources} for name, outcome in item.fields.items()}
            checks = validation.check(proposed, item.unknown, transcript)
            for name, checked in checks.fields.items():
                item.fields[name].status = "pending" if checked.pending else "empty"
                item.fields[name].reasons = checked.reasons
                item.fields[name].value = checked.value if checked.pending else ""
        items.append(item)
    out = dict(results)
    out["provenance"] = results["provenance"] | {"code": CODE_SHA256, "measurement": measurement_sha256()}
    # The model's answers are the run's own; only the checks and the scoring are this code's.
    out["rescored_from"] = {
        "code": results["provenance"]["code"],
        "measurement": results["provenance"]["measurement"],
        "structuring_outputs_reused": True,
    }
    out["metrics"] = scoring.score(items, load_all())
    out["items"] = [dataclasses.asdict(item) for item in items]
    return out


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("results", type=Path)
    parser.add_argument("--out", type=Path, help="default: the same file")
    args = parser.parse_args(argv)
    rescored = rescore(json.loads(args.results.read_text(encoding="utf-8")))
    (args.out or args.results).write_text(json.dumps(rescored, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Wrote {args.out or args.results}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
