"""The review's wrong translations as a gated set (eval/wrong-translations.toml, ehr_eval/wrong_translations.py; the
expansion plan's step 17): the set is the one the gate records and holds what the review wrote, each pair checked as
the review's harness checked it, and the gate fails on a wrong pair that reverses the meaning and passes without a
warning unless the owner accepted it by its id, and on an accepted one that no longer passes. Small sets are written
here for the gate's rules; the committed set's pass rate is reported (python -m ehr_eval.wrong_translations), never
pinned, so the checks can get better without a test to change."""

from __future__ import annotations

import hashlib
import json
import shutil
from pathlib import Path

import pytest

from ehr_eval import gate, wrong_translations
from ehr_eval.scripts import EVAL_DIR

# Every pair's id, kind, label and text, as the review wrote them (2026-11-24): pinned here apart from the gate's hash
# of the file, so a pair marked as not reversing, relabelled or reworded fails this test even with a new
# WRONG_TRANSLATIONS_SHA256 beside it. w23, w39 and w42 are marked reversing in doubt, conservative labels that stay.
PAIRS_SHA256 = "85dff858a131edf3803fa3625c59ab248a5311434b29c8e6c9a0bee4e97311a1"
PASSES = ("Paciente atérmico.", "Febrile patient.")  # a self-negating word the checks' list doesn't hold
REFUSED = ("Suspender los opioides.", "Continue opioids.")  # a change the checks read the other way


def test_the_set_is_the_one_the_gate_records_and_holds_the_reviews_pairs():
    content = (EVAL_DIR / wrong_translations.PAIRS).read_bytes().replace(b"\r\n", b"\n")
    assert hashlib.sha256(content).hexdigest() == gate.WRONG_TRANSLATIONS_SHA256
    context, pairs = wrong_translations.load()
    wrong = [pair for pair in pairs if pair.wrong]
    faithful = [pair for pair in pairs if not pair.wrong]
    assert (len(wrong), len(faithful)) == (70, 30)
    assert [pair.id for pair in wrong] == [f"w{i:02d}" for i in range(1, 71)]
    assert [pair.id for pair in faithful] == [f"f{i:02d}" for i in range(1, 31)]
    assert {pair.kind for pair in wrong} == {"selfneg", "who", "change", "severity", "doubt", "glossary"}
    assert all(isinstance(pair.reverses, bool) for pair in wrong) and context.endswith("visita.")


def test_every_wrong_pair_is_marked_reversing_and_each_pair_is_pinned_as_written():
    _, pairs = wrong_translations.load()
    assert all(pair.reverses for pair in pairs if pair.wrong)
    assert {pair.id for pair in pairs if pair.wrong and pair.id in ("w23", "w39", "w42") and pair.reverses} == {
        "w23", "w39", "w42"}
    rows = [[pair.id, pair.wrong, pair.kind, pair.reverses, pair.spanish, pair.english] for pair in pairs]
    assert hashlib.sha256(json.dumps(rows, ensure_ascii=False).encode()).hexdigest() == PAIRS_SHA256


@pytest.mark.parametrize("change", [("reverses = true", "reverses = false"),
                                    ("Febrile patient.", "Febrile patient today.")])
def test_a_pair_relabelled_or_reworded_breaks_the_pin(tmp_path, monkeypatch, change):
    text = (EVAL_DIR / wrong_translations.PAIRS).read_text(encoding="utf-8").replace(*change, 1)
    (tmp_path / wrong_translations.PAIRS).write_text(text, encoding="utf-8")
    _, pairs = wrong_translations.load(tmp_path)
    rows = [[pair.id, pair.wrong, pair.kind, pair.reverses, pair.spanish, pair.english] for pair in pairs]
    assert hashlib.sha256(json.dumps(rows, ensure_ascii=False).encode()).hexdigest() != PAIRS_SHA256


def write_set(directory: Path, *wrong: tuple[str, str], reverses: bool = True) -> None:
    pairs = "".join(f'[[wrong]]\nid = "w{i:02d}"\nclass = "selfneg"\nspanish = "{es}"\nenglish = "{en}"\n'
                    f"reverses = {'true' if reverses else 'false'}\n\n" for i, (es, en) in enumerate(wrong, 1))
    faithful = '[[faithful]]\nid = "f01"\nspanish = "Paciente afebril."\nenglish = "Afebrile patient."\n'
    (directory / wrong_translations.PAIRS).write_text(
        f'context = "Consulta programada de seguimiento en el centro de salud."\n\n{pairs}{faithful}', encoding="utf-8")


def recorded(directory: Path) -> str:
    return hashlib.sha256((directory / wrong_translations.PAIRS).read_bytes()).hexdigest()


def test_each_pair_is_checked_as_the_reviews_harness_checked_it(tmp_path):
    write_set(tmp_path, PASSES, REFUSED)
    scores = wrong_translations.score(tmp_path)

    assert (scores.wrong, scores.faithful, scores.refused) == (2, 1, [])
    assert [(pair.spanish, pair.english) for pair in scores.passing] == [PASSES]
    assert scores.reversing == scores.passing


def test_a_reversing_pair_that_passes_fails_the_gate_unless_the_owner_accepted_it_by_id(tmp_path):
    write_set(tmp_path, PASSES, REFUSED)
    (problem,) = gate.check_wrong_translations(tmp_path, recorded(tmp_path))
    assert "w01" in problem and "reverses the meaning" in problem and "[[accepted_wrong_translation]]" in problem

    (tmp_path / gate.KNOWN_LIMITATIONS).write_text(
        '[[accepted_wrong_translation]]\nid = "w01"\nenglish = "Febrile patient."\nreason = "synthetic"\n',
        encoding="utf-8")
    assert gate.check_wrong_translations(tmp_path, recorded(tmp_path)) == []

    # Accepted, and no longer passing (or no longer marked reversing): the list must stay true.
    write_set(tmp_path, PASSES, REFUSED, reverses=False)
    (stale,) = gate.check_wrong_translations(tmp_path, recorded(tmp_path))
    assert "accepts the wrong translation w01" in stale and "Remove it" in stale


def test_a_pair_that_doesnt_reverse_the_meaning_is_reported_not_gated(tmp_path):
    write_set(tmp_path, PASSES, reverses=False)
    assert gate.check_wrong_translations(tmp_path, recorded(tmp_path)) == []
    assert len(wrong_translations.score(tmp_path).passing) == 1


@pytest.mark.parametrize("change", ["added", "missing"])
def test_a_set_other_than_the_one_recorded_fails_the_gate(tmp_path, change):
    shutil.copy(EVAL_DIR / wrong_translations.PAIRS, tmp_path)
    (tmp_path / gate.KNOWN_LIMITATIONS).write_text("", encoding="utf-8")
    if change == "added":
        with (tmp_path / wrong_translations.PAIRS).open("a", encoding="utf-8") as pairs:
            pairs.write('\n[[faithful]]\nid = "f31"\nspanish = "Paciente afebril."\nenglish = "Afebrile patient."\n')
        assert any("isn't the set this gate records" in p for p in gate.check_wrong_translations(tmp_path))
    else:
        (tmp_path / wrong_translations.PAIRS).unlink()
        (problem,) = gate.check_wrong_translations(tmp_path)
        assert "There are no wrong translations" in problem
