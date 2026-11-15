"""normalize.py and dates.py: transcripts and references made comparable, and dates found however they came out."""

from __future__ import annotations

import pytest

from ehr_eval.dates import find, said_right
from ehr_eval.normalize import normalize
from ehr_eval.scripts import render_display, render_spoken


@pytest.mark.parametrize(("written", "spoken"), [
    ("A 54-year-old woman.", "a fifty-four year old woman"),
    ("BP 142/88.", "BP one hundred forty-two over eighty-eight"),
    ("Heart rate 105.", "heart rate one hundred and five"),
    ("Seen on the 3rd.", "seen on the third"),
    ("Rest and fluids.", "um, rest and fluids {period}"),
])
def test_written_and_spoken_forms_normalize_alike(written, spoken):
    assert normalize(written) == normalize(spoken)


def test_and_between_ordinary_words_is_kept():
    assert normalize("rest and fluids") == "rest and fluids"
    assert normalize("two and three") == "2 and 3"


@pytest.mark.parametrize("text", [
    "Seen on March 14th, 2026.",
    "seen on 3/14/2026",
    "seen on 3/14/26",
    "the fourteenth of March, twenty twenty-six",
    "march fourteen two thousand twenty six",
])
def test_a_date_is_found_however_it_was_written(text):
    assert said_right("2026-03-14", text)


def test_a_date_without_a_year_needs_only_month_and_day():
    assert said_right("--03-14", "follow up March 14")
    assert not said_right("2026-03-14", "follow up March 14")
    assert not said_right("--03-14", "follow up March 15")
    assert (None, 3, 14) in find("March 14")


def test_scripts_render_a_reference_and_what_is_said():
    text = "BP [[142/88|one forty-two over eighty-eight]] {period} Plan {colon} rest {period}"

    assert render_display(text) == "BP 142/88. Plan: rest."
    assert render_spoken(text) == "BP one forty-two over eighty-eight period Plan colon rest period"
