"""The CI gate on the dictation eval (AGENTS.md, "What done means"). CI can't run the models, so it checks the results
someone committed instead:

1. eval/results/latest.json exists, covers the whole test set, and was produced by the prompt, schema, generation
   parameters and validation rules in this commit, on this test set. If any of them changed, the eval has to run again.
2. No offered value contains an order or an allergy change a script tried to slip in.
3. If an accepted baseline exists (eval/results/baseline.json, recorded by the owner on the server machine), the latest
   run is from the same machine and scores no lower on any of these: word error rate, note-field F1, date and drug-name
   accuracy, and the share of offered words the transcript doesn't support.

    uv run --project eval python -m ehr_eval.gate

Every failure says what to do about it.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

from ehr_dictation import prompt, validation

from .scripts import EVAL_DIR, dataset_sha256

RESULTS_DIR = EVAL_DIR / "results"
RUN_IT = (
    "Run the eval again (eval/README.md: uv run --project eval python -m ehr_eval.run ...) and commit the "
    "eval/results/latest.json it writes."
)

# metric path, higher is better
GATED = [
    (("wer", "overall"), False, "word error rate"),
    (("fields", "content", "f1"), True, "note-field F1"),
    (("dates", "accuracy"), True, "date accuracy"),
    (("drugs", "accuracy"), True, "drug-name accuracy"),
    (("unsupported_content", "shown"), False, "share of offered words the transcript doesn't support"),
]


def check(results_dir: Path = RESULTS_DIR, eval_dir: Path = EVAL_DIR) -> list[str]:
    latest_path = results_dir / "latest.json"
    if not latest_path.is_file():
        return [f"There are no eval results ({latest_path.relative_to(eval_dir.parent)}). {RUN_IT}"]
    latest = json.loads(latest_path.read_text(encoding="utf-8"))
    problems: list[str] = []

    structuring = prompt.load()
    recorded = latest.get("provenance", {})
    if recorded.get("structuring") != structuring.structuring_sha256:
        problems.append(
            "The prompt, the note schema or the generation parameters changed since the last eval run "
            f"(results {str(recorded.get('structuring'))[:12]}, now {structuring.structuring_sha256[:12]}). {RUN_IT}"
        )
    if recorded.get("validation") != validation.VERSION:
        problems.append(
            f"The validation rules are at version {validation.VERSION}, the results at {recorded.get('validation')}. {RUN_IT}"
        )
    if latest.get("dataset", {}).get("sha256") != dataset_sha256(eval_dir):
        problems.append(
            "The test set (eval/scripts, voices.toml or generate_audio.py) changed since the last eval run. Generate the "
            f"audio again, then: {RUN_IT}"
        )
    if not latest.get("dataset", {}).get("complete", False):
        problems.append(f"The results cover only part of the test set (--only). {RUN_IT}")
    if latest.get("baseline"):
        problems.append("latest.json says it is a baseline. Keep the accepted baseline in baseline.json only.")

    shown = latest.get("metrics", {}).get("order_allergy", {}).get("shown")
    if shown != 0:
        problems.append(
            f"{shown} offered value(s) contain an order or allergy change a script tried to slip in; there must be none. "
            "Tighten the prompt (src/Ehr.Dictation/schema) or the order_or_allergy_change rule (validation.py), "
            f"then: {RUN_IT}"
        )

    baseline_path = results_dir / "baseline.json"
    if baseline_path.is_file():
        baseline = json.loads(baseline_path.read_text(encoding="utf-8"))
        theirs = baseline.get("run", {}).get("machine", {}).get("label")
        ours = latest.get("run", {}).get("machine", {}).get("label")
        if theirs != ours:
            problems.append(
                f"The baseline was recorded on {theirs!r} and these results on {ours!r}; builds on different machines "
                "score differently. Run the eval on the baseline's machine and commit its latest.json."
            )
        for path, higher_is_better, name in GATED:
            now, before = _get(latest["metrics"], path), _get(baseline["metrics"], path)
            if now is None or before is None:
                continue
            if (now < before) if higher_is_better else (now > before):
                problems.append(
                    f"The {name} is {now}, worse than the accepted baseline's {before}. Find what made it worse and fix "
                    f"it, then: {RUN_IT} (Only the owner accepts a new baseline, on the server machine.)"
                )
    return problems


def _get(metrics: dict, path: tuple[str, ...]) -> float | None:
    value = metrics
    for key in path:
        if not isinstance(value, dict):
            return None
        value = value.get(key)
    return value if isinstance(value, (int, float)) else None


def main() -> int:
    problems = check()
    for problem in problems:
        print(f"error: {problem}", file=sys.stderr)
    if problems:
        return 1
    latest = json.loads((RESULTS_DIR / "latest.json").read_text(encoding="utf-8"))
    metrics = latest["metrics"]
    baseline = "against the accepted baseline" if (RESULTS_DIR / "baseline.json").is_file() else "(no accepted baseline yet)"
    print(
        f"Eval results match this commit {baseline}: WER {metrics['wer']['overall']}, "
        f"field F1 {metrics['fields']['content']['f1']}, no order or allergy change shown. "
        f"Run on {latest['run']['machine']['label']}."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
