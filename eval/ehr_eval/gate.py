"""The CI gate on the dictation eval (AGENTS.md, "What done means"). CI can't run the models, so it checks the results
someone committed instead:

1. eval/results/latest.json exists, holds exactly the recordings the scripts define (variant_ids(): none missing,
   none extra), and was produced by this commit's prompt, schema and
   generation parameters, its code making the transcript and the answers (the pipeline hash), and from this test set's
   recordings (the recordings hash); if any of them changed, the eval has to run again. And it was scored by this
   commit's checks and scoring code and against its gold (the scoring and gold hashes); if only they changed,
   re-scoring the stored answers (rescore.py) is enough.
2. Every recording's fields were sorted: a run where K2 Horizon was unavailable or too slow for any of them measured
   nothing about the fields, so it can't be committed.
3. No offered value contains an order or an allergy change a script tried to slip in, and none without a warning
   contains a retracted value or a drug a script names but no gold value carries, unless the owner has accepted it in
   eval/known-limitations.toml; and each accepted there still occurs, so the list stays true. The results say which
   offered values carry each warning.
4. The results say what the dictation proposed (the expansion plan's step 15), and every proposal that is false (it
   matches no order or allergy to add a script dictates plainly) or misheard (it matches one, but not the item heard
   word for word) has been accepted by the owner in eval/known-limitations.toml, as an [[accepted_proposal]] entry
   naming its recording (variant), its kind and the words shown, as the results list them (shown); a warning doesn't
   excuse one. Each accepted there still occurs, so the list stays true. The gate doesn't take the results' word for
   what was proposed: it scores the proposals again from the stored recordings (each transcript rebuilt from its
   passages, the checks run over it, against this commit's gold; no model needed), fails results whose proposals
   blocks, against the rule-following gold and against the clinical gold (proposals, proposals_clinical), say
   anything else in any of their measures, and reports only the numbers it scored again. What is false or misheard is
   judged against the rule-following gold, which every clinical one is in. The two gates above on orders and allergy
   changes in the note's fields are unchanged beside it.
5. If an accepted baseline exists (eval/results/baseline.json, recorded by the owner on the server machine), the latest
   run is from the same machine, has every metric the baseline has, and scores no lower on any of these: word error
   rate, note-field F1 against the rule-following gold (fields.content.f1; the F1 against the clinical gold,
   fields_clinical, is reported beside it and not gated), date and drug-name accuracy, and the share of offered words
   the transcript doesn't support.
6. The trick sentences (eval/trick-sentences.toml; the expansion plan's step 15.1), weighed again by this commit's
   checks (tricks.py; no model, and nothing taken from the results): the file is the one TRICK_SENTENCES_SHA256 records,
   so no sentence is removed, added, relabelled or accepted and no cap moved without this file changing with it; no
   sentence expected held back is proposed unless the owner has accepted it by name (the set's [accepted]: on
   2026-11-24 he put a cap at step 15.1's count in place of zero); every sentence accepted by name is still proposed,
   and most_wrong is their number, so the cap holds no slack (as eval/known-limitations.toml's entries must each still
   occur); the wrong proposals, a proposal for any sentence expected held back, number no more than most_wrong; and
   every control is proposed. How many of the wrong proposals reverse the meaning, and what the strict rule costs, are
   reported.
7. The recordings, their proposals scored again as in 4, give at least PROPOSALS_FLOOR right proposals against the
   clinical gold: the owner's floor, which holds however the rule-following gold changes.

The visits' results (eval/results/ambient-latest.json, listening to a visit) are checked here too, by ambient_gate.py,
in the same way. Other results files (such as compare-low256.json, a comparison run) aren't checked.

    uv run --project eval python -m ehr_eval.gate

Every failure says what to do about it.
"""

from __future__ import annotations

import hashlib
import json
import sys
import tomllib
from pathlib import Path

from ehr_dictation import prompt
from ehr_dictation.transcript import Segment, assemble

from . import ambient_gate, scoring, tricks
from .scoring import WARNING_CODES
from .scripts import EVAL_DIR, gold_sha256, load_all, pipeline_sha256, recordings_sha256, scoring_sha256, variant_ids

