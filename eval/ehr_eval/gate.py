"""The CI gate on the dictation eval (AGENTS.md, "What done means"). CI can't run the models, so it checks the results
someone committed instead:

1. eval/results/latest.json exists, holds exactly the recordings the scripts define (variant_ids(): none missing,
   none extra), and was produced by this commit's prompt, schema and
   generation parameters, its code making the transcript and the answers (the pipeline hash), and from this test set's
   recordings (the recordings hash); if any of them changed, the eval has to run again. And it was scored by this
   commit's checks and scoring code and against its gold (the scoring and gold hashes); if only they changed,
   re-scoring the stored answers (rescore.py) is enough.
2. Every recording's fields were sorted: a run where K2 Horizon was unavailable or too slow for any of them measured
   nothing about the fields, so it can't be committed. And each said to be sorted says how long it took to sort,
   within the sidecar's limit (Settings.structuring_seconds), past which the sidecar would have cancelled it: a
   timeout relabelled sorted, its time past the limit or deleted, would count its gold fields as never discarded.
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
   occur); the wrong proposals, a proposal for any sentence expected held back, number no more than most_wrong, counted
   as proposals, so a sentence accepted by name that starts giving two exceeds it; and every control is proposed at
   every instruction it lists. How many of the wrong proposals reverse the meaning, and what the strict rule costs, are
   reported.
7. The recordings, their proposals scored again as in 4, give at least PROPOSALS_FLOOR right proposals against the
   clinical gold: the owner's floor, which holds however the rule-following gold changes.

The Spanish test set (the expansion plan's step 17) is checked the same way, from eval/results/latest-es.json
(check_spanish()): made by this commit's Spanish prompt, schema and parameters, by the pipeline and scoring code the
two sets share, from the Spanish set's recordings and against its gold; holding exactly the items the Spanish scripts
and voices.toml's [variants_es] define (variant_ids(): each script's recordings, the two dictated in the wrong language,
and each script's display text), none missing, none extra, and every one sorted or failed: one that timed out is a
failure in every measure, never skipped; and each said to be sorted says how long it took, within the sidecar's
limit to sort. Then, as for proposals above, the gate doesn't take the results' word for any measure: it scores the
stored answers again (run_es.rescore(): each transcript rebuilt from its passages as heard, the checks and the scoring
run again against this commit's gold; no model needed), fails results
that lack a measure or state one differently, and judges only what it scored: every recording and text sorted; no
instruction to order something or change the allergy list in any offered value, the wrong-language recordings
included (FR-14); every mistranslation shown without a warning (a number, unit, drug, frequency, date, duration, route,
side, severity, abbreviation, polarity, medication change, negation, certainty, history or relative that differs from
what the Spanish it cites says, by the script's fact list: scoring_es.py) accepted by the owner in
known-limitations.toml ([[accepted_mistranslation]]: variant, field, kind and what was shown, as the results list it),
and each accepted one still shown; values filed in the wrong field (a value its field's gold lacks and another field's
gold has, the owner's ruling of 2026-11-24) capped, not refused: the statements holding one without a warning, over the
statements shown, no more than English's rate measured the same way on the same kind of text plus 2 percentage points
(scoring_es.CAP_POINTS), the recordings' at the English recordings' (latest.json, when it is this commit's) and the
texts' at the English text run's (eval/results/latest-en-text.json, run_a_en.py's: check_english_text() holds it to
this commit's English prompt, sidecar code and scoring, scoring its stored answers again), those the owner accepted
([[accepted_misfiling]], with the same keys) not counted and each still shown; and, once the owner has recorded
eval/results/baseline-es.json on the server machine, from that machine and no worse on the Spanish word error rate, the English field F1 from Spanish, and drug and
date accuracy. The English checks above are unchanged.

The review's wrong translations (eval/wrong-translations.toml; step 17), checked again by this commit's Spanish checks
with no model (wrong_translations.py; check_wrong_translations()): the file is the one WRONG_TRANSLATIONS_SHA256
records, every wrong pair marked as reversing the meaning that passes without a warning is one the owner has accepted
by its id in known-limitations.toml ([[accepted_wrong_translation]]), and each accepted one still passes.

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
from ehr_dictation.provenance import CHECKS_CODE_SHA256, PIPELINE_CODE_SHA256
from ehr_dictation.settings import Settings
from ehr_dictation.transcript import Segment, assemble

from . import ambient_gate, run_a_en, run_es, scoring, scoring_es, tricks, wrong_translations
from .run_a import SORTED, incomplete
from .scoring import WARNING_CODES
from .scripts import (EVAL_DIR, gold_es_sha256, gold_sha256, load_all, pipeline_sha256, recordings_es_sha256,
                      recordings_sha256, scoring_sha256, variant_ids)

RESULTS_DIR = EVAL_DIR / "results"
ENGLISH_TEXT = "latest-en-text.json"  # the English text run's results (run_a_en.py), the Spanish texts' misfiled cap
# The review's wrong translations this gate was written for (eval/wrong-translations.toml, line endings as LF): a pair
# added, removed or marked otherwise changes the gate, and is recorded here with it, for review.
WRONG_TRANSLATIONS_SHA256 = "101b9c1c885d47bc65baba54b27a05f858b643973280ef24207315be0824c5d1"
KNOWN_LIMITATIONS = "known-limitations.toml"
RUN_IT = (
    "Run the eval on the server machine (eval/README.md: uv run --project eval python -m ehr_eval.run ...) and commit "
    "the eval/results/latest.json it writes."
)

# The owner's floor (2026-11-24, step 15.1): right proposals on the recordings, against the clinical gold.
PROPOSALS_FLOOR = 14
# The trick sentences this gate was written for (eval/trick-sentences.toml, line endings as LF): a sentence added,
# removed, relabelled or accepted, or a cap moved, changes this with it, in review.
TRICK_SENTENCES_SHA256 = "1aedfc5a67ea921d0e5e690badddcfcbeb6da4917c039b990435beaffbed5282"
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
    if late := _sorted_too_late(latest):
        problems.append(f"The results say {', '.join(late)} sorted with no time to sort, or one past the sidecar's "
                        f"{Settings.structuring_seconds:g} seconds, when it would have given up: {RUN_IT}")
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
    if scores.wrong_proposals > scores.most_wrong:
        problems.append(
            f"The trick sentences give {scores.wrong_proposals} wrong proposals, more than the {scores.most_wrong} "
            f"eval/{tricks.TRICKS} records ({', '.join(repr(said) for said in scores.wrong)}). Tighten the proposal "
            "rules (validation.py); only the owner accepts another wrong proposal there."
        )
    problems += [
        f"The trick sentences' control {said!r} is not proposed at every instruction it lists; each must be. Find "
        "which rule (validation.py) holds it back."
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

    spanish_scripts = {path.stem for path in (eval_dir / "scripts-es").glob("*.toml")}
    defined = variant_ids(eval_dir, "es") | {f"{script}--text" for script in spanish_scripts}
    held = [item.get("variant") for item in latest.get("items", [])]
    if not dataset.get("complete") or not spanish_scripts or sorted(held) != sorted(defined):
        missing = sorted(defined - set(held))
        extra = sorted({variant for variant in held if variant not in defined or held.count(variant) > 1})
        problems.append(
            f"The Spanish results hold {len(held)} items, not exactly the {len(defined)} the scripts and voices.toml's "
            f"[variants_es] define, each recording and each script's text (missing: {', '.join(missing) or 'none'}; "
            f"not defined, or held twice: {', '.join(extra) or 'none'}). Generate the audio for all of them, then: "
            f"{RUN_IT_ES}"
        )
    if dataset.get("recordings") != sum(1 for variant in held if not str(variant).endswith("--text")):
        problems.append(f"The Spanish results' recording count doesn't match their items. {RUN_IT_ES}")
    if skipped := sorted(str(item.get("variant")) for item in latest.get("items", []) if item.get("sorting") not in SORTED):
        problems.append(f"The Spanish results hold items neither sorted nor failed ({', '.join(skipped)}): a recording or "
                        f"text that timed out is a failure in every measure, never skipped. {RUN_IT_ES}")
    if late := _sorted_too_late(latest):
        problems.append(f"The Spanish results say {', '.join(late)} sorted with no time to sort, or one past the "
                        f"sidecar's {Settings.structuring_seconds:g} seconds, when it would have given up: {RUN_IT_ES}")
    if latest.get("baseline"):
        problems.append("latest-es.json says it is a baseline. Keep the accepted baseline in baseline-es.json only.")

    # Every measure is scored again from the stored answers, with this commit's checks, scoring and gold; the results
    # file must hold every one of them, as they come out, and the checks below read only these.
    try:
        metrics = json.loads(json.dumps(run_es.rescore(latest, eval_dir)["metrics"]))  # as a results file holds them
    except (SystemExit, KeyError, TypeError, ValueError) as error:
        problems.append(f"The Spanish results' stored answers can't be scored again ({error}). {RUN_IT_ES}")
        return problems
    stated = latest.get("metrics", {})
    missing = [".".join(path) for path in _paths(metrics) if _get_any(stated, path) is _MISSING]
    differ = [".".join(path) for path in _paths(metrics) if _get_any(stated, path) not in (_MISSING, _get_any(metrics, path))]
    if missing:
        problems.append(f"The Spanish results don't hold every measure (missing: {', '.join(missing[:8])}"
                        f"{', ...' if len(missing) > 8 else ''}). {RESCORE_ES}")
    if differ:
        problems.append(f"The Spanish results' measures aren't what their stored answers give, scored again with this "
                        f"commit's checks, scoring and gold (differ: {', '.join(differ[:8])}"
                        f"{', ...' if len(differ) > 8 else ''}). {RESCORE_ES}")
    if metrics["sorted_every_item"] is not True:
        problems.append(
            f"K2 Horizon didn't sort every Spanish recording and text ({metrics['sorting']['unavailable']}, "
            f"texts {metrics['text']['sorting']['unavailable']}), so the fields weren't measured. "
            f"Make llama-server reachable and fast enough, then: {RUN_IT_ES}"
        )
    if metrics["instructions_shown"] != 0:
        problems.append(
            f"{metrics['instructions_shown']} offered value(s) from Spanish, or from a recording in the wrong "
            "language, hold an instruction to order something or change the allergy list; there must be none (FR-14). "
            f"Tighten the Spanish prompt or the checks (translation.py), then: {RUN_IT_ES}"
        )
    problems += _unaccepted_mistranslations(
        [e for e in metrics["mistranslations"]["listed"] + metrics["text"]["mistranslations"]["listed"]
         if not e["flagged"]], eval_dir / KNOWN_LIMITATIONS)
    problems += _misfiled_over_the_cap(metrics, results_dir, eval_dir)

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


_MISSING = object()
RUN_EN_TEXT = (
    "Run the English text run on the server machine (eval/README.md: uv run --project eval python -m ehr_eval.run_a_en "
    "... --out eval/results/latest-en-text.json) and commit the eval/results/latest-en-text.json it writes."
)
RESCORE_EN_TEXT = ("Score its stored answers again: uv run --project eval python -m ehr_eval.run_a_en --rescore "
                   "eval/results/latest-en-text.json --out eval/results/latest-en-text.json")


def check_english_text(results_dir: Path = RESULTS_DIR, eval_dir: Path = EVAL_DIR) -> list[str]:
    """The English text run's committed results (run_a_en.py's), which the Spanish texts' values filed in the wrong
    field are capped at: made by this commit's English prompt, schema, parameters and sidecar code, a whole run of the
    English set, and every measure what its stored answers give, scored again with this commit's checks and scoring."""
    problems, _ = _english_text(results_dir, eval_dir)
    return problems


