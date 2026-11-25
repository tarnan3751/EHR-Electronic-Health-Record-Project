"""gate.py: what CI accepts as the committed eval results, and what it tells you to do when it doesn't."""

from __future__ import annotations

import dataclasses
import json

import pytest

from ehr_dictation import prompt
from ehr_dictation.transcript import Segment, assemble
from ehr_eval import conversations, gate, scoring
from ehr_eval.scripts import (
    EVAL_DIR,
    PIPELINE_FILES,
    SCORING_FILES,
    gold_sha256,
    load_all,
    pipeline_sha256,
    recordings_sha256,
    scoring_sha256,
)


def check(results_dir, eval_dir, floor: int = 0, tricks_sha256: str | None = None) -> list[str]:
    """The gate, with no floor of right proposals (the small test set has one at most, d02's chest X-ray, and the
    owner's floor is tested on its own), recording the trick sentences the eval directory holds unless told which."""
    recorded = (eval_dir / "trick-sentences.toml").is_file() and gate.trick_sentences_sha256(eval_dir)
    return gate.check(results_dir, eval_dir, floor=floor, tricks_sha256=tricks_sha256 or recorded or "")


# Plain, and heard right: d02's chest X-ray is proposed where the script says it.
HEARD = "Takes aprocitentan daily. Go ahead and order a chest X-ray."


def stored(variant: str, script: str, heard: str) -> dict:
    """A recording as a run stores it, MedASR's passage included, with what the checks propose from it."""
    segment = Segment(0, 64000, heard, 0.9, None, True)
    transcript = assemble([segment], final=True)
    item = scoring.Item(variant=variant, script=script, tags=["accent:us", "noise:none", "container:webm"], seconds=4.0,
                        hypothesis=transcript.text, sorting="done", segments=[dataclasses.asdict(segment)],
                        timings={"structure": 12.0})
    scoring.weigh_proposals(item, transcript)
    return dataclasses.asdict(item)


def results(eval_dir, heard: str = HEARD, **overrides) -> dict:
    items = [stored("d01--clean", "d01", "Cough for 3 days. Seen on March 3. Plan: rest."), stored("d02--clean", "d02", heard)]
    proposals = scoring.proposal_scores([scoring.Item(**item) for item in items], load_all(eval_dir / "scripts"))
    data = {
        "baseline": False,
        "run": {"machine": {"label": "the server"}},
        "provenance": {"structuring": prompt.load().structuring_sha256, "pipeline": pipeline_sha256(eval_dir),
                       "scoring": scoring_sha256(eval_dir)},
        "dataset": {"recordings_sha256": recordings_sha256(eval_dir), "gold_sha256": gold_sha256(eval_dir),
                    "complete": True, "recordings": 2},
        "metrics": {"wer": {"overall": 0.1}, "fields": {"content": {"f1": 0.8}}, "dates": {"accuracy": 0.9},
                    "drugs": {"accuracy": 0.7}, "unsupported_content": {"shown": 0.02},
                    "order_allergy": {"shown": 0, "retracted_shown": 2, "retracted_shown_unflagged": 0,
                                      "drug_not_in_gold_shown": 1, "drug_not_in_gold_shown_unflagged": 0,
                                      "unflagged": []},
                    **proposals,
                    "warnings": {"shown": 3, "possible_correction": {"flagged": 1}, "near_unclear_passage": {"flagged": 0},
                                 "one_field_holds_dictation": {"flagged": 0}, "any": {"flagged": 1}},
                    "sorting": {"done": 2, "unavailable": {}}},
        "items": items,
    }
    for key, value in overrides.items():
        data[key] = value
    return data


def save(eval_dir, name: str, data: dict) -> None:
    (eval_dir / "results").mkdir(exist_ok=True)
    (eval_dir / "results" / name).write_text(json.dumps(data))


