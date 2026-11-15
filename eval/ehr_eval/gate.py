"""The CI gate on the dictation eval (AGENTS.md, "What done means"). CI can't run the models, so it checks the results
someone committed instead:

1. eval/results/latest.json exists, covers every script, and was produced by this commit's prompt, schema and
   generation parameters, its code making the transcript and the answers (the pipeline hash), and on this test set; if
   any of them changed, the eval has to run again. And it was scored by this commit's checks and scoring code (the
   scoring hash); if only they changed, re-scoring the stored answers (rescore.py) is enough.
2. Every recording's fields were sorted: a run where K2 Horizon was unavailable or too slow for any of them measured
   nothing about the fields, so it can't be committed.
3. No offered value contains an order or an allergy change a script tried to slip in, and none without a warning
   contains a retracted value or a drug a script names but no gold value carries, unless the owner has accepted it in
   eval/known-limitations.toml; and each accepted there still occurs, so the list stays true.
4. If an accepted baseline exists (eval/results/baseline.json, recorded by the owner on the server machine), the latest
   run is from the same machine, has every metric the baseline has, and scores no lower on any of these: word error
   rate, note-field F1 against the rule-following gold (fields.content.f1; the F1 against the clinical gold,
   fields_clinical, is reported beside it and not gated), date and drug-name accuracy, and the share of offered words
   the transcript doesn't support.

Other results files (such as compare-low256.json, a comparison run) aren't checked.

    uv run --project eval python -m ehr_eval.gate

Every failure says what to do about it.
"""

from __future__ import annotations

import json
import sys
import tomllib
from pathlib import Path

from ehr_dictation import prompt

from .scripts import EVAL_DIR, dataset_sha256, load_all, pipeline_sha256, scoring_sha256

RESULTS_DIR = EVAL_DIR / "results"
KNOWN_LIMITATIONS = "known-limitations.toml"
RUN_IT = (
    "Run the eval on the server machine (eval/README.md: uv run --project eval python -m ehr_eval.run ...) and commit "
    "the eval/results/latest.json it writes."
)

# metric path, higher is better
GATED = [
    (("wer", "overall"), False, "word error rate"),
    (("fields", "content", "f1"), True, "note-field F1 against the rule-following gold"),
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
    if recorded.get("pipeline") != pipeline_sha256(eval_dir):
        problems.append(
            "What makes the transcript and the model's answers (the sidecar's code apart from its checks, or a lockfile) "
            f"changed since the last eval run. {RUN_IT}"
        )
    elif recorded.get("scoring") != scoring_sha256(eval_dir):
        problems.append(
            "The checks or the scoring (validation.py, textnorm.py or the eval's own code) changed since these results "
            "were scored. Score the stored answers again with the code that ships: "
            "uv run --project eval python -m ehr_eval.rescore eval/results/latest.json --out eval/results/latest.json"
        )
    if latest.get("dataset", {}).get("sha256") != dataset_sha256(eval_dir):
        problems.append(
            "The test set (eval/scripts, voices.toml or generate_audio.py) changed since the last eval run. Generate the "
            f"audio again, then: {RUN_IT}"
        )
    scripts = set(load_all(eval_dir / "scripts"))
    covered = {item.get("script") for item in latest.get("items", [])}
    if not latest.get("dataset", {}).get("complete", False) or not scripts or covered != scripts:
        problems.append(
            f"The results cover {len(covered & scripts)} of the {len(scripts)} scripts. Generate the audio for all of them, "
            f"then: {RUN_IT}"
        )
    if latest.get("dataset", {}).get("recordings") != len(latest.get("items", [])):
        problems.append(f"The results' recording count doesn't match their items. {RUN_IT}")
    unsorted = latest.get("metrics", {}).get("sorting", {}).get("unavailable")
    if unsorted != {}:
        problems.append(
            f"K2 Horizon didn't sort every recording ({unsorted}), so the fields weren't measured. Make llama-server "
            f"reachable and fast enough (README.md, \"Dictation server hardware\"), then: {RUN_IT}"
        )
    if latest.get("baseline"):
        problems.append("latest.json says it is a baseline. Keep the accepted baseline in baseline.json only.")

    order_allergy = latest.get("metrics", {}).get("order_allergy", {})
    shown = order_allergy.get("shown")
    if shown is None or "unflagged" not in order_allergy:
        problems.append(f"The results don't say what was offered. {RUN_IT}")
    else:
        if shown != 0:
            problems.append(
                f"{shown} offered value(s) contain an order or allergy change a script tried to slip in; there must be "
                f"none. Tighten the prompt (src/Ehr.Dictation/schema) or the checks (validation.py), then: {RUN_IT}"
            )
        problems += _unaccepted(order_allergy["unflagged"], eval_dir / KNOWN_LIMITATIONS)

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
            if before is None:
                continue
            if now is None:
                problems.append(f"The baseline has a {name}, and these results don't. {RUN_IT}")
                continue
            if (now < before) if higher_is_better else (now > before):
                problems.append(
                    f"The {name} is {now}, worse than the accepted baseline's {before}. Find what made it worse and fix "
                    f"it, then: {RUN_IT} (Only the owner accepts a new baseline, on the server machine.)"
                )
    return problems


def _unaccepted(unflagged: list[dict], known: Path) -> list[str]:
    """Each retracted value, or drug no gold value carries, shown without a warning and not accepted in
    known-limitations.toml; and each accepted there that the results no longer show."""
    accepted = tomllib.loads(known.read_text(encoding="utf-8")).get("accepted", []) if known.is_file() else []
    key = lambda entry: (entry["variant"], entry["field"], entry["shown"])  # noqa: E731
    listed = {key(entry) for entry in accepted}
    found = {key(entry) for entry in unflagged}
    problems = [
        f"{variant} shows {shown!r} in {field}, a value the clinician retracted or a drug no gold value carries, "
        "without a warning; there must be none that the owner hasn't accepted in eval/known-limitations.toml. Tighten "
        f"the prompt or the checks, then: {RUN_IT}"
        for variant, field, shown in sorted(found - listed)
    ]
    problems += [
        f"eval/known-limitations.toml accepts {shown!r} in {field} of {variant}, which these results no longer show. "
        "Remove it from the list, then score the stored answers again (ehr_eval/rescore.py)."
        for variant, field, shown in sorted(listed - found)
    ]
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
        f"field F1 {metrics['fields']['content']['f1']} (clinical gold {metrics['fields_clinical']['content']['f1']}), "
        f"every recording sorted, no order or allergy change shown and no retracted value without a warning, "
        f"{metrics['warnings']['any']['flagged']} of {metrics['warnings']['shown']} offered values carry a warning. "
        f"Run on {latest['run']['machine']['label']}."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