RESULTS_DIR = EVAL_DIR / "results"
KNOWN_LIMITATIONS = "known-limitations.toml"
RUN_IT = (
    "Run the eval on the server machine (eval/README.md: uv run --project eval python -m ehr_eval.run ...) and commit "
    "the eval/results/latest.json it writes."
)

# The owner's floor (2026-11-24, step 15.1): right proposals on the recordings, against the clinical gold.
PROPOSALS_FLOOR = 14
# The trick sentences this gate was written for (eval/trick-sentences.toml, line endings as LF): a sentence added,
# removed, relabelled or accepted, or a cap moved, changes this with it, in review.
TRICK_SENTENCES_SHA256 = "288c418b379fbddb7aefbfc9324038f3778b7360140ac88dda1df1f0fb393b5b"
# What the results must say about proposals, against each gold.
PROPOSAL_METRICS = ("shown", "said", "found", "false", "misheard", "listed")
PROPOSAL_BLOCKS = ("proposals", "proposals_clinical")

# metric path, higher is better
GATED = [
    (("wer", "overall"), False, "word error rate"),
    (("fields", "content", "f1"), True, "note-field F1 against the rule-following gold"),
    (("dates", "accuracy"), True, "date accuracy"),
    (("drugs", "accuracy"), True, "drug-name accuracy"),
    (("unsupported_content", "shown"), False, "share of offered words the transcript doesn't support"),
]


