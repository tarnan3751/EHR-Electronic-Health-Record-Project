"""The test set's gold against the sidecar's own checks (src/Ehr.Dictation/sidecar/ehr_dictation/validation.py): every
rule-following gold value passes them against its script's display text, read as a transcript, so it is an answer the
pipeline could give; and every order or allergy-list change a script lists as forbidden is one the checks recognize in
its display text, so none can reach a field the clinician sees (FR-14). The checks propose exactly the script's gold
proposals from its display text, each where its words are and of its kind, and hold back what it says mustn't be
proposed for the rule it names (the expansion plan's step 15), so the eval measures what speech recognition does to
them. The clinical gold values the checks would discard are listed, with why, in eval/clinical-gold.md, which must be
what ehr_eval.goldreport writes."""

from __future__ import annotations

import pytest

from ehr_dictation import validation
from ehr_eval import goldreport
from ehr_eval.scripts import load_all

SCRIPTS = load_all()


@pytest.mark.parametrize("script", list(SCRIPTS.values()), ids=lambda s: s.id)
def test_every_rule_following_gold_value_passes_the_checks(script):
    fields = {name: {"value": gold.value, "sources": list(gold.sources)} for name, gold in script.gold_rules.items()}

    checks = validation.check(fields, [], goldreport.transcript(script.display))

    for name, checked in checks.fields.items():
        expected = ["not_dictated"] if not fields[name]["value"] else []
        assert checked.reasons == expected, f"gold_rules.{name} would be discarded: {checked.reasons}"


def test_the_report_on_the_clinical_gold_is_up_to_date():
    assert goldreport.REPORT.read_text(encoding="utf-8") == goldreport.report(SCRIPTS), (
        "eval/clinical-gold.md is out of date: uv run --project eval python -m ehr_eval.goldreport"
    )


@pytest.mark.parametrize("script", [s for s in SCRIPTS.values() if s.forbidden], ids=lambda s: s.id)
def test_every_forbidden_passage_is_inside_an_instruction_the_checks_recognize(script):
    text = script.display
    spans = validation.directives(text)
    for forbidden in script.forbidden:
        at = text.find(forbidden)
        assert at >= 0, f"{script.id}: {forbidden!r} isn't in the display text"
        assert any(start <= at and at + len(forbidden) <= end for start, end, _ in spans), (
            f"{script.id}: {forbidden!r} isn't inside an instruction validation.directives() finds"
        )


@pytest.mark.parametrize("script", list(SCRIPTS.values()), ids=lambda s: s.id)
def test_the_checks_propose_exactly_the_gold_proposals(script):
    text = script.display
    proposed = [(p["kind"], text[p["start"] : p["end"]].lower()) for p in validation.proposals(goldreport.transcript(text))]

    assert len(proposed) == len(script.proposals), f"{script.id}: proposes {proposed}"
    for (kind, words), gold in zip(proposed, script.proposals, strict=True):
        assert kind == gold.kind and gold.words.lower() in words, f"{script.id}: {words!r} isn't {gold.words!r}"


@pytest.mark.parametrize("script", [s for s in SCRIPTS.values() if s.not_proposed], ids=lambda s: s.id)
def test_what_mustnt_be_proposed_is_held_back_for_the_reason_given(script):
    text = script.display
    weighed = validation.weigh(goldreport.transcript(text))
    for row in script.not_proposed:
        at = text.lower().find(row.words.lower())
        holding = [w for w in weighed if w.start <= at and at + len(row.words) <= w.end]
        if row.why == "not_an_instruction":
            assert not holding, f"{script.id}: {row.words!r} is inside an instruction the checks list"
        else:
            assert row.why in validation.HELD_BACK, f"{script.id}: {row.why} isn't a rule (validation.HELD_BACK)"
            assert [w.held_back for w in holding] == [row.why], f"{script.id}: {row.words!r} is {holding}"
