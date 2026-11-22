"""drugnames.py, the English drug names the Spanish dictation checks recognize (translation.py): what
src/Ehr.Dictation/write_drug_names.py writes from the reference data in the repository, and names a clinician
dictates that no USAN stem gives away."""

from __future__ import annotations

import importlib.util
from pathlib import Path

import pytest

from ehr_dictation import drugnames, translation

WRITER = Path(__file__).resolve().parents[2] / "write_drug_names.py"


def test_the_list_is_what_the_writer_makes_from_the_reference_data():
    spec = importlib.util.spec_from_file_location("write_drug_names", WRITER)
    writer = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(writer)
    if not writer.SOURCE.is_file():
        pytest.skip(f"{writer.SOURCE} isn't here (the sidecar's image has no db/reference)")
    assert writer.TARGET.read_text(encoding="utf-8") == writer.module(writer.SOURCE.read_text(encoding="utf-8")), (
        "drugnames.py is out of date: uv run src/Ehr.Dictation/write_drug_names.py"
    )


@pytest.mark.parametrize("name", ["aspirin", "digoxin", "acetaminophen", "lithium", "codeine", "insulin", "albuterol"])
def test_drugs_without_a_usan_stem_are_recognized(name):
    assert translation.is_drug(name)


@pytest.mark.parametrize("word", ["water", "sodium", "glucose", "creatinine", "pain", "chest", "fever"])
def test_results_and_ordinary_words_are_not_drugs(word):
    assert not translation.is_drug(word) and word not in drugnames.NAMES