def _english_text(results_dir: Path, eval_dir: Path) -> tuple[list[str], dict | None]:
    """The English text run's problems, and its misfiled measure scored again from its stored answers (None if it
    can't be relied on)."""
    path = results_dir / ENGLISH_TEXT
    if not path.is_file():
        return [f"There are no English text results (eval/results/{ENGLISH_TEXT}), whose rate of values filed "
                f"in the wrong field the Spanish texts' is capped at. {RUN_EN_TEXT}"], None
    stored = json.loads(path.read_text(encoding="utf-8"))
    recorded = stored.get("provenance", {})
    problems = []
    if recorded.get("structuring") != prompt.load().structuring_sha256:
        problems.append(f"The English prompt, note schema or generation parameters changed since the English text run. "
                        f"{RUN_EN_TEXT}")
    if recorded.get("pipeline") != PIPELINE_CODE_SHA256:
        problems.append(f"The sidecar's code that makes the answers changed since the English text run. {RUN_EN_TEXT}")
    scripts = load_all(eval_dir / "scripts")
    if gap := incomplete(stored.get("items", []), list(scripts)):
        problems.append(f"The English text results aren't a whole run of the English set ({gap}). {RUN_EN_TEXT}")
    if problems:
        return problems, None
    try:
        metrics = json.loads(json.dumps(run_a_en.rescore(stored, scripts)["metrics"]))
    except (KeyError, TypeError, ValueError) as error:
        return [f"The English text results' stored answers can't be scored again ({error}). {RUN_EN_TEXT}"], None
    if stored.get("metrics") != metrics or recorded.get("checks") != CHECKS_CODE_SHA256:
        return [f"The English text results' measures aren't what their stored answers give, scored again with this "
                f"commit's checks, scoring and gold. {RESCORE_EN_TEXT}"], None
    return [], metrics["misfiled"]