def test_matching_results_with_no_baseline_pass(eval_dir):
    save(eval_dir, "latest.json", results(eval_dir))

    assert check(eval_dir / "results", eval_dir) == []


def test_no_results_says_to_run_the_eval(eval_dir):
    (problem,) = check(eval_dir / "results", eval_dir)

    assert "no eval results" in problem and "Run the eval" in problem


def test_results_from_another_prompt_pipeline_or_test_set_fail(eval_dir):
    stale = results(eval_dir)
    stale["provenance"] = {"structuring": "0" * 64, "pipeline": "1" * 64, "scoring": "2" * 64}
    save(eval_dir, "latest.json", stale)
    (eval_dir / "scripts" / "d01.toml").write_text(
        (eval_dir / "scripts" / "d01.toml").read_text().replace("Cough for", "Coughing for", 1))

    problems = check(eval_dir / "results", eval_dir)

    assert len(problems) == 3
    assert all("Run the eval" in p for p in problems)


def test_results_scored_against_other_gold_need_only_scoring_again(eval_dir):
    save(eval_dir, "latest.json", results(eval_dir))
    script = eval_dir / "scripts" / "d01.toml"
    script.write_text(script.read_text().replace('value = "Rest."', 'value = "Rest at home."'))

    (problem,) = check(eval_dir / "results", eval_dir)

    assert "Score the stored answers again" in problem and "Run the eval" not in problem


def test_results_scored_by_other_checks_need_only_scoring_again(eval_dir):
    rescorable = results(eval_dir)
    rescorable["provenance"]["scoring"] = "2" * 64
    save(eval_dir, "latest.json", rescorable)

    (problem,) = check(eval_dir / "results", eval_dir)

    assert "Score the stored answers again" in problem and "Run the eval" not in problem


def test_the_code_feeding_the_models_needs_a_new_run_and_the_rest_only_scoring_again(eval_dir):
    (eval_dir / "ehr_eval").mkdir()
    save(eval_dir, "latest.json", results(eval_dir))
    (eval_dir / "ehr_eval" / "answers.py").write_text("feeds the recordings to the models another way")

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "Run the eval" in problem

    save(eval_dir, "latest.json", results(eval_dir))
    (eval_dir / "ehr_eval" / "run.py").write_text("checks what they answer another way")

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "Score the stored answers again" in problem and "Run the eval" not in problem


def test_every_eval_module_is_hashed_with_the_answers_or_the_scoring():
    modules = {f"ehr_eval/{path.name}" for path in (EVAL_DIR / "ehr_eval").glob("*.py")}
    hashed = {*PIPELINE_FILES, *SCORING_FILES, *conversations.PIPELINE_FILES, *conversations.SCORING_FILES}

    # Neither the package, the gates, the clinical gold report nor the trick sentences' scorer (which the gate runs
    # itself every time) makes or scores an answer: a dictation's (the first two lists; the Spanish eval's, the expansion
    # plan's step 17, with the English set's, since run A's code sorts each script's text in its run) or a visit's (the
    # visits' own, conversations.py); nor the review's wrong translations, which the gate checks the Spanish checks with,
    # apart from any results.
    assert modules - hashed == {"ehr_eval/__init__.py", "ehr_eval/gate.py", "ehr_eval/ambient_gate.py",
                                "ehr_eval/goldreport.py", "ehr_eval/tricks.py", "ehr_eval/wrong_translations.py"}
    assert not set(PIPELINE_FILES) & set(SCORING_FILES)
    assert not set(conversations.PIPELINE_FILES) & set(conversations.SCORING_FILES)


def test_results_that_miss_a_script_or_a_sorting_fail(eval_dir):
    missing = results(eval_dir)
    missing["items"] = missing["items"][:1]
    missing["dataset"]["recordings"] = 1
    missing["metrics"]["sorting"]["unavailable"] = {"model_timeout": 1}
    save(eval_dir, "latest.json", missing)

    problems = check(eval_dir / "results", eval_dir)

    assert any("cover 1 of the 2 scripts" in p for p in problems)
    assert any("didn't sort every recording" in p for p in problems)


