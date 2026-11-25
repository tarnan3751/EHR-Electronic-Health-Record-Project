"""Tests for the false refusals (ehr_eval/refusals.py): each correct translation the Spanish checks refuse, of the gold
and of a run's answers, listed by sentence beside the fields discarded, and how a model's statement is decided."""

from __future__ import annotations

import dataclasses
import json

from dictation_scripts import load_scripts as load_english
from dictation_scripts_es import HOLDOUT_DIR, load_scripts
from ehr_dictation import translation
from ehr_eval import refusals, run_es, scoring_es
from ehr_eval.run_a import check, text_transcript

SCRIPTS = {script.id: script for script in load_scripts() + load_scripts(HOLDOUT_DIR)}
ENGLISH = {script.id: script for script in load_english()}
E15 = "e15-multiproblema"
ASPIRIN = "Toma también ácido acetilsalicílico 100 mg al día."


def checked(script_id: str, name: str, statements: list[dict]) -> scoring_es.SpanishItem:
    """The script's display text as run A reads it, with one field proposed, through the checks."""
    script = SCRIPTS[script_id]
    transcript, _ = text_transcript(script)
    item = scoring_es.SpanishItem(variant=f"{script_id}--text", script=script_id, tags=list(script.tags),
                                  hypothesis=transcript.text, sorting="done")
    check(item, {name: statements}, [], transcript)
    return item


def found(item: scoring_es.SpanishItem) -> dict:
    return refusals.measure([item], {item.variant: text_transcript(SCRIPTS[item.script])[0]}, SCRIPTS)


def test_a_correct_translation_the_checks_refuse_is_a_false_refusal_and_a_wrong_one_is_not():
    right = checked(E15, "subjective", [{"spanish": [ASPIRIN], "english": "Also takes acetylsalicylic acid 100 mg a day."}])
    wrong = checked(E15, "subjective", [{"spanish": [ASPIRIN], "english": "Also takes acetylsalicylic acid 300 mg a day."}])
    assert right.fields["subjective"].status != "pending" and wrong.fields["subjective"].status != "pending"

    (false,) = found(right)["answers"]["false"]
    assert false["english"] == "Also takes acetylsalicylic acid 100 mg a day." and false["spanish"] == [ASPIRIN]
    assert false["reasons"] and "Also takes aspirin 100 mg daily." in false["gold"]
    assert found(right)["count"] == 1
    (right_refusal,) = found(wrong)["answers"]["right"]
    assert found(wrong)["count"] == 0 and right_refusal["english"] == "Also takes acetylsalicylic acid 300 mg a day."


def test_a_statement_the_scorer_reads_nothing_in_is_undecided_and_one_citing_what_wasnt_said_is_a_right_refusal():
    unread = checked("e16-anadido-corto", "plan", [{"spanish": ["Mándale hacer una prueba de orina"],
                                                    "english": "Have her do a urine test"}])
    elsewhere = checked(E15, "subjective", [{"spanish": ["Toma también ácido acetilsalicílico 300 mg al día."],
                                             "english": "Also takes acetylsalicylic acid 300 mg a day."}])
    assert [e["verdict"] for e in found(unread)["answers"]["undecided"]] == ["undecided"]
    assert found(unread)["count"] == 0
    assert found(elsewhere)["count"] == 0 and len(found(elsewhere)["answers"]["right"]) == 1


def test_an_offered_field_has_no_refusal():
    offered = checked(E15, "subjective", [{"spanish": [ASPIRIN], "english": "Also takes aspirin 100 mg daily."}])
    assert offered.fields["subjective"].status == "pending"
    assert found(offered)["answers"]["refused"] == 0


def test_a_gold_statement_the_checks_refuse_is_a_false_refusal_of_the_gold():
    (h01,) = [script for script in SCRIPTS.values() if script.id.startswith("h01")]
    assert {(e["field"], e["english"], tuple(e["reasons"])) for e in refusals.of_gold([h01])} >= {
        ("objective", "Positive Lasègue sign in the right leg at 40 degrees.", ("translation_adds_words",))}
    test_set = [script for script in SCRIPTS.values() if script.path.parent != HOLDOUT_DIR]
    assert refusals.of_gold(test_set) == []