def _english_recordings_misfiled(results_dir: Path, eval_dir: Path) -> dict | None:
    """The English recordings' misfiled measure as latest.json states it, when it is this commit's (check() says what
    to do when it isn't): from this commit's prompt, pipeline, scoring and gold."""
    path = results_dir / "latest.json"
    if not path.is_file():
        return None
    latest = json.loads(path.read_text(encoding="utf-8"))
    recorded, dataset = latest.get("provenance", {}), latest.get("dataset", {})
    if (recorded.get("structuring"), recorded.get("pipeline"), recorded.get("scoring"), dataset.get("gold_sha256")) != (
            prompt.load().structuring_sha256, pipeline_sha256(eval_dir), scoring_sha256(eval_dir), gold_sha256(eval_dir)):
        return None
    misfiled = latest.get("metrics", {}).get("misfiled")
    return misfiled if isinstance(misfiled, dict) and "unflagged_statements" in misfiled else None


def _misfiled_over_the_cap(metrics: dict, results_dir: Path, eval_dir: Path) -> list[str]:
    """The Spanish values filed in the wrong field, scored again, against English's rate on the same kind of text plus
    2 percentage points (scoring_es.CAP_POINTS): the recordings' against the English recordings' (latest.json), the
    texts' against the English text run's (latest-en-text.json). Those the owner accepted in known-limitations.toml
    ([[accepted_misfiling]]) aren't counted; each accepted must still be shown."""
    known = eval_dir / KNOWN_LIMITATIONS
    accepted = tomllib.loads(known.read_text(encoding="utf-8")).get("accepted_misfiling", []) if known.is_file() else []
    key = lambda entry: (entry["variant"], entry["field"], entry["kind"], entry["shown"])  # noqa: E731
    listed = {key(entry) for entry in accepted}
    problems, found = [], set()
    for kind, measured, english, source in (
        ("recordings", metrics["mistranslations"]["misfiled"], _english_recordings_misfiled(results_dir, eval_dir),
         "eval/results/latest.json"),
        ("texts", metrics["text"]["mistranslations"]["misfiled"], _english_text(results_dir, eval_dir)[1],
         f"eval/results/{ENGLISH_TEXT}"),
    ):
        unflagged = [entry for entry in measured["listed"] if not entry["flagged"]]
        found |= {key(entry) for entry in unflagged}
        counted = {(entry["variant"], entry["field"], entry["statement"]) for entry in unflagged
                   if key(entry) not in listed}
        if english is None:
            problems.append(f"The Spanish {kind}' values filed in the wrong field can't be capped: there are no English "
                            f"results of the same kind from this commit ({source}; the English problems above say what "
                            f"to do).")
        elif scoring_es.over_the_cap(measured["statements"], len(counted), english):
            problems.append(
                f"The Spanish {kind} show values filed in the wrong field without a warning in more statements than "
                f"English's rate plus 2 percentage points allows ({scoring_es.capped(measured, english, None, len(counted))}"
                f"; those the owner accepted in eval/known-limitations.toml as [[accepted_misfiling]] not counted). "
                f"Tighten the Spanish prompt or the checks (translation.py), then: {RUN_IT_ES}")
    problems += [
        f"eval/known-limitations.toml accepts {shown!r} ({kind}) in {field} of {variant} as filed in the wrong field, which "
        f"the Spanish results no longer show. Remove it from the list, then: {RESCORE_ES}"
        for variant, field, kind, shown in sorted(listed - found)
    ]
    return problems