# The review's cases: a timeout relabelled sorted, its time left past the limit or deleted.
@pytest.mark.parametrize("timings", [{"structure": 95.0}, {}, {"decode": 0.4, "transcribe": 3.0}, {"structure": None}])
def test_a_recording_said_to_be_sorted_past_the_limit_or_untimed_fails(eval_dir, timings):
    late = results(eval_dir)
    late["items"][1]["timings"] = timings
    save(eval_dir, "latest.json", late)

    assert any("d02--clean sorted with no time to sort, or one past the sidecar's 90 seconds" in p
               for p in check(eval_dir / "results", eval_dir))


# The review's case: recordings dropped and the counts made to agree still fail, as do a recording held twice or one no
# script defines.
@pytest.mark.parametrize("change", ["drop", "twice", "undefined"])
def test_results_that_hold_other_recordings_than_the_scripts_define_fail(eval_dir, change):
    other = results(eval_dir)
    if change == "drop":
        other["items"] = other["items"][:1]
        other["dataset"]["recordings"] = 1
    elif change == "twice":
        other["items"][1] = dict(other["items"][0])
        other["items"][1]["script"] = "d02"
    else:
        other["items"][1] = dict(other["items"][1], variant="d02--hvac-20db")
    other["metrics"] |= scoring.proposal_scores(
        [scoring.Item(**item) for item in other["items"]], load_all(eval_dir / "scripts"))
    save(eval_dir, "latest.json", other)

    problems = check(eval_dir / "results", eval_dir)

    assert any("not exactly the 2 the scripts define" in p for p in problems)


def test_results_that_dont_say_what_was_shown_fail(eval_dir):
    silent = results(eval_dir)
    silent["metrics"]["order_allergy"] = {}
    save(eval_dir, "latest.json", silent)

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "don't say what was offered" in problem


@pytest.mark.parametrize(
    "missing", [None, "shown", "possible_correction", "near_unclear_passage", "one_field_holds_dictation", "any"])
def test_results_that_dont_say_which_values_carry_a_warning_fail(eval_dir, missing):
    silent = results(eval_dir)
    if missing:
        del silent["metrics"]["warnings"][missing]
    else:
        del silent["metrics"]["warnings"]
    save(eval_dir, "latest.json", silent)

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "don't say which offered values carry a warning" in problem


def test_partial_results_or_a_baseline_flag_fail(eval_dir):
    partial = results(eval_dir, baseline=True)
    partial["dataset"]["complete"] = False
    save(eval_dir, "latest.json", partial)

    assert len(check(eval_dir / "results", eval_dir)) == 2


# TC-UC10-06 (NFR-05, FR-12, FR-14): field safety, checked on every change: committed eval results that show an
# order or allergy change fail CI's eval step (ci.yml, "Check the committed eval results").
def test_an_order_or_allergy_change_shown_fails(eval_dir):
    shown = results(eval_dir)
    shown["metrics"]["order_allergy"]["shown"] = 1
    save(eval_dir, "latest.json", shown)

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "there must be none" in problem


UNFLAGGED = {"variant": "d01--babble-5db", "field": "plan", "shown": "metoprolol"}
ACCEPTED = """
[[accepted]]
variant = "d01--babble-5db"
field = "plan"
shown = "metoprolol"
reason = "misheard cue"
accepted = "2026-11-15"
by = "the owner"
"""


def test_a_retracted_value_shown_without_a_warning_fails_unless_accepted(eval_dir):
    shown = results(eval_dir)
    shown["metrics"]["order_allergy"]["unflagged"] = [UNFLAGGED]
    save(eval_dir, "latest.json", shown)

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "without a warning" in problem and "d01--babble-5db" in problem

    (eval_dir / "known-limitations.toml").write_text(ACCEPTED, encoding="utf-8")
    shown["provenance"]["scoring"] = scoring_sha256(eval_dir)
    save(eval_dir, "latest.json", shown)

    assert check(eval_dir / "results", eval_dir) == []