def test_the_report_puts_the_false_refusals_beside_the_fields_discarded():
    item = checked(E15, "subjective", [{"spanish": [ASPIRIN], "english": "Also takes acetylsalicylic acid 100 mg a day."}])
    lines = refusals.lines(scoring_es.score([item], SCRIPTS)["discarded"], found(item))
    assert lines[0].startswith("Fields with gold discarded: ") and "false refusals: 1 of the answers' 1" in lines[0]
    assert "acetylsalicylic acid 100 mg" in lines[1] and lines[1].startswith("  false: ")


def test_run_b_stores_them_for_the_recordings_and_the_texts():
    item = checked(E15, "subjective", [{"spanish": [ASPIRIN], "english": "Also takes acetylsalicylic acid 100 mg a day."}])
    metrics = run_es.measured([item], SCRIPTS, ENGLISH)
    assert metrics["text"]["false_refusals"]["count"] == 1 and metrics["false_refusals"]["count"] == 0
    assert any("acetylsalicylic acid 100 mg" in line for line in run_es.false_refusals(metrics))


def test_the_command_reports_a_stored_run_a(tmp_path, capsys):
    item = checked(E15, "subjective", [{"spanish": [ASPIRIN], "english": "Also takes acetylsalicylic acid 100 mg a day."}])
    path = tmp_path / "run-a.json"
    path.write_text(json.dumps({"dataset": {"holdout": False}, "items": [dataclasses.asdict(item)]}), encoding="utf-8")
    assert refusals.main([str(path)]) == 0
    out = capsys.readouterr().out
    assert ("false refusals: 1 of the answers' 1 statements refused (0 undecided, 0 right), 0 of the gold; 0 more "
            "statements discarded alongside a refused one; 0 refused for a hedge of leave added") in out


def test_a_value_no_gold_has_is_no_change_so_its_refusal_is_false():
    e01 = SCRIPTS["e01-hta-control"]
    quote = "Control en 4 semanas."
    at = e01.display.index(quote)
    assert refusals.verdict("In 4 weeks.", [(at, at + len(quote))], e01.display, "plan", e01) == "false"
    assert refusals.verdict("Follow-up in 5 weeks.", [(at, at + len(quote))], e01.display, "plan", e01) == "right"


def test_statements_discarded_alongside_a_refused_one_and_refusals_of_leave_are_counted():
    alongside = checked(E15, "subjective", [
        {"spanish": [ASPIRIN], "english": "Also takes aspirin 100 mg daily."},
        {"spanish": [ASPIRIN], "english": "Also takes acetylsalicylic acid 300 mg a day."}])
    assert found(alongside)["answers"]["alongside"] == 1
    leave = checked(E15, "subjective", [
        {"spanish": [ASPIRIN], "english": "Also takes acetylsalicylic acid 100 mg a day, which may be continued."}])
    (counted,) = found(leave)["answers"]["leave"]
    assert "certainty_added" in counted["reasons"]
    lines = refusals.lines(scoring_es.score([leave], SCRIPTS)["discarded"], found(leave))
    assert "1 refused for a hedge of leave added" in lines[0] and lines[1].startswith("  right: ")


def test_a_refusal_for_a_kind_the_scorer_compares_nothing_of_says_so():
    assert refusals.UNSEEN <= set(translation.REASONS)
    item = checked("e16-anadido-corto", "plan", [{"spanish": ["Evitar antiinflamatorios"],
                                                  "english": "Avoid anti-inflammatory drugs"}])
    entries = [e for kind in ("false", "undecided", "right") for e in found(item)["answers"][kind]]
    assert entries and all(e["unseen"] == ["translation_adds_words"] for e in entries)
    assert "the scorer compares nothing of translation_adds_words" in "\n".join(
        refusals.lines(scoring_es.score([item], SCRIPTS)["discarded"], found(item)))