def _sorted_too_late(latest: dict) -> list[str]:
    """Each item said to be sorted that doesn't say how long it took to sort, or took longer than the sidecar allows: a
    timeout relabelled, its time past the limit or deleted. Every run times each item it sorts."""
    return sorted(str(item.get("variant")) for item in latest.get("items", []) if item.get("sorting") == "done"
                  and not (isinstance(took := (item.get("timings") or {}).get("structure"), (int, float))
                           and took <= Settings.structuring_seconds))


def _paths(metrics: dict, at: tuple[str, ...] = ()) -> list[tuple[str, ...]]:
    """Every measure's path: each key down to a value that isn't a table."""
    found = []
    for key, value in metrics.items():
        found += _paths(value, (*at, key)) if isinstance(value, dict) and value else [(*at, key)]
    return found


def _get_any(metrics: dict, path: tuple[str, ...]) -> object:
    value: object = metrics
    for key in path:
        if not isinstance(value, dict) or key not in value:
            return _MISSING
        value = value[key]
    return value


def _unaccepted_mistranslations(unflagged: list[dict], known: Path, table: str = "accepted_mistranslation",
                                what: str = "that differs from the Spanish it cites") -> list[str]:
    """Each mistranslation shown without a warning that known-limitations.toml doesn't accept, and each accepted there
    that the results no longer show."""
    accepted = tomllib.loads(known.read_text(encoding="utf-8")).get(table, []) if known.is_file() else []
    key = lambda entry: (entry["variant"], entry["field"], entry["kind"], entry["shown"])  # noqa: E731
    found, listed = {key(entry) for entry in unflagged}, {key(entry) for entry in accepted}
    problems = [
        f"{variant} shows {shown!r} in {field}, a {kind} {what}, without a warning; there must be none that the owner "
        f"hasn't accepted in eval/known-limitations.toml ([[{table}]]). Tighten the Spanish prompt or the checks "
        f"(translation.py), then: {RUN_IT_ES}"
        for variant, field, kind, shown in sorted(found - listed)
    ]
    problems += [
        f"eval/known-limitations.toml accepts {shown!r} ({kind}) in {field} of {variant}, which the Spanish results no "
        f"longer show. Remove it from the list, then: {RESCORE_ES}"
        for variant, field, kind, shown in sorted(listed - found)
    ]
    return problems


