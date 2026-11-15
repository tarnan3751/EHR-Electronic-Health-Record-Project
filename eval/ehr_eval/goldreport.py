"""Writes eval/clinical-gold.md: which of the test set's clinical gold values the sidecar's own checks (validation.py)
would discard, each script's display text read as its transcript, with the reason codes and the rule-following gold
beside each. It shows what the checks' strict rules cost against what a careful clinician would accept; the eval's
fields_clinical score measures the same on the pipeline's answers. A test fails if the committed file isn't what this
writes, so after a change to the scripts or the checks:

    uv run --project eval python -m ehr_eval.goldreport
"""

from __future__ import annotations

import sys
from collections import Counter
from pathlib import Path

from ehr_dictation import validation
from ehr_dictation.prompt import FIELDS
from ehr_dictation.transcript import Placed, Segment, Transcript

from .scripts import EVAL_DIR, Script, load_all

REPORT = EVAL_DIR / "clinical-gold.md"


def transcript(text: str) -> Transcript:
    """A script's display text as one passage of a final transcript."""
    return Transcript(text, (Placed(Segment(0, 0, text, 1.0, None, True), 0, len(text)),), True)


def discarded(script: Script) -> dict[str, list[str]]:
    """The clinical gold fields the checks would discard, with their reason codes."""
    fields = {name: {"value": gold.value, "sources": list(gold.sources)} for name, gold in script.gold.items()}
    checks = validation.check(fields, [], transcript(script.display))
    return {name: checked.reasons for name, checked in checks.fields.items() if fields[name]["value"] and checked.reasons}


def report(scripts: dict[str, Script]) -> str:
    found = {script.id: discarded(script) for script in scripts.values()}
    values = sum(1 for script in scripts.values() for name in FIELDS if script.gold[name].value)
    count = sum(len(fields) for fields in found.values())
    reasons = Counter(reason for fields in found.values() for codes in fields.values() for reason in codes)
    lines = [
        "# The clinical gold against the dictation checks",
        "",
        "Written by `uv run --project eval python -m ehr_eval.goldreport` (`ehr_eval/goldreport.py`); don't edit it. Each "
        "script's display text is read as its transcript, and its clinical gold (`[gold.*]`), what a careful clinician "
        "would accept, is checked by the dictation service's own checks (`validation.py` in the sidecar). These are the "
        "values they would discard, with why, and the rule-following gold (`[gold_rules.*]`) that the eval measures the "
        "pipeline against instead. See README.md, Two gold sets.",
        "",
        f"{count} of {values} clinical gold values would be discarded. By reason (a value can have more than one):",
        "",
        "| Reason | Values |",
        "|---|---|",
        *(f"| `{reason}` | {n} |" for reason, n in sorted(reasons.items(), key=lambda item: (-item[1], item[0]))),
    ]
    for script_id, fields in found.items():
        for name, codes in fields.items():
            script = scripts[script_id]
            lines += [
                "",
                f"## {script_id}: {name}",
                "",
                f"Reasons: {', '.join(f'`{code}`' for code in codes)}",
                "",
                f"- Clinical gold: {script.gold[name].value}",
                f"- Rule-following gold: {script.gold_rules[name].value}",
            ]
    return "\n".join(lines) + "\n"


def main() -> int:
    REPORT.write_text(report(load_all()), encoding="utf-8")
    print(f"Wrote {REPORT.relative_to(Path.cwd()) if REPORT.is_relative_to(Path.cwd()) else REPORT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