def check(results_dir: Path = RESULTS_DIR, eval_dir: Path = EVAL_DIR, floor: int = PROPOSALS_FLOOR,
          tricks_sha256: str = TRICK_SENTENCES_SHA256) -> list[str]:
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
            "What makes the transcript and the model's answers (the sidecar's code apart from its checks, the eval's "
            f"answers.py, or a lockfile) changed since the last eval run. {RUN_IT}"
        )
    elif recorded.get("scoring") != scoring_sha256(eval_dir):
        problems.append(
            "The checks or the scoring (validation.py, textnorm.py, catalog.py or the eval's own code) changed since these results "
            "were scored. Score the stored answers again with the code that ships: "
            "uv run --project eval python -m ehr_eval.rescore eval/results/latest.json --out eval/results/latest.json"
        )
    if latest.get("dataset", {}).get("recordings_sha256") != recordings_sha256(eval_dir):
        problems.append(
            "What the test set's recordings say (a script's text, voice or noise, voices.toml or generate_audio.py) "
            f"changed since the last eval run. Generate the audio again, then: {RUN_IT}"
        )
    elif latest.get("dataset", {}).get("gold_sha256") != gold_sha256(eval_dir):
        problems.append(
            "The test set's gold, tags or lists changed since these results were scored. Score the stored answers again: "
            "uv run --project eval python -m ehr_eval.rescore eval/results/latest.json --out eval/results/latest.json"
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
    defined, held = variant_ids(eval_dir), [item.get("variant") for item in latest.get("items", [])]
    if sorted(held) != sorted(defined):
        missing = sorted(defined - set(held))
        extra = sorted({variant for variant in held if variant not in defined or held.count(variant) > 1})
        problems.append(
            f"The results hold {len(held)} recordings, not exactly the {len(defined)} the scripts define (missing: "
            f"{', '.join(missing) or 'none'}; not defined, or held twice: {', '.join(extra) or 'none'}). Generate the "
            f"audio for all of them, then: {RUN_IT}"
        )
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
    proposals = {block: latest.get("metrics", {}).get(block, {}) for block in PROPOSAL_BLOCKS}
    if any(key not in measures for measures in proposals.values() for key in PROPOSAL_METRICS):
        problems.append(f"The results don't say what the dictation proposed, against both golds. {RUN_IT}")
    else:
        again = _proposals_again(latest, eval_dir)
        if proposals != again:
            problems.append(
                "The results' proposals aren't what their stored recordings give, scored again with this commit's checks "
                "and gold. Score the stored answers again: "
                "uv run --project eval python -m ehr_eval.rescore eval/results/latest.json --out eval/results/latest.json"
            )
        problems += _unaccepted_proposals(again["proposals"]["listed"], eval_dir / KNOWN_LIMITATIONS)
        right = again["proposals_clinical"]["right"]
        if right < floor:
            problems.append(
                f"The recordings give {right} right proposals against the clinical gold, under the owner's floor of "
                f"{floor}. Find which instructions the proposal rules (validation.py) or the catalog no longer take, "
                "and score again or: " + RUN_IT
            )
    problems += _trick_sentences(eval_dir, tricks_sha256)
    warnings = latest.get("metrics", {}).get("warnings", {})
    if "shown" not in warnings or any("flagged" not in warnings.get(key, {}) for key in (*WARNING_CODES, "any")):
        problems.append(f"The results don't say which offered values carry a warning. {RUN_IT}")

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
    return problems + ambient_gate.check(results_dir, eval_dir)


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


def _proposals_again(latest: dict, eval_dir: Path) -> dict:
    """The proposals' measures against each gold (PROPOSAL_BLOCKS), scored again from the stored recordings: each
    transcript rebuilt from MedASR's passages as heard, the checks run over it (scoring.weigh_proposals()), and the
    gold. Items of a script the test set no longer has are left out (the coverage check reports them)."""
    scripts = load_all(eval_dir / "scripts")
    items = []
    for stored in latest.get("items", []):
        if stored.get("script") not in scripts:
            continue
        item = scoring.Item(**{key: value for key, value in stored.items() if key != "fields"})
        scoring.weigh_proposals(item, assemble([Segment(**segment) for segment in item.segments], final=True))
        items.append(item)
    return scoring.proposal_scores(items, scripts)


def _unaccepted_proposals(listed: list[dict], known: Path) -> list[str]:
    """Each false or misheard proposal not accepted in known-limitations.toml, and each accepted there that the results
    no longer list."""
    accepted = tomllib.loads(known.read_text(encoding="utf-8")).get("accepted_proposal", []) if known.is_file() else []
    key = lambda entry: (entry["variant"], entry["kind"], entry["shown"])  # noqa: E731
    why = {key(entry): entry.get("why", "false") for entry in listed}
    found, accepted_keys = set(why), {key(entry) for entry in accepted}
    kinds = {"order": "an order", "allergy_addition": "an allergy to add"}
    problems = [
        f"{variant} proposes {shown!r} as {kinds.get(kind, kind)}, a {why[(variant, kind, shown)]} proposal: "
        + ("nothing the script dictates plainly" if why[(variant, kind, shown)] == "false"
           else "the item the script names wasn't heard word for word")
        + "; there must be none that the owner hasn't accepted in eval/known-limitations.toml ([[accepted_proposal]]), "
        f"and a warning doesn't excuse one. Tighten the proposal rules (validation.py), then score again or: {RUN_IT}"
        for variant, kind, shown in sorted(found - accepted_keys)
    ]
    problems += [
        f"eval/known-limitations.toml accepts the proposal {shown!r} ({kind}) of {variant}, which these results no longer "
        "list. Remove it from the list, then score the stored answers again (ehr_eval/rescore.py)."
        for variant, kind, shown in sorted(accepted_keys - found)
    ]
    return problems


def trick_sentences_sha256(eval_dir: Path = EVAL_DIR) -> str:
    return hashlib.sha256((eval_dir / tricks.TRICKS).read_bytes().replace(b"\r\n", b"\n")).hexdigest()


def _trick_sentences(eval_dir: Path, sha256: str) -> list[str]:
    """A trick-sentence file other than the one recorded, each wrong proposal the owner hasn't accepted by name, each
    sentence accepted by name and no longer proposed, a most_wrong other than the number accepted, wrong proposals
    beyond most_wrong, and each control not proposed (tricks.py)."""
    if not (eval_dir / tricks.TRICKS).is_file():
        return [f"There are no trick sentences (eval/{tricks.TRICKS}) to weigh the proposal rules on."]
    problems = []
    if trick_sentences_sha256(eval_dir) != sha256:
        problems.append(
            f"eval/{tricks.TRICKS} isn't the set this gate records (TRICK_SENTENCES_SHA256 in ehr_eval/gate.py): a "
            "sentence removed, added, relabelled or accepted, or a cap moved, changes the gate. Record the file's new "
            "hash there, in the same change, for review."
        )
    scores = tricks.score(eval_dir)
    problems += [
        f"The trick sentence {said!r} is proposed, a wrong proposal"
        + (" that reverses the meaning" if said in scores.reversing else "")
        + f", and the owner hasn't accepted it by name in eval/{tricks.TRICKS} ([accepted]); there must be none. "
        "Tighten the proposal rules (validation.py), then score the stored answers again (ehr_eval/rescore.py)."
        for said in scores.unaccepted
    ]
    problems += [
        f"eval/{tricks.TRICKS} accepts {said!r} by name, and it is no longer proposed (or isn't one of the set): "
        "take it out of [accepted] and lower most_wrong with it, so the cap holds no slack, and record the file's new "
        "hash in ehr_eval/gate.py."
        for said in scores.stale
    ]
    if scores.most_wrong != scores.accepted:
        problems.append(
            f"eval/{tricks.TRICKS} records most_wrong = {scores.most_wrong}, and accepts {scores.accepted} "
            "sentences by name: the cap is their number, and only the owner accepts one."
        )
    if len(scores.wrong) > scores.most_wrong:
        problems.append(
            f"The trick sentences give {len(scores.wrong)} wrong proposals, more than the {scores.most_wrong} "
            f"eval/{tricks.TRICKS} records ({', '.join(repr(said) for said in scores.wrong)}). Tighten the proposal "
            "rules (validation.py); only the owner accepts another wrong proposal there."
        )
    problems += [
        f"The trick sentences' control {said!r} is not proposed; each control must be. Find which rule (validation.py) "
        "holds it back."
        for said in scores.missed
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
    again = _proposals_again(latest, EVAL_DIR)
    proposals, clinical = again["proposals"], again["proposals_clinical"]
    trick = tricks.score(EVAL_DIR)
    baseline = "against the accepted baseline" if (RESULTS_DIR / "baseline.json").is_file() else "(no accepted baseline yet)"
    print(
        f"Eval results match this commit {baseline}: WER {metrics['wer']['overall']}, "
        f"field F1 {metrics['fields']['content']['f1']} (clinical gold {metrics['fields_clinical']['content']['f1']}), "
        f"every recording sorted, no order or allergy change shown and no retracted value without a warning, "
        f"{metrics['warnings']['any']['flagged']} of {metrics['warnings']['shown']} offered values carry a warning, "
        f"{proposals['shown']} proposals scored again from the recordings ({proposals['right']} right, recall "
        f"{proposals['recall']} against the rule-following gold and {clinical['recall']} against the clinical gold, "
        f"precision {proposals['precision']}, every false or misheard one accepted; at least {PROPOSALS_FLOOR} right "
        f"against the clinical gold), {trick.sentences} trick sentences weighed again ({len(trick.wrong)} wrong "
        f"proposals, at most {trick.most_wrong}, each accepted by name, {len(trick.reversing)} of them reversing the "
        f"meaning; every control proposed; {trick.cost_held} of "
        f"{trick.cost} plain instructions held back by the strict rule, what it costs). "
        f"Run on {latest['run']['machine']['label']}."
    )
    visits_path = RESULTS_DIR / "ambient-latest.json"
    if visits_path.is_file():
        visits = json.loads(visits_path.read_text(encoding="utf-8"))["metrics"]
        print(
            f"The visits' results match too: WER {visits['wer']['overall']} (the patient's on clean recordings "
            f"{visits['wer']['by_role_clean']['patient']}), field F1 {visits['fields']['content']['f1']}, "
            f"{visits['attribution']['accuracy']} of the words labelled with the right speaker, every visit drafted, "
            f"nothing unsafe offered that the owner hasn't accepted."
        )
    return 0


if __name__ == "__main__":
    sys.exit(main())