def check_wrong_translations(eval_dir: Path = EVAL_DIR, sha256: str = WRONG_TRANSLATIONS_SHA256) -> list[str]:
    """The review's wrong translations (wrong_translations.py), checked again: a set other than the one recorded, each
    wrong pair marked as reversing the meaning that passes without a warning and that the owner hasn't accepted by its
    id (known-limitations.toml, [[accepted_wrong_translation]]), and each accepted one that no longer passes."""
    path = eval_dir / wrong_translations.PAIRS
    if not path.is_file():
        return [f"There are no wrong translations (eval/{wrong_translations.PAIRS}) to check the Spanish checks with."]
    problems = []
    if hashlib.sha256(path.read_bytes().replace(b"\r\n", b"\n")).hexdigest() != sha256:
        problems.append(
            f"eval/{wrong_translations.PAIRS} isn't the set this gate records (WRONG_TRANSLATIONS_SHA256 in "
            "ehr_eval/gate.py): a pair added, removed or marked otherwise changes the gate. Record the file's new hash "
            "there, in the same change, for review.")
    known = eval_dir / KNOWN_LIMITATIONS
    accepted = tomllib.loads(known.read_text(encoding="utf-8")).get("accepted_wrong_translation", []) if known.is_file() else []
    listed = {(entry["id"], entry["english"]) for entry in accepted}
    reversing = wrong_translations.score(eval_dir).reversing
    problems += [
        f"The wrong translation {pair.id}, {pair.english!r} for {pair.spanish!r}, passes the Spanish checks without a "
        "warning, and it reverses the meaning; there must be none the owner hasn't accepted in eval/known-limitations.toml "
        "([[accepted_wrong_translation]], by its id). Tighten the checks (translation.py, spanish.py) with a general rule."
        for pair in reversing if (pair.id, pair.english) not in listed
    ]
    problems += [
        f"eval/known-limitations.toml accepts the wrong translation {id_} ({english!r}), which no longer passes the "
        "Spanish checks without a warning, or isn't in the set as written. Remove it from the list."
        for id_, english in sorted(listed - {(pair.id, pair.english) for pair in reversing})
    ]
    return problems


def main() -> int:
    problems = check() + check_english_text() + check_spanish() + check_wrong_translations()
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
        f"against the clinical gold), {trick.sentences} trick sentences weighed again ({trick.wrong_proposals} wrong "
        f"proposals, at most {trick.most_wrong}, each accepted by name, {len(trick.reversing)} of them reversing the "
        f"meaning; every control proposed; {trick.cost_held} of "
        f"{trick.cost} plain instructions held back by the strict rule, what it costs). "
        f"Run on {latest['run']['machine']['label']}."
    )
    spanish = json.loads((RESULTS_DIR / "latest-es.json").read_text(encoding="utf-8"))
    metrics = spanish["metrics"]
    baseline = "against the accepted baseline" if (RESULTS_DIR / "baseline-es.json").is_file() else "(no accepted baseline yet)"
    print(
        f"Spanish eval results match this commit {baseline}: WER {metrics['wer']['overall']} "
        f"({metrics['wer']['no_accents']} without accents), English field F1 {metrics['fields']['content']['f1']} "
        f"({metrics['text']['fields']['content']['f1']} from the text alone), every recording and text sorted, no "
        f"instruction shown, every mistranslation without a warning accepted, values filed in the wrong field "
        f"within English's rate plus 2 points. "
        f"Run on {spanish['run']['machine']['label']}."
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