def test_an_accepted_limitation_the_results_no_longer_show_is_stale(eval_dir):
    (eval_dir / "known-limitations.toml").write_text(ACCEPTED, encoding="utf-8")
    save(eval_dir, "latest.json", results(eval_dir))

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "no longer show" in problem


def test_a_metric_the_baseline_has_and_the_results_lack_fails(eval_dir):
    save(eval_dir, "baseline.json", results(eval_dir))
    lacking = results(eval_dir)
    lacking["metrics"]["fields"]["content"]["f1"] = None
    save(eval_dir, "latest.json", lacking)

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "note-field F1" in problem


# TC-UC9-06 (NFR-14, FR-07): English dictation accuracy. Committed results scoring below the accepted baseline fail
# CI's eval step; the live-transcript speed is test_sessions.py. Also TC-UC10-06 (NFR-05, FR-12, FR-14): the
# structuring scores and time are held to the baseline on every change.
def test_a_score_below_the_accepted_baseline_fails(eval_dir):
    save(eval_dir, "baseline.json", results(eval_dir))
    worse = results(eval_dir)
    worse["metrics"]["wer"]["overall"] = 0.12
    worse["metrics"]["fields"]["content"]["f1"] = 0.79
    save(eval_dir, "latest.json", worse)

    problems = check(eval_dir / "results", eval_dir)

    assert len(problems) == 2 and "word error rate" in problems[0] and "note-field F1" in problems[1]


def test_results_from_another_machine_than_the_baselines_fail(eval_dir):
    save(eval_dir, "baseline.json", results(eval_dir))
    save(eval_dir, "latest.json", results(eval_dir, run={"machine": {"label": "a laptop"}}))

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "baseline's machine" in problem


# TC-UC10-12 (NFR-05; FR-14 as reworded, the expansion plan's step 15): the results say what the dictation proposed,
# and every false or misheard proposal must be accepted by the owner, a warning or not; each accepted one must still be
# listed. The gate scores the proposals again from the stored recordings, so results that say less fail. The gates on
# orders and allergy changes in the fields stand beside these, unchanged.
MISHEARD = "Takes aprocitentan daily. Go ahead and order a lumbar spine X-ray."
FALSE = "Takes aprocitentan daily. Go ahead and order a lipid panel."


def accepting(shown: str) -> str:
    return f"""
[[accepted_proposal]]
variant = "d02--clean"
kind = "order"
shown = "{shown}"
reason = "misheard"
accepted = "2026-11-22"
by = "the owner"
"""


@pytest.mark.parametrize("block", ["proposals", "proposals_clinical"])
@pytest.mark.parametrize("missing", ["shown", "said", "found", "false", "misheard", "listed"])
def test_results_that_dont_say_what_was_proposed_fail(eval_dir, block, missing):
    silent = results(eval_dir)
    del silent["metrics"][block][missing]
    save(eval_dir, "latest.json", silent)

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "don't say what the dictation proposed" in problem


@pytest.mark.parametrize(("why", "heard", "shown"), [
    ("misheard", MISHEARD, "go ahead and order a lumbar spine x ray"),
    ("false", FALSE, "go ahead and order a lipid panel"),
])
def test_a_false_or_misheard_proposal_fails_unless_accepted(eval_dir, why, heard, shown):
    listed = results(eval_dir, heard)
    assert listed["metrics"]["proposals"]["listed"] == [{"variant": "d02--clean", "kind": "order", "shown": shown, "why": why}]
    save(eval_dir, "latest.json", listed)

    (problem,) = check(eval_dir / "results", eval_dir)
    assert f"a {why} proposal" in problem and "d02--clean" in problem and "warning doesn't excuse" in problem

    (eval_dir / "known-limitations.toml").write_text(accepting(shown), encoding="utf-8")
    listed["provenance"]["scoring"] = scoring_sha256(eval_dir)
    save(eval_dir, "latest.json", listed)

    assert check(eval_dir / "results", eval_dir) == []


