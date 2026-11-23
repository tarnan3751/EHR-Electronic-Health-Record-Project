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
   block says anything else in any of its measures, and reports only the numbers it scored again. The two gates above on orders and allergy
   changes in the note's fields are unchanged beside it.
5. If an accepted baseline exists (eval/results/baseline.json, recorded by the owner on the server machine), the latest
   run is from the same machine, has every metric the baseline has, and scores no lower on any of these: word error
   rate, note-field F1 against the rule-following gold (fields.content.f1; the F1 against the clinical gold,
   fields_clinical, is reported beside it and not gated), date and drug-name accuracy, and the share of offered words
   the transcript doesn't support.

The Spanish test set (the expansion plan's step 17) is checked the same way, from eval/results/latest-es.json
(check_spanish()): made by this commit's Spanish prompt, schema and parameters, by the pipeline and scoring code the
two sets share, from the Spanish set's recordings and against its gold; covering every Spanish script's recordings and
display text and the two recordings dictated in the wrong language; every recording and text sorted; no instruction to
order something or change the allergy list in any offered value, the wrong-language recordings included (FR-14); every
mistranslation shown without a warning (a number, unit, drug, frequency, date, duration, route, side, severity,
abbreviation, polarity, medication change, negation, certainty, history or relative that differs from what the Spanish it
cites says, by the script's fact list: scoring_es.py) accepted by the owner in known-limitations.toml
([[accepted_mistranslation]]: variant, field, kind and what was shown, as the results list it), and each accepted one
still shown; and, once the owner has recorded eval/results/baseline-es.json on the server machine, from that machine
and no worse on the Spanish word error rate, the English field F1 from Spanish, and drug and date accuracy. The English
checks above are unchanged.

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
from ehr_dictation.transcript import Segment, assemble

from . import scoring
from .scoring import WARNING_CODES
from .scripts import (EVAL_DIR, gold_es_sha256, gold_sha256, load_all, pipeline_sha256, recordings_es_sha256,
                      recordings_sha256, scoring_sha256, variant_ids)

RESULTS_DIR = EVAL_DIR / "results"
KNOWN_LIMITATIONS = "known-limitations.toml"
RUN_IT = (
    "Run the eval on the server machine (eval/README.md: uv run --project eval python -m ehr_eval.run ...) and commit "
    "the eval/results/latest.json it writes."
)

# What the results must say about proposals.
PROPOSAL_METRICS = ("shown", "said", "found", "false", "misheard", "listed")

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
    proposals = latest.get("metrics", {}).get("proposals", {})
    if any(key not in proposals for key in PROPOSAL_METRICS):
        problems.append(f"The results don't say what the dictation proposed. {RUN_IT}")
    else:
        again = _proposals_again(latest, eval_dir)
        if proposals != again:
            problems.append(
                "The results' proposals aren't what their stored recordings give, scored again with this commit's checks "
                "and gold. Score the stored answers again: "
                "uv run --project eval python -m ehr_eval.rescore eval/results/latest.json --out eval/results/latest.json"
            )
        problems += _unaccepted_proposals(again["listed"], eval_dir / KNOWN_LIMITATIONS)
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


def _proposals_again(latest: dict, eval_dir: Path) -> dict:
    """The proposals' measures, scored again from the stored recordings: each transcript rebuilt from MedASR's passages
    as heard, the checks run over it (scoring.weigh_proposals()), and the scripts' gold. Items of a script the test set
    no longer has are left out (the coverage check reports them)."""
    scripts = load_all(eval_dir / "scripts")
    items = []
    for stored in latest.get("items", []):
        if stored.get("script") not in scripts:
            continue
        item = scoring.Item(**{key: value for key, value in stored.items() if key != "fields"})
        scoring.weigh_proposals(item, assemble([Segment(**segment) for segment in item.segments], final=True))
        items.append(item)
    return scoring._proposals(items, scripts)


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


def _get(metrics: dict, path: tuple[str, ...]) -> float | None:
    value = metrics
    for key in path:
        if not isinstance(value, dict):
            return None
        value = value.get(key)
    return value if isinstance(value, (int, float)) else None


RUN_IT_ES = (
    "Run the Spanish eval on the server machine (eval/README.md: generate_audio.py --set es, then "
    "uv run --project eval python -m ehr_eval.run --set es ...) and commit the eval/results/latest-es.json it writes."
)
RESCORE_ES = (
    "Score the stored answers again: uv run --project eval python -m ehr_eval.run --set es --rescore "
    "eval/results/latest-es.json --out eval/results/latest-es.json"
)
GATED_ES = [
    (("wer", "overall"), False, "Spanish word error rate"),
    (("fields", "content", "f1"), True, "English note-field F1 from Spanish"),
    (("drugs", "accuracy"), True, "Spanish drug-name accuracy"),
    (("dates", "accuracy"), True, "Spanish date accuracy"),
]


def check_spanish(results_dir: Path = RESULTS_DIR, eval_dir: Path = EVAL_DIR) -> list[str]:
    """The Spanish results' checks (the module's header)."""
    latest_path = results_dir / "latest-es.json"
    if not latest_path.is_file():
        return [f"There are no Spanish eval results ({latest_path.relative_to(eval_dir.parent)}). {RUN_IT_ES}"]
    latest = json.loads(latest_path.read_text(encoding="utf-8"))
    problems: list[str] = []
    recorded, dataset = latest.get("provenance", {}), latest.get("dataset", {})
    if recorded.get("structuring") != prompt.load(language="es").structuring_sha256:
        problems.append(f"The Spanish prompt, note schema or generation parameters changed since the Spanish eval run. {RUN_IT_ES}")
    if recorded.get("pipeline") != pipeline_sha256(eval_dir):
        problems.append(f"What makes the transcripts and the model's answers changed since the Spanish eval run. {RUN_IT_ES}")
    elif recorded.get("scoring") != scoring_sha256(eval_dir):
        problems.append(f"The checks or the scoring changed since the Spanish results were scored. {RESCORE_ES}")
    if dataset.get("recordings_sha256") != recordings_es_sha256(eval_dir):
        problems.append("What the Spanish test set's recordings say (a script's text, voice or noise, voices.toml or "
                        f"generate_audio.py) changed since the Spanish eval run. Generate the audio again, then: {RUN_IT_ES}")
    elif dataset.get("gold_sha256") != gold_es_sha256(eval_dir):
        problems.append(f"The Spanish test set's gold, tags or lists changed since these results were scored. {RESCORE_ES}")

    scripts = {path.stem for path in (eval_dir / "scripts-es").glob("*.toml")}
    items = latest.get("items", [])
    texts = {item["script"] for item in items if item.get("variant", "").endswith("--text")}
    recordings = [item for item in items if not item.get("variant", "").endswith("--text")]
    spoken = {item["script"] for item in recordings if not item.get("run_as")}
    wrong = {item.get("run_as") for item in recordings if item.get("run_as")}
    if not dataset.get("complete") or not scripts or spoken != scripts or texts != scripts or wrong != {"en", "es"}:
        problems.append(
            f"The Spanish results cover {len(spoken & scripts)} of the {len(scripts)} scripts' recordings, "
            f"{len(texts & scripts)} of their texts and {len(wrong)} of the 2 recordings in the wrong language. "
            f"Generate the audio for all of them, then: {RUN_IT_ES}"
        )
    if dataset.get("recordings") != len(recordings):
        problems.append(f"The Spanish results' recording count doesn't match their items. {RUN_IT_ES}")
    metrics = latest.get("metrics", {})
    if metrics.get("sorted_every_item") is not True:
        problems.append(
            f"K2 Horizon didn't sort every Spanish recording and text ({metrics.get('sorting', {}).get('unavailable')}, "
            f"texts {metrics.get('text', {}).get('sorting', {}).get('unavailable')}), so the fields weren't measured. "
            f"Make llama-server reachable and fast enough, then: {RUN_IT_ES}"
        )
    if metrics.get("instructions_shown") != 0:
        problems.append(
            f"{metrics.get('instructions_shown')} offered value(s) from Spanish, or from a recording in the wrong "
            "language, hold an instruction to order something or change the allergy list; there must be none (FR-14). "
            f"Tighten the Spanish prompt or the checks (translation.py), then: {RUN_IT_ES}"
        )
    if latest.get("baseline"):
        problems.append("latest-es.json says it is a baseline. Keep the accepted baseline in baseline-es.json only.")
    listed = metrics.get("mistranslations", {}).get("listed")
    in_text = metrics.get("text", {}).get("mistranslations", {}).get("listed")
    if listed is None or in_text is None:
        problems.append(f"The Spanish results don't say what was mistranslated. {RUN_IT_ES}")
    else:
        problems += _unaccepted_mistranslations([e for e in listed + in_text if not e["flagged"]],
                                                eval_dir / KNOWN_LIMITATIONS)

    baseline_path = results_dir / "baseline-es.json"
    if baseline_path.is_file():
        baseline = json.loads(baseline_path.read_text(encoding="utf-8"))
        theirs = baseline.get("run", {}).get("machine", {}).get("label")
        ours = latest.get("run", {}).get("machine", {}).get("label")
        if theirs != ours:
            problems.append(f"The Spanish baseline was recorded on {theirs!r} and these results on {ours!r}. Run the "
                            "Spanish eval on the baseline's machine and commit its latest-es.json.")
        for path, higher_is_better, name in GATED_ES:
            now, before = _get(metrics, path), _get(baseline.get("metrics", {}), path)
            if before is None:
                continue
            if now is None:
                problems.append(f"The Spanish baseline has a {name}, and these results don't. {RUN_IT_ES}")
            elif (now < before) if higher_is_better else (now > before):
                problems.append(f"The {name} is {now}, worse than the accepted baseline's {before}. Find what made it "
                                f"worse and fix it, then: {RUN_IT_ES} (Only the owner accepts a new baseline.)")
    return problems


def _unaccepted_mistranslations(unflagged: list[dict], known: Path) -> list[str]:
    """Each mistranslation shown without a warning that known-limitations.toml doesn't accept, and each accepted there
    that the results no longer show."""
    accepted = tomllib.loads(known.read_text(encoding="utf-8")).get("accepted_mistranslation", []) if known.is_file() else []
    key = lambda entry: (entry["variant"], entry["field"], entry["kind"], entry["shown"])  # noqa: E731
    found, listed = {key(entry) for entry in unflagged}, {key(entry) for entry in accepted}
    problems = [
        f"{variant} shows {shown!r} in {field}, a {kind} that differs from the Spanish it cites, without a warning; "
        "there must be none that the owner hasn't accepted in eval/known-limitations.toml ([[accepted_mistranslation]]). "
        f"Tighten the Spanish prompt or the checks (translation.py), then: {RUN_IT_ES}"
        for variant, field, kind, shown in sorted(found - listed)
    ]
    problems += [
        f"eval/known-limitations.toml accepts {shown!r} ({kind}) in {field} of {variant}, which the Spanish results no "
        f"longer show. Remove it from the list, then: {RESCORE_ES}"
        for variant, field, kind, shown in sorted(listed - found)
    ]
    return problems


def main() -> int:
    problems = check() + check_spanish()
    for problem in problems:
        print(f"error: {problem}", file=sys.stderr)
    if problems:
        return 1
    latest = json.loads((RESULTS_DIR / "latest.json").read_text(encoding="utf-8"))
    metrics = latest["metrics"]
    proposals = _proposals_again(latest, EVAL_DIR)
    baseline = "against the accepted baseline" if (RESULTS_DIR / "baseline.json").is_file() else "(no accepted baseline yet)"
    print(
        f"Eval results match this commit {baseline}: WER {metrics['wer']['overall']}, "
        f"field F1 {metrics['fields']['content']['f1']} (clinical gold {metrics['fields_clinical']['content']['f1']}), "
        f"every recording sorted, no order or allergy change shown and no retracted value without a warning, "
        f"{metrics['warnings']['any']['flagged']} of {metrics['warnings']['shown']} offered values carry a warning, "
        f"{proposals['shown']} proposals scored again from the recordings ({proposals['right']} right, recall "
        f"{proposals['recall']}, precision {proposals['precision']}, every false or misheard one accepted). "
        f"Run on {latest['run']['machine']['label']}."
    )
    spanish = json.loads((RESULTS_DIR / "latest-es.json").read_text(encoding="utf-8"))
    metrics = spanish["metrics"]
    baseline = "against the accepted baseline" if (RESULTS_DIR / "baseline-es.json").is_file() else "(no accepted baseline yet)"
    print(
        f"Spanish eval results match this commit {baseline}: WER {metrics['wer']['overall']} "
        f"({metrics['wer']['no_accents']} without accents), English field F1 {metrics['fields']['content']['f1']} "
        f"({metrics['text']['fields']['content']['f1']} from the text alone), every recording and text sorted, no "
        f"instruction shown, every mistranslation without a warning accepted. "
        f"Run on {spanish['run']['machine']['label']}."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
