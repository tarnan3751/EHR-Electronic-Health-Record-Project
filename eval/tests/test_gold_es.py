"""The Spanish test set's gold against the sidecar's Spanish checks (translation.py): every script's gold, held-out ones
included, its display text read as a transcript, passes them, so it is an answer the pipeline could give, carrying only the warnings it is
meant to show (a number written two ways, "cada tercer día"); and the English prompt and schema are still the ones the
committed English results were made with."""

from __future__ import annotations

import json

import pytest

from dictation_scripts_es import HOLDOUT_DIR, load_scripts
from ehr_dictation import prompt, translation
from ehr_eval import goldreport
from ehr_eval.scripts import EVAL_DIR

SCRIPTS = load_scripts()
HOLDOUT = load_scripts(HOLDOUT_DIR)
MEANT = {translation.AMBIGUOUS_FREQUENCY}
# The one gold statement the checks refuse, on purpose: the English may say one content word more than its Spanish, the
# most the scripts' gold needs (translation.EXTRA_WORDS, the coordinator's decision), and this hold-out's "sign" is a
# second. The hold-outs are spent, so the allowance doesn't follow them. Its field is discarded, with this reason.
REFUSED = {
    ("h01-lumbalgia", "objective", "Positive Lasègue sign in the right leg at 40 degrees."): ["translation_adds_words"],
}


@pytest.mark.parametrize("script", SCRIPTS + HOLDOUT, ids=lambda s: s.id)
def test_every_gold_statement_passes_the_spanish_checks(script):
    fields = {name: [{"spanish": list(s.spanish), "english": s.english} for s in statements]
              for name, statements in script.statements.items()}

    checks = translation.check(fields, [], goldreport.transcript(script.display))

    for name, checked in checks.fields.items():
        refused = [s for s in fields[name] if (script.id, name, s["english"]) in REFUSED]
        if refused:
            # Exactly the statement named is refused, with its reason, and every other one of its field passes.
            transcript = goldreport.transcript(script.display)
            for statement in fields[name]:
                alone = translation.check({name: [statement]}, [], transcript).fields[name].reasons
                assert alone == REFUSED.get((script.id, name, statement["english"]), []), statement["english"]
            assert checked.reasons == REFUSED[(script.id, name, refused[0]["english"])]
            continue
        expected = [] if fields[name] else ["not_dictated"]
        assert checked.reasons == expected, f"gold.{name} would be discarded: {checked.reasons}"
        assert {code for code, _, _ in checked.warnings} <= MEANT, f"gold.{name}: {checked.warnings}"
        if fields[name]:
            assert checked.value == script.gold[name].value


def test_the_meant_warnings_are_shown():
    warned = set()
    for script in SCRIPTS:
        fields = {name: [{"spanish": list(s.spanish), "english": s.english} for s in statements]
                  for name, statements in script.statements.items()}
        checks = translation.check(fields, [], goldreport.transcript(script.display))
        warned |= {(script.id[:3], code) for f in checks.fields.values() for code, _, _ in f.warnings}
    assert ("e12", translation.AMBIGUOUS_FREQUENCY) in warned


def test_the_english_prompt_and_schema_are_the_committed_english_results():
    latest = json.loads((EVAL_DIR / "results" / "latest.json").read_text(encoding="utf-8"))
    english = prompt.load()
    assert latest["provenance"]["structuring"] == english.structuring_sha256
    assert (latest["provenance"]["prompt"], latest["provenance"]["schema"]) == (english.prompt_sha256, english.schema_sha256)