# Every measure of both proposals blocks is scored again, not only the ones the owner's list is checked against.
@pytest.mark.parametrize("block", ["proposals", "proposals_clinical"])
@pytest.mark.parametrize("altered", [{"right": 0}, {"recall": 0.5}, {"precision": 0.5}, {"with_warnings": 7},
                                     {"held_back": {"not_plain": 3}}])
def test_results_whose_proposals_block_says_anything_else_fail(eval_dir, block, altered):
    tampered = results(eval_dir)
    tampered["metrics"][block] |= altered
    save(eval_dir, "latest.json", tampered)

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "aren't what their stored recordings give" in problem


# The review's case: a results file that says it listed nothing, its proposals altered, still lists the misheard one.
@pytest.mark.parametrize("altered", [{"listed": []}, {"listed": [], "misheard": 0, "right": 1}])
def test_results_whose_proposals_were_altered_fail_and_list_what_their_recordings_give(eval_dir, altered):
    tampered = results(eval_dir, MISHEARD)
    tampered["metrics"]["proposals"] |= altered
    save(eval_dir, "latest.json", tampered)

    problems = check(eval_dir / "results", eval_dir)

    assert len(problems) == 2
    assert "aren't what their stored recordings give" in problems[0]
    assert "a misheard proposal" in problems[1] and "go ahead and order a lumbar spine x ray" in problems[1]


def test_an_accepted_proposal_the_results_no_longer_list_is_stale(eval_dir):
    (eval_dir / "known-limitations.toml").write_text(accepting("go ahead and order a lumbar spine x ray"), encoding="utf-8")
    fresh = results(eval_dir)
    fresh["provenance"]["scoring"] = scoring_sha256(eval_dir)
    save(eval_dir, "latest.json", fresh)

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "no longer list" in problem and "lumbar spine x ray" in problem


def test_an_accepted_retracted_value_doesnt_accept_a_proposal(eval_dir):
    (eval_dir / "known-limitations.toml").write_text(ACCEPTED, encoding="utf-8")
    both = results(eval_dir, MISHEARD)
    both["metrics"]["order_allergy"]["unflagged"] = [UNFLAGGED]
    both["provenance"]["scoring"] = scoring_sha256(eval_dir)
    save(eval_dir, "latest.json", both)

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "a misheard proposal" in problem


# TC-UC10-20 (NFR-05; FR-14 as reworded, the expansion plan's step 15.1): the trick sentences, weighed again by the
# checks on every change, and pinned: none expected held back may be proposed unless the owner has accepted it by name,
# each accepted by name must still be proposed and most_wrong must be their number, the wrong proposals may number no
# more than the set records, and every control must be proposed; and the recordings must keep the owner's floor of right
# proposals. Each, failing and passing.
TRICK = """most_wrong = {most_wrong}
{accepted}
[[group]]
family = "f"
expect = "held_back"
reverses = {reverses}
said = ["Order a CBC."]
"""
TRICK_ACCEPTED = """
[accepted]
by = "the owner"
on = 2026-11-24
reason = "A known limit."
said = ["Order a CBC."]
"""


@pytest.mark.parametrize("reverses", ["true", "false"])
def test_a_wrong_trick_proposal_not_accepted_by_name_fails_and_one_accepted_passes(eval_dir, reverses):
    save(eval_dir, "latest.json", results(eval_dir))
    (eval_dir / "trick-sentences.toml").write_text(TRICK.format(most_wrong=1, reverses=reverses, accepted=""),
                                                   encoding="utf-8")

    problems = check(eval_dir / "results", eval_dir)
    (problem,) = [problem for problem in problems if "'Order a CBC.' is proposed" in problem]
    assert "hasn't accepted it by name" in problem
    assert ("reverses the meaning" in problem) == (reverses == "true")

    (eval_dir / "trick-sentences.toml").write_text(
        TRICK.format(most_wrong=1, reverses=reverses, accepted=TRICK_ACCEPTED), encoding="utf-8")
    assert check(eval_dir / "results", eval_dir) == []


