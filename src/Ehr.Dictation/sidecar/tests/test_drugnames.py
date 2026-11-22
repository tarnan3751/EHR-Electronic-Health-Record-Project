"""drugnames.py, the English drug names the Spanish dictation checks recognize (translation.py): what
src/Ehr.Dictation/write_drug_names.py writes from the reference data in the repository, names a clinician dictates
that no USAN stem gives away, the words of names given in several words and brand names, and not the ordinary words
of those names."""

from __future__ import annotations

import importlib.util
from pathlib import Path

import pytest

from ehr_dictation import drugnames, spanish, translation

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


@pytest.mark.parametrize("name", ["glargine", "lispro", "valproic", "folic", "tranexamic", "depakote", "tylenol",
                                  "zyrtec"])
def test_words_of_ingredients_named_in_several_and_brand_names_are_recognized(name):
    assert name in drugnames.NAMES and translation.is_drug(name)


@pytest.mark.parametrize("word", ["type", "white", "with", "daily", "once", "weight", "human", "regular", "tobacco",
                                  "hair", "kidney", "influenza", "apple", "acid", "hydrochloride", "solution", "plan"])
def test_the_other_words_of_those_names_are_not_drugs(word):
    # "adenovirus type 4 vaccine live", "white petrolatum", "once-daily gabapentin", "insulin, regular, human".
    assert word not in drugnames.NAMES and not translation.is_drug(word)


@pytest.mark.parametrize("word", ["forma", "rayos", "coronario", "íntegra", "lenta", "pedía", "retina", "valió", "allí",
                                  "colesterol", "ultrasonido", "cardiaca", "ella"])
def test_ordinary_spanish_words_are_not_drugs_in_the_spanish(word):
    # Read in Spanish, a word that a name in the lists (a Latin binomial's, a brand's) gives once its accents and ending
    # go would make the English name a drug the clinician never said (drug_dropped).
    assert not [term for term in translation._terms(spanish.tokens(word)) if term.kind == "drug"]
