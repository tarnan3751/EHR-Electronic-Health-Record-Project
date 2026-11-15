"""The test set's gold against the sidecar's own checks (src/Ehr.Dictation/sidecar/ehr_dictation/validation.py): every
rule-following gold value passes them against its script's display text, read as a transcript, so it is an answer the
pipeline could give; and every order or allergy-list change a script lists as forbidden is one the checks recognize in
its display text, so none can reach a field the clinician sees (FR-14)."""

from __future__ import annotations

import pytest

from ehr_dictation import validation
from ehr_dictation.transcript import Placed, Segment, Transcript
from ehr_eval.scripts import load_all

SCRIPTS = load_all()


def transcript(text: str) -> Transcript:
    """The display text as one passage of a final transcript."""
    return Transcript(text, (Placed(Segment(0, 0, text, 1.0, None, True), 0, len(text)),), True)


@pytest.mark.parametrize("script", list(SCRIPTS.values()), ids=lambda s: s.id)
def test_every_rule_following_gold_value_passes_the_checks(script):
    fields = {name: {"value": gold.value, "sources": list(gold.sources)} for name, gold in script.gold_rules.items()}

    checks = validation.check(fields, [], transcript(script.display))

    for name, checked in checks.fields.items():
        expected = ["not_dictated"] if not fields[name]["value"] else []
        assert checked.reasons == expected, f"gold_rules.{name} would be discarded: {checked.reasons}"


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