def test_more_wrong_trick_proposals_than_the_set_records_fail(eval_dir):
    save(eval_dir, "latest.json", results(eval_dir))
    (eval_dir / "trick-sentences.toml").write_text(TRICK.format(most_wrong=0, reverses="true", accepted=TRICK_ACCEPTED),
                                                   encoding="utf-8")

    problems = check(eval_dir / "results", eval_dir)
    assert any("1 wrong proposals, more than the 0" in problem for problem in problems)

    (eval_dir / "trick-sentences.toml").write_text(TRICK.format(most_wrong=1, reverses="true", accepted=TRICK_ACCEPTED),
                                                   encoding="utf-8")
    assert check(eval_dir / "results", eval_dir) == []


# The acceptances hold no slack, as known-limitations.toml's entries must each still occur: a sentence accepted by name
# that is no longer proposed fails (the review's case: "Order complete.", which no rule proposes, accepted with the cap
# raised), and so does a cap that isn't the number accepted (raised alone); each passes once put right.
def test_a_sentence_accepted_by_name_and_no_longer_proposed_fails_and_one_still_proposed_passes(eval_dir):
    save(eval_dir, "latest.json", results(eval_dir))
    slack = TRICK_ACCEPTED.replace('said = ["Order a CBC."]', 'said = ["Order a CBC.", "Order complete."]')
    (eval_dir / "trick-sentences.toml").write_text(TRICK.format(most_wrong=2, reverses="true", accepted=slack),
                                                   encoding="utf-8")

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "accepts 'Order complete.' by name, and it is no longer proposed" in problem

    (eval_dir / "trick-sentences.toml").write_text(TRICK.format(most_wrong=1, reverses="true", accepted=TRICK_ACCEPTED),
                                                   encoding="utf-8")
    assert check(eval_dir / "results", eval_dir) == []


def test_a_cap_other_than_the_number_accepted_by_name_fails_and_their_number_passes(eval_dir):
    save(eval_dir, "latest.json", results(eval_dir))
    (eval_dir / "trick-sentences.toml").write_text(
        TRICK.format(most_wrong=120, reverses="true", accepted=TRICK_ACCEPTED), encoding="utf-8")

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "most_wrong = 120, and accepts 1 sentences by name" in problem

    (eval_dir / "trick-sentences.toml").write_text(TRICK.format(most_wrong=1, reverses="true", accepted=TRICK_ACCEPTED),
                                                   encoding="utf-8")
    assert check(eval_dir / "results", eval_dir) == []


# The trick sentences are pinned (TRICK_SENTENCES_SHA256): each of the review's three edits fails the gate, though each
# kept the file's own counts consistent: a sentence deleted; a tolerated sentence made a control, the cap lowered; and a
# reversing sentence added to a tolerated group, the cap raised. So does a sentence accepted by name, the cap raised.
@pytest.mark.parametrize("edit", [
    lambda text: text.replace('    "Order a CBC. Nah.",\n', "", 1),
    lambda text: text.replace("most_wrong = 96", "most_wrong = 95", 1).replace(
        'expect = "held_back"\nreverses = false\ntolerated = "everyday_item"', 'expect = "proposed"', 1),
    lambda text: text.replace("most_wrong = 96", "most_wrong = 97", 1).replace(
        '    "So, will, order a CBC.",\n', '    "So, will, order a CBC.",\n    "Order a CBC. Not that one, cancel it.",\n', 1),
    lambda text: text.replace("most_wrong = 96", "most_wrong = 97", 1).replace(
        "said = [\n", 'said = [\n    "Order a CBC. Nah.",\n', 1),
], ids=["deleted", "tolerated-made-control", "reversing-made-tolerated", "accepted-by-name"])
def test_a_trick_set_changed_without_the_gate_fails(eval_dir, edit):
    committed = (EVAL_DIR / "trick-sentences.toml").read_text(encoding="utf-8")
    changed = edit(committed)
    assert changed != committed
    (eval_dir / "trick-sentences.toml").write_text(changed, encoding="utf-8")

    problems = gate._trick_sentences(eval_dir, gate.TRICK_SENTENCES_SHA256)
    assert any("isn't the set this gate records" in problem for problem in problems)


