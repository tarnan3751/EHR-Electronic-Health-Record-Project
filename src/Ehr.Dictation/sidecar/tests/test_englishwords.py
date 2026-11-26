"""englishwords.py, the words the English checks count as English (validation.py, english_share()): what
src/Ehr.Dictation/write_english_words.py takes from SCOWL's lists, shown on a small archive of its own (SCOWL itself is
downloaded, so it isn't read here: the writer's --check does that), the clinical words it takes from the reference
data's diagnoses, checked against the repository's, the committed module in the writer's form, and SCOWL's notice
beside it."""

from __future__ import annotations

import importlib.util
import io
import tarfile
from pathlib import Path

import pytest

from ehr_dictation import englishwords

WRITER = Path(__file__).resolve().parents[2] / "write_english_words.py"


def writer():  # noqa: ANN201
    spec = importlib.util.spec_from_file_location("write_english_words", WRITER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def archive(files: dict[str, str]) -> bytes:
    """A gzipped tar of these files, each written in Latin-1, as SCOWL writes its lists."""
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w:gz") as tar:
        for name, text in files.items():
            data = text.encode("latin-1")
            member = tarfile.TarInfo(name)
            member.size = len(data)
            tar.addfile(member, io.BytesIO(data))
    return buffer.getvalue()


def test_the_writer_takes_the_lists_it_says_and_nothing_else():
    data = archive({
        "scowl/final/english-words.10": "the\ncafé\naardvark's\n",
        "scowl/final/american-words.50": "color\n",
        "scowl/final/british-words.35": "colour\n",
        "scowl/final/canadian-upper.50": "Ottawa\n",
        "scowl/final/english-contractions.35": "don't\n",
        "scowl/final/english-words.55": "rarer\n",  # past size 50
        "scowl/final/english-abbreviations.10": "PO\n",  # general abbreviations
        "scowl/final/english-proper-names.35": "Maria\n",
        "scowl/final/australian-words.10": "arvo\n",
        "scowl/misc/english-words.10": "elsewhere\n",  # not a final list
        "scowl/Copyright": "Copyright 2000-2018 by Kevin Atkinson\n\nAnd the rest, verbatim.\n",
    })
    made = writer()

    assert made.words(data) == ["café", "color", "colour", "don't", "ottawa", "the"]
    assert "Copyright 2000-2018 by Kevin Atkinson\n\nAnd the rest, verbatim.\n```" in made.notice(data)


def test_the_clinical_words_are_every_diagnosis_word_of_four_letters_or_more():
    sql = "\n".join([
        "condition\ticd-10-cm\tL03.90\tCellulitis, unspecified\t\\N",
        "condition\ticd-10-cm\tM65.4\tRadial styloid tenosynovitis [de Quervain]\t\\N",
        "condition\ticd-10-cm\tA92.5\tZika virus disease\t\\N",
        "allergen\trxnorm\t10109\tStreptomycin\tmedication",  # not a diagnosis
    ])

    assert writer().clinical(sql) == ["cellulitis", "disease", "quervain", "radial", "styloid", "tenosynovitis",
                                      "unspecified", "virus", "zika"]


def test_the_clinical_words_are_what_the_writer_reads_from_the_reference_data():
    made = writer()
    if not made.DIAGNOSES.is_file():
        pytest.skip(f"{made.DIAGNOSES} isn't here (the sidecar's image has no db/reference)")
    assert sorted(englishwords.CLINICAL) == made.clinical(made.DIAGNOSES.read_text(encoding="utf-8")), (
        "englishwords.py's clinical words are out of date: uv run src/Ehr.Dictation/write_english_words.py")


def test_the_committed_list_is_in_the_writers_form():
    made = writer()

    assert made.TARGET.read_text(encoding="utf-8") == made.module(sorted(englishwords.WORDS),
                                                                    sorted(englishwords.CLINICAL)), (
        "englishwords.py isn't what write_english_words.py writes: uv run src/Ehr.Dictation/write_english_words.py")


def test_scowls_notice_ships_with_the_list():
    notice = (Path(englishwords.__file__).parent / "LICENSE-SCOWL.md").read_text(encoding="utf-8")

    assert "Permission to use, copy, modify, distribute and sell these word" in notice
    assert "UK Advanced Cryptics Dictionary" in notice and "WordNet 1.6 Copyright 1997 by Princeton University" in notice
    assert "Copyright 2000-2018 by Kevin Atkinson" in englishwords.__doc__


@pytest.mark.parametrize("word", ["the", "i", "patient", "cough", "denies", "don't", "colour", "color", "arterial"])
def test_ordinary_english_is_there(word):
    assert word in englishwords.WORDS


@pytest.mark.parametrize("word", ["manganas", "ocasionale", "fiebre", "paciente", "aardvark's", "afaik", "da"])
def test_words_of_another_language_or_none_possessives_and_general_abbreviations_arent(word):
    assert word not in englishwords.WORDS
