"""The test set's gold against the sidecar's own checks (src/Ehr.Dictation/sidecar/ehr_dictation/validation.py): every
order or allergy-list change a script lists as forbidden is one the checks recognize in its display text, so none can
reach a field the clinician sees (FR-14)."""

from __future__ import annotations

import pytest

from ehr_dictation import validation
from ehr_eval.scripts import load_all

SCRIPTS = load_all()


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