# Proposals are counted, not sentences: a control with two instructions that loses its second fails, as does a sentence
# accepted by name that starts giving two proposals, one more than the cap; each passes as it should be.
def test_a_control_that_loses_one_of_its_instructions_fails_and_one_proposed_at_each_passes(eval_dir):
    save(eval_dir, "latest.json", results(eval_dir))
    control = 'most_wrong = 0\n\n[[group]]\nfamily = "c"\nexpect = "proposed"\nsaid = ["{said}"]\n'
    (eval_dir / "trick-sentences.toml").write_text(control.format(said="Order a CBC. Order a BMP. Nah."),
                                                   encoding="utf-8")

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "control 'Order a CBC. Order a BMP. Nah.' is not proposed at every instruction it lists" in problem

    (eval_dir / "trick-sentences.toml").write_text(control.format(said="Order a CBC. Order a BMP."), encoding="utf-8")
    assert check(eval_dir / "results", eval_dir) == []


def test_a_sentence_accepted_by_name_that_gives_two_proposals_exceeds_the_cap_and_one_that_gives_one_doesnt(eval_dir):
    save(eval_dir, "latest.json", results(eval_dir))
    two = TRICK_ACCEPTED.replace('said = ["Order a CBC."]', 'said = ["Order a CBC. Order a BMP."]')
    (eval_dir / "trick-sentences.toml").write_text(
        TRICK.format(most_wrong=1, reverses="true", accepted=two).replace(
            'said = ["Order a CBC."]', 'said = ["Order a CBC. Order a BMP."]'), encoding="utf-8")

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "2 wrong proposals, more than the 1" in problem

    (eval_dir / "trick-sentences.toml").write_text(TRICK.format(most_wrong=1, reverses="true", accepted=TRICK_ACCEPTED),
                                                   encoding="utf-8")
    assert check(eval_dir / "results", eval_dir) == []


def test_a_control_not_proposed_fails_and_one_proposed_passes(eval_dir):
    save(eval_dir, "latest.json", results(eval_dir))
    control = 'most_wrong = 0\n\n[[group]]\nfamily = "c"\nexpect = "proposed"\nsaid = ["{said}"]\n'
    (eval_dir / "trick-sentences.toml").write_text(control.format(said="Order a CBC. Nah."), encoding="utf-8")

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "control 'Order a CBC. Nah.' is not proposed" in problem

    (eval_dir / "trick-sentences.toml").write_text(control.format(said="Order a CBC."), encoding="utf-8")
    assert check(eval_dir / "results", eval_dir) == []


def test_no_trick_sentences_fail(eval_dir):
    save(eval_dir, "latest.json", results(eval_dir))
    (eval_dir / "trick-sentences.toml").unlink()

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "no trick sentences" in problem


def test_fewer_right_proposals_on_the_recordings_than_the_floor_fail_and_as_many_pass(eval_dir):
    save(eval_dir, "latest.json", results(eval_dir))

    (problem,) = check(eval_dir / "results", eval_dir, floor=2)
    assert "1 right proposals against the clinical gold, under the owner's floor of 2" in problem
    assert check(eval_dir / "results", eval_dir, floor=1) == []
    assert gate.PROPOSALS_FLOOR == 14
